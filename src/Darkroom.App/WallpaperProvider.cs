using Darkroom.Core;
using Darkroom.Providers;
using Microsoft.Win32;

namespace Darkroom.App;

/// <summary>
/// wallpaper 네임스페이스 — 지금 Windows 배경화면(누가 바꿨든). 5초마다 확인하고, 사진이 바뀌었을 때만 다시 계산한다.
/// Darkroom이 배경화면을 바꾸면 바로 갱신된다(AppController가 Kick).
///   wallpaper.current   = { name, exif: { camera, lens, focalLength, aperture, shutter, iso, takenAt }, width, height } 또는 null
///   wallpaper.histogram = photos.histogram과 같은 형태 { bins, r, g, b, l, shadowsClipped, highlightsClipped, meanLuminance }
/// </summary>
internal sealed class WallpaperProvider(Func<string?> current) : IDataProvider
{
    string? _key;

    public string Namespace => "wallpaper";
    public TimeSpan Interval => TimeSpan.FromSeconds(5);

    /// <summary>배경화면 원본 경로. Darkroom이 지정한 사진이 우선(레지스트리 값은 IDesktopWallpaper로 바꾸면 갱신되지 않는다).
    /// 없으면 레지스트리, 그것도 없으면 Windows가 화면에 쓰는 변환본(TranscodedWallpaper).</summary>
    (string? path, string name) Current()
    {
        if (current() is { Length: > 0 } mine && File.Exists(mine)) return (mine, Path.GetFileName(mine));
        var reg = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;
        if (!string.IsNullOrWhiteSpace(reg) && File.Exists(reg)) return (reg, Path.GetFileName(reg));
        var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        return File.Exists(transcoded) ? (transcoded, "현재 배경화면") : (null, "");
    }

    public async Task UpdateAsync(IDataBus bus, CancellationToken ct)
    {
        var (path, name) = Current();
        if (path == null)
        {
            _key = null;
            bus.Publish("wallpaper.current", (object?)null);
            bus.Publish("wallpaper.histogram", (object?)null);
            return;
        }
        var fi = new FileInfo(path);
        var key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        if (key == _key) return;
        _key = key;

        var ext = fi.Extension.ToLowerInvariant();
        var exif = ext is ".jpg" or ".jpeg" ? ExifReader.ReadFile(path) : null;
        var size = PhotoLibrary.TryReadSize(path);
        string? camera = exif == null ? null
            : exif.Model != null && exif.Make != null && !exif.Model.StartsWith(exif.Make.Split(' ')[0], StringComparison.OrdinalIgnoreCase)
                ? $"{exif.Make} {exif.Model}" : exif.Model ?? exif.Make;
        bus.Publish("wallpaper.current", new
        {
            name,
            exif = exif == null ? null : new
            {
                camera, lens = exif.Lens, focalLength = exif.FocalLength, aperture = exif.Aperture,
                shutter = exif.Shutter, iso = exif.Iso, takenAt = exif.TakenAt,
            },
            width = size?.width,
            height = size?.height,
        });

        object? histogram = null;
        try { histogram = await PhotoLibrary.HistogramAsync(path); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"배경화면 히스토그램 실패 {name}: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
        bus.Publish("wallpaper.histogram", histogram);
    }
}
