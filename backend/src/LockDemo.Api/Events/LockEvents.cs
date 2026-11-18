using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace LockDemo.Api.Events;

/// <summary>
/// An event pushed to browser tabs over SSE.
/// TargetTabId = null means every tab; otherwise only that tab receives it.
/// </summary>
public sealed record LockEvent(string Type, int RecordId, Guid? TargetTabId, JsonElement Data)
{
    public const string LockChanged = "lock-changed";   // Data: LockDto or null
    public const string LockLost = "lock-lost";         // Data: { byName }, sent to the displaced tab
    public const string RecordUpdated = "record-updated"; // Data: RecordDto

    public static LockEvent Create<T>(string type, int recordId, T data, Guid? target = null) =>
        new(type, recordId, target, JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web));
}

/// <summary>A tab connected to THIS instance, with its own outgoing queue.</summary>
public sealed class TabConnection(Guid tabId, Guid publicId, string name)
{
    public Guid TabId { get; } = tabId;
    public Guid PublicId { get; } = publicId;
    public string Name { get; } = name;

    // Bounded so one slow tab cannot grow memory without limit; oldest events drop first.
    public Channel<LockEvent> Outbox { get; } = Channel.CreateBounded<LockEvent>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
}

/// <summary>Tabs whose SSE stream is open on this instance.</summary>
public sealed class TabRegistry
{
    private readonly ConcurrentDictionary<Guid, TabConnection> _tabs = new();

    /// <summary>
    /// Registers the tab's stream. An EventSource reconnect reuses the same tab id and
    /// replaces the old connection; the public id comes from the session row so it stays stable.
    /// </summary>
    public TabConnection Register(Guid tabId, Guid publicId, string name)
    {
        var conn = new TabConnection(tabId, publicId, name);
        _tabs[tabId] = conn;
        return conn;
    }

    /// <summary>Removes the tab only if this exact connection is still the current one.</summary>
    public bool Unregister(TabConnection conn) =>
        _tabs.TryRemove(new KeyValuePair<Guid, TabConnection>(conn.TabId, conn));

    public bool IsConnected(Guid tabId) => _tabs.ContainsKey(tabId);
    public Guid[] ConnectedTabIds() => _tabs.Keys.ToArray();

    /// <summary>Delivers an event to the tabs on this instance that should get it.</summary>
    public void Dispatch(LockEvent evt)
    {
        if (evt.TargetTabId is { } target)
        {
            if (_tabs.TryGetValue(target, out var one)) one.Outbox.Writer.TryWrite(evt);
            return;
        }
        foreach (var conn in _tabs.Values) conn.Outbox.Writer.TryWrite(evt);
    }
}

public interface ILockEventBus
{
    Task PublishAsync(LockEvent evt, CancellationToken ct = default);
}

/// <summary>Single instance: deliver straight to the local tabs.</summary>
public sealed class InMemoryLockEventBus(TabRegistry registry) : ILockEventBus
{
    public Task PublishAsync(LockEvent evt, CancellationToken ct = default)
    {
        registry.Dispatch(evt);
        return Task.CompletedTask;
    }
}
