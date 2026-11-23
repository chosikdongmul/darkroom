using System.Text.Json;
using System.Text.RegularExpressions;

namespace Darkroom.Core;

public interface IDataBus
{
    /// <summary>값 게시. 키 형식은 <c>namespace.key</c>.</summary>
    void Publish(string key, object? value);
    bool TryGet(string key, out JsonElement value);
    /// <summary>구독. handler는 게시한 스레드에서 호출된다(UI 스레드가 아닐 수 있음).</summary>
    IDisposable Subscribe(string key, Action<string, JsonElement> handler);
}

public sealed partial class DataBus : IDataBus
{
    readonly object _lock = new();
    readonly Dictionary<string, JsonElement> _values = new();
    readonly Dictionary<string, List<Action<string, JsonElement>>> _subs = new();

    /// <summary>네임스페이스의 구독자 수가 0↔1 이상으로 바뀔 때. (namespace, 현재 구독자 수)</summary>
    public event Action<string, int>? NamespaceSubscribersChanged;

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9]*\.[a-zA-Z0-9.]+$")]
    private static partial Regex KeyPattern();

    public static bool IsValidKey(string key) => key.Length <= 128 && KeyPattern().IsMatch(key);

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9]*\.\*$")]
    private static partial Regex WildcardPattern();

    /// <summary>manifest subscribe 항목: 키 또는 네임스페이스 전체("system.*")</summary>
    public static bool IsValidSubscription(string s) => IsValidKey(s) || WildcardPattern().IsMatch(s);
    public static string NamespaceOf(string key) => key[..key.IndexOf('.')];

    public void Publish(string key, object? value)
    {
        if (!IsValidKey(key)) throw new ArgumentException($"잘못된 키: {key}");
        var element = value is JsonElement je ? je : DrJson.ToElement(value);
        Action<string, JsonElement>[] handlers;
        lock (_lock)
        {
            // 값이 그대로면 알리지 않는다(위젯을 깨우지 않음 → CPU 절약).
            if (_values.TryGetValue(key, out var prev) && prev.GetRawText() == element.GetRawText()) return;
            _values[key] = element;
            handlers = _subs.TryGetValue(key, out var list) ? list.ToArray() : [];
        }
        foreach (var h in handlers)
        {
            try { h(key, element); }
            catch (Exception ex) { Log.Error($"구독자 오류 ({key})", ex); }
        }
    }

    public bool TryGet(string key, out JsonElement value)
    {
        lock (_lock) return _values.TryGetValue(key, out value);
    }

    public IDisposable Subscribe(string key, Action<string, JsonElement> handler)
    {
        if (!IsValidKey(key)) throw new ArgumentException($"잘못된 키: {key}");
        var ns = NamespaceOf(key);
        int count;
        lock (_lock)
        {
            if (!_subs.TryGetValue(key, out var list)) _subs[key] = list = new();
            list.Add(handler);
            count = CountNamespace(ns);
        }
        if (count == 1) NamespaceSubscribersChanged?.Invoke(ns, count);
        return new Subscription(this, key, handler);
    }

    public int SubscriberCount(string ns)
    {
        lock (_lock) return CountNamespace(ns);
    }

    int CountNamespace(string ns)
    {
        int n = 0;
        foreach (var (k, list) in _subs)
            if (k.Length > ns.Length && k[ns.Length] == '.' && k.StartsWith(ns, StringComparison.Ordinal)) n += list.Count;
        return n;
    }

    void Unsubscribe(string key, Action<string, JsonElement> handler)
    {
        var ns = NamespaceOf(key);
        int count;
        lock (_lock)
        {
            if (!_subs.TryGetValue(key, out var list) || !list.Remove(handler)) return;
            if (list.Count == 0) _subs.Remove(key);
            count = CountNamespace(ns);
        }
        if (count == 0) NamespaceSubscribersChanged?.Invoke(ns, 0);
    }

    sealed class Subscription(DataBus bus, string key, Action<string, JsonElement> handler) : IDisposable
    {
        int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) bus.Unsubscribe(key, handler);
        }
    }
}
