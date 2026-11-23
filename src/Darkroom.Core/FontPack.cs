using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Darkroom.Core;

/// <summary>
/// 글꼴 조합(sdk/fonts/packs.json의 한 항목 + packs/&lt;key&gt;/font.css). 색 테마와 따로 고른다.
/// 위젯에는 CSS 변수로 들어간다: --dr-font-ui, --dr-font-num, --dr-num-weight, --dr-num-tracking, --dr-num-scale, --dr-num-style, --dr-num-variation
/// </summary>
public sealed partial class FontPack
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>한글·본문 글꼴 스택</summary>
    public string Ui { get; set; } = "sans-serif";
    /// <summary>큰 숫자용 글꼴 스택</summary>
    public string Num { get; set; } = "sans-serif";
    public int NumWeight { get; set; } = 600;
    public string NumTracking { get; set; } = "-0.03em";
    /// <summary>큰 숫자 크기 배율(세리프는 같은 크기에서 작아 보여서 키운다)</summary>
    public double NumScale { get; set; } = 1;
    /// <summary>보조 숫자(초, 조리개 등)의 글자 모양: normal | italic</summary>
    public string NumStyle { get; set; } = "normal";
    public string? NumVariation { get; set; }

    /// <summary>packs/&lt;key&gt;/font.css 내용(@font-face 목록)</summary>
    [JsonIgnore] public string Css { get; set; } = "";

    public const string DefaultKey = "pretendard";

    [GeneratedRegex(@"^[a-z0-9-]{1,32}$")]
    private static partial Regex KeyPattern();

    static bool SafeCss(string v) => v.Length <= 300 && v.IndexOfAny([';', '{', '}', '<', '>', '\\']) < 0;

    public string? Validate()
    {
        if (!KeyPattern().IsMatch(Key)) return $"key '{Key}' 형식 오류";
        if (!SafeCss(Ui) || !SafeCss(Num) || !SafeCss(NumTracking) || (NumVariation != null && !SafeCss(NumVariation))) return $"'{Key}': 글꼴 값에 쓸 수 없는 문자";
        if (NumWeight is < 100 or > 1000) return $"'{Key}': numWeight 범위 오류";
        if (NumScale is < 0.5 or > 2) return $"'{Key}': numScale 범위 오류";
        if (NumStyle is not ("normal" or "italic")) return $"'{Key}': numStyle은 normal|italic";
        return null;
    }

    public Dictionary<string, string> CssVariables() => new()
    {
        ["--dr-font-ui"] = Ui,
        ["--dr-font-num"] = Num,
        ["--dr-num-weight"] = NumWeight.ToString(CultureInfo.InvariantCulture),
        ["--dr-num-tracking"] = NumTracking,
        ["--dr-num-scale"] = NumScale.ToString(CultureInfo.InvariantCulture),
        ["--dr-num-style"] = NumStyle,
        ["--dr-num-variation"] = NumVariation ?? "normal",
    };

    /// <summary>fonts 폴더의 packs.json과 각 팩의 font.css를 읽는다. 문제 있는 팩은 errors에 담고 건너뛴다.</summary>
    public static Dictionary<string, FontPack> LoadAll(string fontsDir, List<string> errors)
    {
        var result = new Dictionary<string, FontPack>(StringComparer.OrdinalIgnoreCase);
        var index = Path.Combine(fontsDir, "packs.json");
        if (!File.Exists(index)) return result;
        List<FontPack> packs;
        try { packs = DrJson.Read<List<FontPack>>(index); }
        catch (Exception ex) { errors.Add($"packs.json: {ex.Message}"); return result; }
        foreach (var p in packs)
        {
            if (p.Validate() is { } err) { errors.Add(err); continue; }
            var css = Path.Combine(fontsDir, "packs", p.Key, "font.css");
            if (!File.Exists(css)) { errors.Add($"'{p.Key}': font.css 없음"); continue; }
            p.Css = File.ReadAllText(css);
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = p.Key;
            result[p.Key] = p;
        }
        return result;
    }
}
