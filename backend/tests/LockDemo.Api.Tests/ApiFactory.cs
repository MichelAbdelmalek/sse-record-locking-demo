using LockDemo.Api.Data;
using LockDemo.Api.Events;
using LockDemo.Api.Locking;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace LockDemo.Api.Tests;

/// <summary>Runs the real API against a throwaway Postgres container.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async Task InitializeAsync() => await _db.StartAsync();
    public new async Task DisposeAsync() { await base.DisposeAsync(); await _db.DisposeAsync(); }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _db.GetConnectionString());
        builder.UseSetting("Locks:TtlSeconds", "30");
    }

    /// <summary>Stands in for a browser tab with an open SSE stream.</summary>
    public async Task<TestTab> ConnectTabAsync(string name)
    {
        var tabId = Guid.NewGuid();
        await using var scope = Services.CreateAsyncScope();
        var (publicId, _) = await scope.ServiceProvider.GetRequiredService<TabSessionService>()
            .ConnectAsync(tabId, name, CancellationToken.None);
        var conn = Services.GetRequiredService<TabRegistry>().Register(tabId, publicId, name);
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Tab-Id", tabId.ToString());
        return new TestTab(tabId, publicId, conn, client);
    }

    /// <summary>Makes a tab look like it vanished without saying goodbye (crashed browser).</summary>
    public async Task ExpireAsync(TestTab tab)
    {
        Services.GetRequiredService<TabRegistry>().Unregister(tab.Connection);
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.TabSessions.Where(s => s.TabId == tab.TabId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeen, DateTimeOffset.UtcNow.AddMinutes(-5)));
    }
}

public sealed record TestTab(Guid TabId, Guid PublicId, TabConnection Connection, HttpClient Client)
{
    /// <summary>Events this tab has received so far.</summary>
    public List<LockEvent> Drain()
    {
        var list = new List<LockEvent>();
        while (Connection.Outbox.Reader.TryRead(out var evt)) list.Add(evt);
        return list;
    }
}
