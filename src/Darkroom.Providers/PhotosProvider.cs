using System.Security.Cryptography;
using System.Text;
using Darkroom.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Darkroom.Providers;

/// <summary>
/// photos 네임스페이스 — 사진 폴더(하위 폴더 포함)를 섞은 순서로 돌린다. 간격은 환경 설정(기본 60초).
/// 고른 사진은 긴 변 1280px 이하로 줄여 캐시 폴더에 쓰고, 호스트는 그 캐시 폴더만 https://photos.darkroom/ 에 매핑한다.
/// (위젯은 원본 사진 폴더 전체를 읽을 수 없고, 원본 해상도로 디코딩하지 않아 GPU 메모리를 아낀다.)
///   photos.current   = { url, name, relativePath, index, count, exif: { camera, lens, focalLength, focalLength35, aperture, shutter, iso, takenAt, width, height } }
///   photos.histogram = { bins, r, g, b, l (0~1000), shadowsClipped, highlightsClipped (%), meanLuminance (0~255) } — photos.current의 사진
///   photos.stats     = { total, today, week, month, lastShotAt } — 파일 수정 시각 기준 촬영 수 (2분마다 다시 셈)
///   photos.status    = { ok, folder, count, error }
/// </summary>
public sealed class PhotosProvider(Func<ProviderSettings> settings) : IDataProvider
{
    public const string HostName = "photos.darkroom";
    public const int MaxFiles = 20_000;
    public const int MaxEdge = 1280, MaxCached = 40;
    public static string CacheFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Darkroom", "photo-cache");
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp" };
    static readonly TimeSpan Rescan = TimeSpan.FromMinutes(2);

    string? _folder;
    List<string> _order = new();                                         // 보여 줄 순서(섞인 상태)
    Dictionary<string, DateTime> _times = new(StringComparer.OrdinalIgnoreCase); // 경로 → 수정 시각(로컬)
    DateTimeOffset _scannedAt;
    int _index = -1;

