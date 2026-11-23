using Darkroom.Core;

namespace Darkroom.App;

/// <summary>폴더 감시 + 300ms 디바운스. 마지막 변경 후 300ms 동안 조용하면 바뀐 경로 묶음을 한 번에 넘긴다.</summary>
internal sealed class HotReloader : IDisposable
{
    readonly List<FileSystemWatcher> _watchers = new();
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Timer _timer;
    readonly Action<IReadOnlyCollection<string>> _onBatch;
    readonly object _lock = new();

    public HotReloader(IEnumerable<string> folders, Action<IReadOnlyCollection<string>> onBatch)
    {
        _onBatch = onBatch;
        _timer = new Timer(_ => Flush());
        foreach (var dir in folders)
        {
            if (!Directory.Exists(dir)) { Log.Warn($"감시 폴더 없음: {dir}"); continue; }
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Changed += (_, e) => Enqueue(e.FullPath);
            w.Created += (_, e) => Enqueue(e.FullPath);
            w.Deleted += (_, e) => Enqueue(e.FullPath);
            w.Renamed += (_, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
            w.Error += (_, e) => { Log.Warn($"파일 감시 오류({dir}): {e.GetException().Message}"); Enqueue(dir); };
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
    }

    /// <summary>편집기가 만드는 임시 파일은 무시.</summary>
    static bool Ignored(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith('~') || name.StartsWith(".#") || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase) || name == "4913";
    }

    void Enqueue(string path)
    {
        if (Ignored(path)) return;
        lock (_lock)
        {
            _pending.Add(path);
            _timer.Change(300, Timeout.Infinite);
        }
    }

    void Flush()
    {
        string[] batch;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
        }
        _onBatch(batch);
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _timer.Dispose();
    }
}
