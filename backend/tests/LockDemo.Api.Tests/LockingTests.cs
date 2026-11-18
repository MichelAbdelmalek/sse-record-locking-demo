using System.Net;
using System.Net.Http.Json;
using LockDemo.Api.Events;
using LockDemo.Api.Locking;
using LockDemo.Api.Records;

namespace LockDemo.Api.Tests;

public sealed class LockingTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = System.Text.Json.JsonSerializerOptions.Web;

    private async Task<RecordDto> FirstFreeRecordAsync()
    {
        var anon = api.CreateClient();
        var rec = await anon.GetFromJsonAsync<RecordDto>("/api/records/next-unlocked", Json);
        Assert.NotNull(rec);
        return rec!;
    }

    private static Task<HttpResponseMessage> LockAsync(TestTab tab, int id, bool takeover = false) =>
        tab.Client.PostAsJsonAsync($"/api/records/{id}/lock", new LockRequest(takeover));

    private static Task<HttpResponseMessage> SaveAsync(TestTab tab, RecordDto rec, string title) =>
        tab.Client.PutAsJsonAsync($"/api/records/{rec.Id}", new RecordUpdate(title, "InReview", "checked", rec.Version));

    [Fact]
    public async Task A_live_lock_is_exclusive()
    {
        var alice = await api.ConnectTabAsync("Alice");
        var bob = await api.ConnectTabAsync("Bob");
        var rec = await FirstFreeRecordAsync();

        Assert.Equal(HttpStatusCode.OK, (await LockAsync(alice, rec.Id)).StatusCode);
        var denied = await LockAsync(bob, rec.Id);

        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Contains("Alice", await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Takeover_warns_the_displaced_tab_and_rejects_its_saves()
    {
        var alice = await api.ConnectTabAsync("Alice");
        var bob = await api.ConnectTabAsync("Bob");
        var rec = await FirstFreeRecordAsync();
        await LockAsync(alice, rec.Id);
        alice.Drain();

        Assert.Equal(HttpStatusCode.OK, (await LockAsync(bob, rec.Id, takeover: true)).StatusCode);

        var lost = Assert.Single(alice.Drain(), e => e.Type == LockEvent.LockLost);
        Assert.Equal(rec.Id, lost.RecordId);
        Assert.Equal(HttpStatusCode.Locked, (await SaveAsync(alice, rec, "Alice was here")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(bob, rec, "Bob was here")).StatusCode);
    }

    [Fact]
    public async Task A_lock_whose_holder_disconnected_can_be_taken_without_takeover()
    {
        var alice = await api.ConnectTabAsync("Alice");
        var bob = await api.ConnectTabAsync("Bob");
        var rec = await FirstFreeRecordAsync();
        await LockAsync(alice, rec.Id);

        await api.ExpireAsync(alice);

        Assert.Equal(HttpStatusCode.OK, (await LockAsync(bob, rec.Id)).StatusCode);
    }

    [Fact]
    public async Task Next_unlocked_skips_records_held_by_live_tabs()
    {
        var alice = await api.ConnectTabAsync("Alice");
        var first = await FirstFreeRecordAsync();
        await LockAsync(alice, first.Id);

        var next = await FirstFreeRecordAsync();

        Assert.NotEqual(first.Id, next.Id);
        Assert.True(next.Priority > first.Priority || (next.Priority == first.Priority && next.Id > first.Id));
    }

    [Fact]
    public async Task Saving_without_the_lock_is_rejected()
    {
        var alice = await api.ConnectTabAsync("Alice");
        var rec = await FirstFreeRecordAsync();

        Assert.Equal(HttpStatusCode.Locked, (await SaveAsync(alice, rec, "no lock")).StatusCode);
    }

    [Fact]
    public async Task Saving_an_outdated_version_returns_the_latest_record()
    {
        var alice = await api.ConnectTabAsync("Alice");
        var rec = await FirstFreeRecordAsync();
        await LockAsync(alice, rec.Id);
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(alice, rec, "first save")).StatusCode);

        var stale = await SaveAsync(alice, rec, "second save with old version");

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("first save", await stale.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Taking_a_lock_requires_an_open_event_stream()
    {
        var rec = await FirstFreeRecordAsync();
        var stranger = api.CreateClient();
        stranger.DefaultRequestHeaders.Add("X-Tab-Id", Guid.NewGuid().ToString());

        var res = await stranger.PostAsJsonAsync($"/api/records/{rec.Id}/lock", new LockRequest(false));

        Assert.Equal(HttpStatusCode.PreconditionRequired, res.StatusCode);
    }

    [Fact]
    public async Task The_event_stream_greets_the_tab_with_its_public_id()
    {
        var client = api.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var res = await client.GetAsync($"/api/events?tabId={Guid.NewGuid()}&name=Carol",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
        Assert.Equal("event: hello", await reader.ReadLineAsync(cts.Token));
        Assert.Contains("publicId", await reader.ReadLineAsync(cts.Token));
    }

    [Fact]
    public async Task Records_page_with_a_keyset_cursor()
    {
        var client = api.CreateClient();
        var first = await client.GetFromJsonAsync<RecordPage>("/api/records?limit=10", Json);
        Assert.Equal(10, first!.Items.Count);
        Assert.NotNull(first.Next);

        var second = await client.GetFromJsonAsync<RecordPage>(
            $"/api/records?limit=10&afterPriority={first.Next!.Priority}&afterId={first.Next.Id}", Json);

        Assert.Empty(first.Items.Select(r => r.Id).Intersect(second!.Items.Select(r => r.Id)));
    }
}
