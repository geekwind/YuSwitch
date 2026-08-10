using System.Collections.Concurrent;

namespace YuSwitch.Services;

/// <summary>
/// Notification context that carries optional metadata about the event.
/// Allows pages to make intelligent decisions about whether to refresh.
/// </summary>
public class NotificationContext
{
    public string? Model { get; set; }
    public string? Service { get; set; }
    public string? Provider { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// Cross-tab sync event: when a user changes filters/auto-refresh in one tab,
/// the same event is broadcast to all tabs sharing the same user key.
/// </summary>
public class SyncEvent
{
    public string EventType { get; set; } = "";       // "filter" | "autoRefresh" | "navigate"
    public string Page { get; set; } = "";             // "home" | "logs" | "usage"
    public string? FilterJson { get; set; }            // serialized filter values
    public bool? AutoRefresh { get; set; }             // auto-refresh toggle state
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Enhanced real-time notification service with smart filtering capabilities
/// and cross-tab synchronization.
/// </summary>
public class RealtimeNotificationService
{
    private readonly ILogger<RealtimeNotificationService>? _log;

    public RealtimeNotificationService(ILogger<RealtimeNotificationService>? log = null) => _log = log;

    private readonly ConcurrentDictionary<string, HashSet<string>> _subscriptions = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Action<NotificationContext?>>> _callbacks = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastBroadcastTime = new();
    private readonly ConcurrentDictionary<string, System.Threading.Timer?> _debounceTimers = new();

    // Cross-tab sync: user key -> set of connection IDs (tabs) for that user
    private readonly ConcurrentDictionary<string, HashSet<string>> _userConnections = new();
    // Cross-tab sync: connection ID -> user key (reverse lookup)
    private readonly ConcurrentDictionary<string, string> _connectionUser = new();
    // Cross-tab sync callbacks: user key -> callback
    private readonly ConcurrentDictionary<string, Action<SyncEvent>> _syncCallbacks = new();

    private const int DefaultDebounceMs = 500;

    /// <summary>
    /// Associate a connection with a user key for cross-tab sync.
    /// Multiple tabs (connections) sharing the same user key will receive
    /// each other's sync events.
    /// </summary>
    public void RegisterUserConnection(string connectionId, string userKey)
    {
        if (string.IsNullOrEmpty(connectionId) || string.IsNullOrEmpty(userKey))
            return;

        _connectionUser[connectionId] = userKey;
        _userConnections.AddOrUpdate(userKey,
            new HashSet<string> { connectionId },
            (key, existing) =>
            {
                lock (existing) { existing.Add(connectionId); }
                return existing;
            });
    }

    /// <summary>
    /// Unregister a connection from cross-tab sync (on dispose).
    /// </summary>
    public void UnregisterUserConnection(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId))
            return;

        if (_connectionUser.TryRemove(connectionId, out var userKey))
        {
            if (_userConnections.TryGetValue(userKey, out var conns))
            {
                lock (conns) { conns.Remove(connectionId); }
                if (conns.Count == 0)
                {
                    _userConnections.TryRemove(userKey, out _);
                    _syncCallbacks.TryRemove(userKey, out _);
                }
            }
        }
    }

    /// <summary>
    /// Register a sync callback for a user key. When BroadcastSync is called
    /// with the same user key, this callback will be invoked on all tabs.
    /// </summary>
    public void RegisterSyncCallback(string userKey, Action<SyncEvent> callback)
    {
        if (string.IsNullOrEmpty(userKey))
            return;
        _syncCallbacks[userKey] = callback;
    }

    /// <summary>
    /// Broadcast a sync event to all tabs sharing the same user key.
    /// </summary>
    public void BroadcastSync(string userKey, SyncEvent evt)
    {
        if (string.IsNullOrEmpty(userKey) || evt is null)
            return;

        if (_syncCallbacks.TryGetValue(userKey, out var cb))
        {
            try { cb.Invoke(evt); }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Sync callback error");
            }
        }
    }

    public void Subscribe(string connectionId, string messageType, Action<NotificationContext?>? callback = null)
    {
        if (string.IsNullOrEmpty(connectionId) || string.IsNullOrEmpty(messageType))
            return;

        _subscriptions.AddOrUpdate(connectionId,
            new HashSet<string> { messageType },
            (key, existing) =>
            {
                lock (existing) { existing.Add(messageType); }
                return existing;
            });

        if (callback != null)
        {
            _callbacks.AddOrUpdate(messageType,
                new ConcurrentDictionary<string, Action<NotificationContext?>>(StringComparer.Ordinal),
                (key, existing) => existing);

            _callbacks[messageType].AddOrUpdate(connectionId, callback, (k, v) => callback);
        }
    }

