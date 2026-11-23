using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Darkroom.Core;

public sealed partial class Theme
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Tokens { get; set; } = new();

    /// <summary>파일 이름(확장자 제외). 상태·레이아웃에는 이 키를 저장한다.</summary>
    [JsonIgnore] public string Key { get; set; } = "";

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex CamelBoundary();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9]*$")]
    private static partial Regex TokenName();

    /// <summary>fontUi → --dr-font-ui</summary>
    public static string CssVariableName(string token) => "--dr-" + CamelBoundary().Replace(token, "$1-$2").ToLowerInvariant();

    /// <summary>CSS 변수로 바꾼다. 이름이 이상하거나 값에 CSS를 깨뜨리는 문자가 있으면 제외.</summary>
    public Dictionary<string, string> CssVariables()
    {
        var vars = new Dictionary<string, string>();
        foreach (var (k, v) in Tokens)
        {
            if (!TokenName().IsMatch(k) || v.IndexOfAny([';', '{', '}', '<', '>']) >= 0 || v.Length > 512) continue;
            vars[CssVariableName(k)] = v;
        }
        return vars;
    }

    public static Theme Load(string path)
    {
        var t = DrJson.Read<Theme>(path);
        t.Key = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(t.Name)) t.Name = t.Key;
        return t;
    }

    /// <summary>themes/*.json 전부. 깨진 파일은 errors에 담고 건너뛴다.</summary>
    public static Dictionary<string, Theme> LoadAll(string dir, List<string> errors)
    {
        var result = new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir)) return result;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json").Order())
        {
            try { var t = Load(f); result[t.Key] = t; }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        return result;
    }
}
