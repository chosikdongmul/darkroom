using System.Runtime.InteropServices;
using System.Windows.Threading;
using Darkroom.Core;
using Darkroom.Providers;

namespace Darkroom.App;

/// <summary>Windows 배경화면 COM 인터페이스(shobjidl_core.h). 메서드 순서가 vtable 순서라 바꾸면 안 된다.</summary>
[ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
    [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
    [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
    uint GetMonitorDevicePathCount();
    void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out Win32.Native.RECT rect);
    void SetBackgroundColor(uint color);
    uint GetBackgroundColor();
    void SetPosition(int position);
    int GetPosition();
    void SetSlideshow(IntPtr items);
    IntPtr GetSlideshow();
    void SetSlideshowOptions(uint options, uint slideshowTick);
    void GetSlideshowOptions(out uint options, out uint slideshowTick);
    void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, int direction);
    uint GetStatus();
    void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
}

[ComImport, Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
internal class DesktopWallpaperCoClass { }

/// <summary>
/// 배경화면 바꾸기: 사진 폴더에서 섞은 순서로 골라 정해진 간격마다 Windows 배경화면을 바꾼다.
/// 켤 때 원래 배경화면을 기억하고, 끄면 되돌린다. 긴 변 1200px 미만 사진(작은 캡처 등)은 건너뛴다.
/// </summary>
internal sealed class WallpaperRotator : IDisposable
{
    const int MinEdge = 1200;
    static readonly TimeSpan Rescan = TimeSpan.FromMinutes(10);
    static readonly Dictionary<string, int> Positions = new() { ["center"] = 0, ["stretch"] = 2, ["fit"] = 3, ["fill"] = 4 };

    readonly AppController _app;
    readonly DispatcherTimer _timer;
    List<string> _order = new();
    int _index = -1;
    string? _folder;
    DateTimeOffset _scannedAt;

    public string? CurrentName { get; private set; }
    public int Count => _order.Count;
    public string? LastError { get; private set; }

    public WallpaperRotator(AppController app, Dispatcher dispatcher)
    {
        _app = app;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += (_, _) => Next();
    }

    WallpaperSettings S => _app.State.Wallpaper;

    static IDesktopWallpaper Api() => (IDesktopWallpaper)new DesktopWallpaperCoClass();

    /// <summary>설정에 맞춰 켜기/끄기·타이머를 맞춘다. 시작할 때와 설정이 바뀔 때 부른다.</summary>
    public void Apply(bool changeNow = false)
    {
        var s = S;
        if (!s.Enabled)
        {
            _timer.Stop();
            Restore();
            return;
        }
        try
        {
            var api = Api();
            if (s.OriginalPath == null)
            {
                s.OriginalPath = api.GetWallpaper(null) ?? "";
                s.OriginalPosition = api.GetPosition();
                Log.Info($"배경화면 바꾸기 켬. 원래 배경화면 기억: {s.OriginalPath} (위치 {s.OriginalPosition})");
            }
            api.SetPosition(Positions.GetValueOrDefault(s.Fit, 4));
        }
        catch (COMException ex) { LastError = ex.Message; Log.Error("배경화면 API 실패", ex); return; }

        // 바로 바꾸지 않을 때도 후보 수·지금 사진 이름은 보이게(재시작 직후 "후보 0장"으로 보이지 않도록)
        if (_folder != _app.PhotosFolder) Scan(_app.PhotosFolder);
        if (CurrentName == null)
        {
            try { var now = Api().GetWallpaper(null); if (!string.IsNullOrEmpty(now)) CurrentName = Path.GetFileName(now); }
            catch (COMException) { }
        }

        var interval = TimeSpan.FromMinutes(s.IntervalMinutes);
        var since = DateTimeOffset.Now - (s.LastChangedAt ?? DateTimeOffset.MinValue);
        if (changeNow || since >= interval) Next();
        else Schedule(interval - since);
    }

    void Schedule(TimeSpan due)
    {
        _timer.Stop();
        _timer.Interval = due < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : due;
        _timer.Start();
    }

    /// <summary>다음 사진으로. 실패하면 다음 후보를 몇 개 더 시도한다.</summary>
    public void Next()
    {
        var s = S;
        Schedule(TimeSpan.FromMinutes(s.IntervalMinutes));
        if (!s.Enabled) return;
        var folder = _app.PhotosFolder;
        if (_folder != folder || DateTimeOffset.Now - _scannedAt > Rescan) Scan(folder);
        if (_order.Count == 0) { LastError = "쓸 수 있는 사진이 없습니다(긴 변 1200px 이상 JPG·PNG·BMP)"; _app.NotifyChanged(); return; }

        for (int attempt = 0; attempt < Math.Min(5, _order.Count); attempt++)
        {
            _index = (_index + 1) % _order.Count;
            var path = _order[_index];
            try
            {
                Api().SetWallpaper(null, path);
                CurrentName = Path.GetFileName(path);
                LastError = null;
                s.LastChangedAt = DateTimeOffset.Now;
                Log.Info($"배경화면 → {path}");
                _app.KickProvider("wallpaper"); // 히스토그램 위젯을 바로 갱신
                _app.SaveStateSoon();
                _app.NotifyChanged();
                return;
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
            {
                Log.Warn($"배경화면 설정 실패 {path}: 0x{ex.HResult:X8} {ex.Message}");
                LastError = ex.Message;
            }
        }
        _app.NotifyChanged();
    }

    void Scan(string folder)
    {
        var files = PhotoLibrary.Enumerate(folder, PhotoLibrary.WallpaperExtensions);
        // 작은 사진(캡처·아이콘)은 배경화면으로 쓰기엔 흐려서 뺀다. 크기를 모르면(일부 PNG 등) 넣는다.
        var usable = files.Where(f => PhotoLibrary.TryReadSize(f) is not { } sz || Math.Max(sz.width, sz.height) >= MinEdge).ToList();
        Random.Shared.Shuffle(CollectionsMarshal.AsSpan(usable));
        _order = usable;
        _index = -1;
        _folder = folder;
        _scannedAt = DateTimeOffset.Now;
        Log.Info($"배경화면 후보: {folder} ({usable.Count}/{files.Count}장)");
    }

    /// <summary>켜기 전 배경화면으로 되돌린다.</summary>
    void Restore()
    {
        var s = S;
        if (s.OriginalPath == null) return;
        try
        {
            var api = Api();
            if (s.OriginalPosition is int pos) api.SetPosition(pos);
            if (s.OriginalPath.Length > 0 && File.Exists(s.OriginalPath)) api.SetWallpaper(null, s.OriginalPath);
            Log.Info($"배경화면 바꾸기 끔. 원래 배경화면으로 되돌림: {s.OriginalPath}");
            _app.KickProvider("wallpaper");
        }
        catch (COMException ex) { Log.Error("원래 배경화면으로 되돌리기 실패", ex); }
        s.OriginalPath = null;
        s.OriginalPosition = null;
        s.LastChangedAt = null;
        CurrentName = null;
        _app.SaveStateSoon();
    }

    public void Dispose() => _timer.Stop();
}
