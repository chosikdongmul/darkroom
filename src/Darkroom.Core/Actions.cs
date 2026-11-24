using System.Text.Json;

namespace Darkroom.Core;

/// <summary>위젯 → 호스트 요청. type: open | toggleWidget | loadLayout | setSetting | media | notify</summary>
public sealed class ActionRequest
{
    public string Type { get; set; } = "";
    /// <summary>open: 파일·폴더·URL·앱 경로</summary>
    public string? Target { get; set; }
    /// <summary>toggleWidget: 위젯 id</summary>
    public string? Id { get; set; }
    /// <summary>toggleWidget: 켜기/끄기 지정 (없으면 반전)</summary>
    public bool? Enabled { get; set; }
    /// <summary>loadLayout: 레이아웃 이름</summary>
    public string? Name { get; set; }
    /// <summary>setSetting: 이 위젯의 설정 키/값</summary>
    public string? Key { get; set; }
    public JsonElement Value { get; set; }
    /// <summary>media: playPause | next | previous</summary>
    public string? Command { get; set; }
    /// <summary>notify: Windows 알림 제목·내용 ("notify" 권한 필요)</summary>
    public string? Title { get; set; }
    public string? Text { get; set; }
}

public static class ActionPolicy
{
    /// <summary>실행 파일·스크립트로 취급하는 확장자. 위젯 manifest에 "launch" 권한이 있어야 연다.</summary>
    static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp",
        ".lnk", ".scr", ".hta", ".cpl", ".reg", ".url", ".pif", ".appref-ms", ".jar", ".py", ".pyw",
    };

    static readonly string[] SafeSchemes = ["http", "https", "mailto"];

    /// <summary>open 대상 검사. null이면 허용, 아니면 거부 사유.</summary>
    public static string? CheckOpen(string? target, WidgetManifest widget)
    {
        if (string.IsNullOrWhiteSpace(target)) return "target이 비어 있음";
        target = target.Trim();
        bool launch = widget.HasPermission("launch");

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile && !IsDrivePath(target))
        {
            if (SafeSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)) return null;
            return launch ? null : $"'{uri.Scheme}:' 주소는 launch 권한이 필요";
        }

        string path;
        try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target)); }
        catch (Exception) { return "경로 형식 오류"; }
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return launch ? null : "네트워크 경로는 launch 권한이 필요";
        if (Directory.Exists(path)) return null;
        if (!File.Exists(path)) return "파일·폴더가 없음";
        if (Executable.Contains(Path.GetExtension(path)) && !launch) return "실행 파일은 launch 권한이 필요";
        return null;
    }

    static bool IsDrivePath(string s) => s.Length >= 2 && char.IsLetter(s[0]) && s[1] == ':';
}
