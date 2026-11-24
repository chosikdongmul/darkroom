using System.IO.Compression;
using System.Security.Cryptography;
using Darkroom.Core;

namespace Darkroom.App;

internal sealed class AppPaths
{
    /// <summary>widgets/, themes/, sdk/, layouts/ (배포본은 manager/ 도) 가 있는 폴더.</summary>
    public required string Root { get; init; }
    public string Widgets => Path.Combine(Root, "widgets");
    public string Themes => Path.Combine(Root, "themes");
    public string Sdk => Path.Combine(Root, "sdk");
    public string SdkFile => Path.Combine(Sdk, "darkroom.js");
    public string Layouts => Path.Combine(Root, "layouts");

    /// <summary>사용자 데이터(state.json, 로그). DARKROOM_DATA 환경 변수로 바꿀 수 있다(새 PC처럼 시험할 때).</summary>
    public static string AppData =>
        Environment.GetEnvironmentVariable("DARKROOM_DATA") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Darkroom");
    public static string StateFile => Path.Combine(AppData, "state.json");
    public static string Logs => Path.Combine(AppData, "logs");
    static string LocalData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Darkroom");
    public static string WebViewData => Path.Combine(LocalData, "WebView2");
    /// <summary>배포 exe가 내장 콘텐츠를 풀어 두는 곳</summary>
    public static string ContentFolder => Path.Combine(LocalData, "content");

    /// <summary>Manager 화면(HTML). 배포본은 콘텐츠 폴더의 manager/, 개발 중에는 실행 파일 옆 Manager/web.</summary>
    public static string ManagerWeb { get; private set; } = Path.Combine(AppContext.BaseDirectory, "Manager", "web");

    public const string ContentResource = "darkroom-content.zip";

    /// <summary>
    /// 루트 찾기: --root 인자 → 실행 파일 위쪽에서 widgets/와 sdk/가 있는 폴더(개발: 저장소 원본을 고치면 바로 Hot Reload)
    /// → exe에 내장된 콘텐츠를 %LOCALAPPDATA%\Darkroom\content 에 풀어서 사용(배포 단일 exe) → 실행 파일 폴더.
    /// </summary>
    public static AppPaths Resolve(string? rootArg)
    {
        if (!string.IsNullOrWhiteSpace(rootArg)) return Use(Path.GetFullPath(rootArg));
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "widgets")) && Directory.Exists(Path.Combine(dir.FullName, "sdk")))
                return Use(dir.FullName);
            dir = dir.Parent;
        }
        return Use(ExtractEmbeddedContent() ?? AppContext.BaseDirectory);
    }

    static AppPaths Use(string root)
    {
        var manager = Path.Combine(root, "manager");
        if (File.Exists(Path.Combine(manager, "index.html"))) ManagerWeb = manager;
        return new AppPaths { Root = root };
    }

    /// <summary>
    /// exe에 내장된 콘텐츠 zip을 풀어 둔다. 내용이 바뀐 새 버전일 때만 다시 풀고(덮어쓰기), 사용자가 추가한 위젯 폴더는 지우지 않는다.
    /// 내장 콘텐츠가 없으면(개발 빌드) null.
    /// </summary>
    static string? ExtractEmbeddedContent()
    {
        using var res = typeof(AppPaths).Assembly.GetManifestResourceStream(ContentResource);
        if (res == null) return null;
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        var bytes = ms.ToArray();
        var stamp = Convert.ToHexString(SHA256.HashData(bytes))[..16];

        var target = ContentFolder;
        var stampFile = Path.Combine(target, ".content-version");
        if (File.Exists(stampFile) && File.ReadAllText(stampFile).Trim() == stamp) return target;

        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip 경로 탈출 방지
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
        RemoveDroppedWidgets(target, bytes);
        File.WriteAllText(stampFile, stamp);
        Log.Info($"내장 콘텐츠를 풀었습니다: {target} ({stamp})");
        return target;
    }

    /// <summary>이전 버전에 들어 있다가 빠진 내장 위젯 폴더를 지운다. 사용자가 직접 넣은 위젯 폴더는 건드리지 않는다.</summary>
    static void RemoveDroppedWidgets(string target, byte[] zipBytes)
    {
        var listFile = Path.Combine(target, ".bundled-widgets");
        HashSet<string> current;
        using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            current = zip.Entries.Select(e => e.FullName.Replace('\\', '/').Split('/'))
                .Where(p => p.Length > 2 && p[0] == "widgets").Select(p => p[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "naver-map" }; // 0.9.0에만 있던 위젯(목록 파일이 생기기 전)
        try { if (File.Exists(listFile)) previous.UnionWith(File.ReadAllLines(listFile).Where(l => l.Length > 0)); }
        catch (IOException) { }
        foreach (var name in previous.Where(n => !current.Contains(n) && !n.Contains('.') && !n.Contains('\\') && !n.Contains('/')))
        {
            var dir = Path.Combine(target, "widgets", name);
            try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); Log.Info($"빠진 내장 위젯 폴더 삭제: {name}"); } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"위젯 폴더 삭제 실패({name}): {ex.Message}"); }
        }
        try { File.WriteAllLines(listFile, current.Order(StringComparer.Ordinal)); }
        catch (IOException) { }
    }
}
