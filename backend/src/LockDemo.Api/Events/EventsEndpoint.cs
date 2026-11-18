using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LockDemo.Api.Locking;
using LockDemo.Api.Records;

namespace LockDemo.Api.Events;

public static class EventsEndpoint
{
    /// <summary>
    /// GET /api/events?tabId=..&amp;name=..
    /// One SSE stream per browser tab. While it is open the tab counts as connected,
    /// which is what keeps its locks valid. EventSource cannot send headers, so the
    /// per-tab id comes in the query string; it is a random id, not a credential.
    /// </summary>
    public static void MapEvents(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/events", IResult (Guid tabId, string? name, HttpContext http, TabRegistry registry,
                IServiceScopeFactory scopes, ILoggerFactory logs, CancellationToken ct) =>
            {
                var displayName = Validation.CleanName(name);
                if (tabId == Guid.Empty || displayName is null)
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                        { ["name"] = ["Use 1 to 40 characters."], ["tabId"] = ["Required."] });

                http.Response.Headers["X-Accel-Buffering"] = "no"; // stop proxies from buffering the stream
                return TypedResults.ServerSentEvents(
                    Stream(tabId, displayName, registry, scopes, logs.CreateLogger("Events"), ct));
            })
            .RequireRateLimiting("streams");
    }

    private static async IAsyncEnumerable<SseItem<JsonElement>> Stream(
        Guid tabId, string name, TabRegistry registry, IServiceScopeFactory scopes, ILogger log,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Guid publicId; DateTimeOffset connectedAt;
        await using (var scope = scopes.CreateAsyncScope())
            (publicId, connectedAt) = await scope.ServiceProvider.GetRequiredService<TabSessionService>()
                .ConnectAsync(tabId, name, ct);

        var conn = registry.Register(tabId, publicId, name);
        log.LogInformation("Tab connected {PublicId}", publicId);
        try
        {
            yield return Item("hello", new { publicId, name });
            while (!ct.IsCancellationRequested)
            {
                LockEvent? evt = null;
                using (var tick = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    // A ping every 15 s keeps idle connections open through proxies and load balancers.
                    tick.CancelAfter(TimeSpan.FromSeconds(15));
                    try { evt = await conn.Outbox.Reader.ReadAsync(tick.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
                if (ct.IsCancellationRequested) yield break;
                yield return evt is null
                    ? Item("ping", new { })
                    : new SseItem<JsonElement>(
                        JsonSerializer.SerializeToElement(new { recordId = evt.RecordId, data = evt.Data }, JsonSerializerOptions.Web),
                        evt.Type);
            }
        }
        finally
        {
            if (registry.Unregister(conn))
                _ = EndAfterGraceAsync(tabId, connectedAt, registry, scopes, log);
        }
    }

    /// <summary>A short grace period lets a tab that blinked offline reconnect without losing its locks.</summary>
    private static async Task EndAfterGraceAsync(Guid tabId, DateTimeOffset connectedAt, TabRegistry registry,
        IServiceScopeFactory scopes, ILogger log)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (registry.IsConnected(tabId)) return;
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TabSessionService>()
                .DisconnectAsync(tabId, connectedAt, CancellationToken.None);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not end tab session"); }
    }

    private static SseItem<JsonElement> Item(string type, object data) =>
        new(JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web), type);
}