    public void Unsubscribe(string connectionId, string messageType)
    {
        if (string.IsNullOrEmpty(connectionId))
            return;

        if (_subscriptions.TryGetValue(connectionId, out var types))
        {
            lock (types) { types.Remove(messageType); }
        }

        if (!string.IsNullOrEmpty(messageType) && _callbacks.TryGetValue(messageType, out var cbs))
        {
            cbs.TryRemove(connectionId, out _);
        }
    }

    public void UnsubscribeAll(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId))
            return;

        if (_subscriptions.TryRemove(connectionId, out var types))
        {
            lock (types)
            {
                foreach (var messageType in types)
                {
                    if (_callbacks.TryGetValue(messageType, out var cbs))
                    {
                        cbs.TryRemove(connectionId, out _);
                    }
                }
            }
        }

        // Also clean up cross-tab sync registration
        UnregisterUserConnection(connectionId);
    }

    /// <summary>
    /// Broadcast with context for smart filtering.
    /// </summary>
    public void Broadcast(string messageType, NotificationContext? context = null, int debounceMs = DefaultDebounceMs)
    {
        if (string.IsNullOrEmpty(messageType))
            return;

        _lastBroadcastTime.AddOrUpdate(messageType, DateTime.UtcNow, (k, v) => DateTime.UtcNow);

        if (_debounceTimers.TryGetValue(messageType, out var existingTimer))
        {
            existingTimer?.Dispose();
        }

        var capturedContext = context;
        var timer = new System.Threading.Timer(_ =>
        {
            if (_lastBroadcastTime.TryGetValue(messageType, out var lastTime))
            {
                var elapsed = (DateTime.UtcNow - lastTime).TotalMilliseconds;
                if (elapsed >= debounceMs - 50)
                {
                    InvokeCallbacks(messageType, capturedContext);
                }
            }
        }, null, debounceMs, System.Threading.Timeout.Infinite);

        _debounceTimers.AddOrUpdate(messageType, timer, (k, v) => { v?.Dispose(); return timer; });
    }

    /// <summary>
    /// Broadcast immediately with context (no debouncing).
    /// </summary>
    public void BroadcastImmediate(string messageType, NotificationContext? context = null)
    {
        if (string.IsNullOrEmpty(messageType))
            return;
        InvokeCallbacks(messageType, context);
    }

    private void InvokeCallbacks(string messageType, NotificationContext? context)
    {
        if (_callbacks.TryGetValue(messageType, out var callbacks))
        {
            foreach (var cb in callbacks)
            {
                try
                {
                    cb.Value?.Invoke(context);
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "Notification callback error");
                }
            }
        }
    }

    public bool IsSubscribed(string connectionId, string messageType)
    {
        if (string.IsNullOrEmpty(connectionId) || string.IsNullOrEmpty(messageType))
            return false;

        return _subscriptions.TryGetValue(connectionId, out var types) &&
               types.Contains(messageType);
    }

    public int SubscriberCount(string messageType)
    {
        if (string.IsNullOrEmpty(messageType))
            return 0;

        return _subscriptions.Values.Count(types =>
        {
            lock (types) { return types.Contains(messageType); }
        });
    }

    public DateTime? GetLastBroadcastTime(string messageType)
    {
        if (string.IsNullOrEmpty(messageType))
            return null;

        return _lastBroadcastTime.TryGetValue(messageType, out var time) ? time : null;
    }

    /// <summary>
    /// Get all message types and their subscriber counts (for admin/debug page).
    /// </summary>
    public Dictionary<string, int> GetAllSubscriberCounts()
    {
        var result = new Dictionary<string, int>();
        foreach (var cb in _callbacks)
        {
            result[cb.Key] = cb.Value.Count;
        }
        return result;
    }

    /// <summary>
    /// Get all user keys and their connection counts (for admin/debug page).
    /// </summary>
    public Dictionary<string, int> GetUserConnectionCounts()
    {
        var result = new Dictionary<string, int>();
        foreach (var kv in _userConnections)
        {
            lock (kv.Value)
            {
                result[kv.Key] = kv.Value.Count;
            }
        }
        return result;
    }
}
