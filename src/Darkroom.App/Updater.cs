using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Darkroom.Core;
using Microsoft.Win32;

namespace Darkroom.App;

/// <summary>
/// GitHub 릴리스로 새 버전을 확인하고, 설치본이면 설치 파일을 받아 조용히 업데이트한다.
/// 설치 파일이 실행 중인 Darkroom을 --quit으로 끄고, 끝나면 다시 실행한다(/RELAUNCH=1).
/// </summary>
internal sealed class Updater
{
    const string Repo = "chosikdongmul/darkroom";
    const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    const string InstallerKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F0C3A2E-8B1D-4E57-9C2A-D4A7B1E9F305}_is1";

    static readonly HttpClient Http = CreateClient();

    public enum Status { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

    public Status State { get; private set; } = Status.Idle;
    public string? Latest { get; private set; }
    public string? Message { get; private set; }
    public string? PageUrl { get; private set; }
    public Version Current { get; }
    /// <summary>설치 파일로 설치된 exe로 실행 중이면 true. 단일 exe로 실행 중이면 다운로드 페이지만 연다.</summary>
    public bool IsInstalled { get; }

    public event Action? Changed;
    /// <summary>새 버전을 처음 발견했을 때(알림용).</summary>
    public event Action<string>? Found;

    Asset? _asset;

    record Asset(string Name, string Url, long Size, string? Sha256);

    public Updater()
    {
        var v = Environment.GetEnvironmentVariable("DARKROOM_UPDATE_VERSION"); // 시험용: 이 버전인 척 확인
        Current = Version.TryParse(v, out var fake) ? fake : typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
        IsInstalled = CheckInstalled();
    }

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Darkroom-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    static bool CheckInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InstallerKey);
        if (key?.GetValue("InstallLocation") is not string dir || Environment.ProcessPath is not { } exe) return false;
        return string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetDirectoryName(exe)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    void Set(Status s, string? message = null)
    {
        State = s;
        Message = message;
        Changed?.Invoke();
    }

    public async Task CheckAsync()
    {
        if (State is Status.Checking or Status.Downloading or Status.Installing) return;
        var wasAvailable = State == Status.Available;
        Set(Status.Checking);
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestApi));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            PageUrl = root.TryGetProperty("html_url", out var page) ? page.GetString() : $"https://github.com/{Repo}/releases";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) { Set(Status.Failed, $"버전 형식을 읽지 못함: {tag}"); return; }
            Latest = latest.ToString(3);
            _asset = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("Darkroom-Setup-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:") ? dg[7..] : null;
                _asset = new Asset(name, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(), sha);
                break;
            }
            if (latest.CompareTo(Trim(Current)) <= 0) { Set(Status.UpToDate); return; }
            Log.Info($"새 버전 {Latest} (지금 {Current.ToString(3)})");
            Set(Status.Available);
            if (!wasAvailable) Found?.Invoke(Latest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn($"업데이트 확인 실패: {ex.Message}");
            Set(Status.Failed, "업데이트를 확인하지 못했습니다(인터넷 연결 확인).");
        }
    }

    static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>설치본이면 설치 파일을 받아 실행한다. 아니면 릴리스 페이지를 연다.</summary>
    public async Task InstallAsync()
    {
        if (State != Status.Available) return;
        if (!IsInstalled || _asset == null) { OpenPage(); return; }
        Set(Status.Downloading);
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Darkroom-update");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, _asset.Name);
            await using (var src = await Http.GetStreamAsync(_asset.Url))
            await using (var dst = File.Create(file))
                await src.CopyToAsync(dst);

            var len = new FileInfo(file).Length;
            if (len != _asset.Size) throw new InvalidDataException($"크기가 다름 ({len} / {_asset.Size})");
            if (_asset.Sha256 != null)
            {
                await using var fs = File.OpenRead(file);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
                if (!hash.Equals(_asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256이 다름");
            }

            Log.Info($"업데이트 설치 시작: {_asset.Name}");
            Set(Status.Installing);
            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"업데이트 실패: {ex.Message}");
            Set(Status.Available, "받지 못했습니다. 다시 눌러 보세요."); // 다시 누를 수 있게
        }
    }

    public void OpenPage() =>
        Process.Start(new ProcessStartInfo(PageUrl ?? $"https://github.com/{Repo}/releases") { UseShellExecute = true });

    public object Snapshot() => new
    {
        state = State.ToString().ToLowerInvariant(),
        latest = Latest,
        message = Message,
        installed = IsInstalled,
    };
}