    public string Namespace => "photos";
    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(settings().PhotosIntervalSeconds, ProviderSettings.MinPhotosInterval, ProviderSettings.MaxPhotosInterval));

    /// <summary>설정의 폴더, 비어 있으면 사용자 사진 폴더.</summary>
    public static string ResolveFolder(ProviderSettings s) =>
        string.IsNullOrWhiteSpace(s.PhotosFolder) ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) : s.PhotosFolder!;

    public async Task UpdateAsync(IDataBus bus, CancellationToken ct)
    {
        var folder = ResolveFolder(settings());
        if (!Directory.Exists(folder))
        {
            _order.Clear(); _times.Clear(); _folder = null;
            bus.Publish("photos.status", new { ok = false, folder, count = 0, error = "폴더가 없습니다" });
            bus.Publish("photos.current", (object?)null);
            bus.Publish("photos.histogram", (object?)null);
            bus.Publish("photos.stats", PhotoStats.Compute([], DateTime.Now));
            return;
        }
        if (_folder != folder || DateTimeOffset.Now - _scannedAt > Rescan) Scan(folder, ct);
        bus.Publish("photos.stats", PhotoStats.Compute(_times.Values, DateTime.Now));
        if (_order.Count == 0)
        {
            bus.Publish("photos.status", new { ok = false, folder, count = 0, error = "사진이 없습니다" });
            bus.Publish("photos.current", (object?)null);
            bus.Publish("photos.histogram", (object?)null);
            return;
        }

        _index = (_index + 1) % _order.Count;
        var path = _order[_index];
        var rel = Path.GetRelativePath(folder, path);
        var exif = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? ExifReader.ReadFile(path) : null;
        string? camera = exif == null ? null
            : exif.Model != null && exif.Make != null && !exif.Model.StartsWith(exif.Make.Split(' ')[0], StringComparison.OrdinalIgnoreCase)
                ? $"{exif.Make} {exif.Model}" : exif.Model ?? exif.Make;

        string cached;
        try { cached = await PrepareAsync(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            Log.Warn($"사진 준비 실패 {path}: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return; // 다음 주기에 다음 사진
        }

        bus.Publish("photos.status", new { ok = true, folder, count = _order.Count, error = (string?)null });
        bus.Publish("photos.current", new
        {
            url = $"https://{HostName}/{cached}",
            name = Path.GetFileName(path),
            relativePath = rel,
            index = _index,
            count = _order.Count,
            exif = exif == null ? null : new
            {
                camera,
                lens = exif.Lens,
                focalLength = exif.FocalLength,
                focalLength35 = exif.FocalLength35,
                aperture = exif.Aperture,
                shutter = exif.Shutter,
                iso = exif.Iso,
                takenAt = exif.TakenAt,
                width = exif.Width,
                height = exif.Height,
            },
        });

        object? histogram = null;
        try { histogram = await PhotoLibrary.HistogramAsync(Path.Combine(CacheFolder, cached)); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"히스토그램 계산 실패 {Path.GetFileName(path)}: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
        bus.Publish("photos.histogram", histogram);
    }

    /// <summary>캐시에 축소본을 만들고 캐시 파일 이름을 돌려준다(같은 원본·수정 시각이면 재사용).</summary>
    static async Task<string> PrepareAsync(string path)
    {
        Directory.CreateDirectory(CacheFolder);
        var fi = new FileInfo(path);
        var ext = fi.Extension.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")))[..20].ToLowerInvariant();
        var name = hash + ext;
        var target = Path.Combine(CacheFolder, name);
        if (File.Exists(target)) { File.SetLastWriteTimeUtc(target, DateTime.UtcNow); return name; }

        bool resized = false;
        if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp")
        {
            try { resized = await ResizeAsync(fi.FullName, target); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or UnauthorizedAccessException or IOException)
            {
                // 디코더가 못 읽는 파일 등: 원본을 그대로 쓴다
                Log.Warn($"사진 축소 실패, 원본 사용 {fi.Name}: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }
        if (!resized) File.Copy(fi.FullName, target, overwrite: true); // 작거나 GIF 등: 그대로

        // 오래된 캐시 정리
        foreach (var old in new DirectoryInfo(CacheFolder).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxCached))
            try { old.Delete(); } catch (IOException) { }
        return name;
    }

    /// <summary>긴 변이 MaxEdge를 넘으면 같은 형식으로 다시 인코딩(메타데이터·회전 정보 유지)하며 줄인다. 줄였으면 true.</summary>
    static async Task<bool> ResizeAsync(string source, string target)
    {
        var file = await StorageFile.GetFileFromPathAsync(source);
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        double scale = Math.Min(1.0, MaxEdge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        if (scale >= 1.0) return false;
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateForTranscodingAsync(output, decoder);
        encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
        encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync();
        output.Seek(0);
        var tmp = target + ".part";
        await using (var fs = File.Create(tmp))
            await output.AsStreamForRead().CopyToAsync(fs);
        File.Move(tmp, target, overwrite: true);
        return true;
    }

    /// <summary>폴더를 다시 센다. 같은 폴더면 기존 순서를 유지하고 새 파일만 섞어서 뒤에 붙인다(보던 순서가 처음으로 돌아가지 않게).</summary>
    void Scan(string folder, CancellationToken ct)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System,
        };
        var times = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (!Extensions.Contains(f.Extension)) continue;
            times[f.FullName] = f.LastWriteTime; // 열거할 때 함께 읽힌 값 → 추가 디스크 접근 없음
            if (times.Count >= MaxFiles) { Log.Warn($"사진 폴더에 파일이 많아 {MaxFiles}장까지만 사용"); break; }
        }

        bool sameFolder = _folder == folder;
        var kept = sameFolder ? _order.Where(times.ContainsKey).ToList() : new List<string>();
        var known = new HashSet<string>(kept, StringComparer.OrdinalIgnoreCase);
        var added = times.Keys.Where(p => !known.Contains(p)).ToList();
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(added));
        if (sameFolder && _index >= 0 && _index < _order.Count)
            _index = Math.Max(-1, kept.IndexOf(_order[_index])); // 보던 위치 유지
        else _index = -1;
        kept.AddRange(added);

        if (!sameFolder || added.Count > 0 || kept.Count != _order.Count)
            Log.Info($"사진 폴더 스캔: {folder} ({times.Count}장, 새로 {added.Count}장)");
        _order = kept;
        _times = times;
        _folder = folder;
        _scannedAt = DateTimeOffset.Now;
    }
}

/// <summary>촬영 수 통계(파일 수정 시각 기준). 주는 월요일부터.</summary>
public static class PhotoStats
{
    public static object Compute(IEnumerable<DateTime> times, DateTime now)
    {
        var today = now.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(today.Year, today.Month, 1);
        int total = 0, todayCount = 0, week = 0, month = 0;
        DateTime? last = null;
        foreach (var t in times)
        {
            total++;
            if (t > now) continue; // 미래 시각(시계가 틀린 카메라 등)은 기간 집계에서 뺀다
            if (t >= today) todayCount++;
            if (t >= weekStart) week++;
            if (t >= monthStart) month++;
            if (last == null || t > last) last = t;
        }
        return new { total, today = todayCount, week, month, lastShotAt = last?.ToString("yyyy-MM-ddTHH:mm:ss") };
    }
}

/// <summary>BGRA8 픽셀 → 64칸 RGB·휘도 히스토그램(0~1000으로 정규화) + 클리핑 비율.</summary>
public static class Histogram
{
    public const int Bins = 64;

    public static object Compute(ReadOnlySpan<byte> bgra)
    {
        var r = new long[Bins]; var g = new long[Bins]; var b = new long[Bins]; var l = new long[Bins];
        long n = 0, dark = 0, bright = 0; double lumSum = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte B = bgra[i], G = bgra[i + 1], R = bgra[i + 2];
            int lum = (int)Math.Round(0.2126 * R + 0.7152 * G + 0.0722 * B);
            r[R >> 2]++; g[G >> 2]++; b[B >> 2]++; l[lum >> 2]++;
            if (lum <= 2) dark++;
            if (lum >= 253) bright++;
            lumSum += lum;
            n++;
        }
        // 양 끝 칸(순수 검정·흰색)이 튀면 나머지가 납작해지므로, 정규화 기준은 가운데 칸들의 최댓값.
        long max = 1;
        for (int i = 1; i < Bins - 1; i++) max = Math.Max(max, Math.Max(Math.Max(r[i], g[i]), Math.Max(b[i], l[i])));
        int[] Norm(long[] a) => a.Select(v => (int)Math.Min(1000, v * 1000 / max)).ToArray();
        return new
        {
            bins = Bins,
            r = Norm(r), g = Norm(g), b = Norm(b), l = Norm(l),
            shadowsClipped = n == 0 ? 0 : Math.Round(100.0 * dark / n, 1),
            highlightsClipped = n == 0 ? 0 : Math.Round(100.0 * bright / n, 1),
            meanLuminance = n == 0 ? 0 : Math.Round(lumSum / n),
        };
    }
}
