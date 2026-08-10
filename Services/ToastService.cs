using System.Collections.Concurrent;

namespace YuSwitch.Services;

/// <summary>
/// Lightweight transient toast notifications for the Blazor UI. Components
/// subscribe to <see cref="Changed"/> and render the active toasts; each toast
/// auto-expires. Replaces blocking alert() calls for a smoother UX.
/// </summary>
public class ToastService
{
    public record Toast(int Id, string Kind, string Message, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<int, Toast> _toasts = new();
    private int _nextId;

    public event Action? Changed;

    public IReadOnlyCollection<Toast> Active =>
        _toasts.Values.Where(t => t.ExpiresAt > DateTime.Now).ToList();

    public void Ok(string message) => Add("ok", message);
    public void Error(string message) => Add("err", message);
    public void Info(string message) => Add("info", message);

    public void Add(string kind, string message, int seconds = 4)
    {
        var id = Interlocked.Increment(ref _nextId);
        _toasts[id] = new Toast(id, kind, message, DateTime.Now.AddSeconds(seconds));
        Changed?.Invoke();
        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ =>
        {
            if (_toasts.TryRemove(id, out var _))
                Changed?.Invoke();
        });
    }

    public void Dismiss(int id)
    {
        if (_toasts.TryRemove(id, out _))
            Changed?.Invoke();
    }
}
