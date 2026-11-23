namespace Darkroom.Core;

public interface IDataProvider
{
    /// <summary>예: "system". 게시하는 키는 모두 이 네임스페이스로 시작해야 한다.</summary>
    string Namespace { get; }
    /// <summary>갱신 주기. 매 주기마다 다시 읽으므로 설정에 따라 바뀌어도 된다.</summary>
    TimeSpan Interval { get; }
    Task UpdateAsync(IDataBus bus, CancellationToken ct);
}

/// <summary>Provider 실행기. 네임스페이스에 구독자가 있을 때만 Provider를 돌린다(없으면 멈춤).</summary>
public sealed class ProviderHost : IDisposable
{
    readonly DataBus _bus;
    readonly Dictionary<string, IDataProvider> _providers;
    readonly Dictionary<string, Runner> _running = new();
    readonly object _lock = new();

    sealed class Runner
    {
        public readonly CancellationTokenSource Cts = new();
        TaskCompletionSource _kick = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task KickTask { get { lock (this) return _kick.Task; } }
        public void Kick() { lock (this) { _kick.TrySetResult(); _kick = new(TaskCreationOptions.RunContinuationsAsynchronously); } }
    }

    public ProviderHost(DataBus bus, IEnumerable<IDataProvider> providers)
    {
        _bus = bus;
        _providers = providers.ToDictionary(p => p.Namespace);
        _bus.NamespaceSubscribersChanged += OnSubscribersChanged;
    }

    public IReadOnlyCollection<string> Running { get { lock (_lock) return _running.Keys.ToArray(); } }

    public T? Get<T>() where T : class, IDataProvider => _providers.Values.OfType<T>().FirstOrDefault();

    /// <summary>설정이 바뀌었을 때: 기다리지 말고 바로 다시 갱신.</summary>
    public void Kick(string ns)
    {
        lock (_lock) if (_running.TryGetValue(ns, out var r)) r.Kick();
    }

    void OnSubscribersChanged(string ns, int count)
    {
        if (!_providers.TryGetValue(ns, out var provider)) return;
        lock (_lock)
        {
            if (count > 0 && !_running.ContainsKey(ns))
            {
                var runner = new Runner();
                _running[ns] = runner;
                _ = Task.Run(() => RunAsync(provider, runner));
                Log.Info($"provider '{ns}' 시작");
            }
            else if (count == 0 && _running.Remove(ns, out var runner))
            {
                runner.Cts.Cancel();
                Log.Info($"provider '{ns}' 정지 (구독자 없음)");
            }
        }
    }

    async Task RunAsync(IDataProvider p, Runner runner)
    {
        var ct = runner.Cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var kick = runner.KickTask;
            try { await p.UpdateAsync(_bus, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Error($"provider '{p.Namespace}' 업데이트 실패", ex); }

            // 주기 경계에 맞춘다(1초 주기 → 매 초 시작 직후). 시계가 초 단위로 정확히 넘어가게.
            long intervalMs = Math.Max(50, (long)p.Interval.TotalMilliseconds);
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            long delay = intervalMs - (now % intervalMs) + 5;
            try { await Task.WhenAny(Task.Delay(TimeSpan.FromMilliseconds(delay), ct), kick); }
            catch (OperationCanceledException) { break; }
        }
        runner.Cts.Dispose();
    }

    public void Dispose()
    {
        _bus.NamespaceSubscribersChanged -= OnSubscribersChanged;
        lock (_lock)
        {
            foreach (var r in _running.Values) r.Cts.Cancel();
            _running.Clear();
        }
    }
}
