using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Darkroom.Core;

public static class WindowModes
{
    public const string Desktop = "desktop", Bottom = "bottom", Normal = "normal", Topmost = "topmost";
    public static readonly string[] All = [Desktop, Bottom, Normal, Topmost];
    public static bool IsValid(string? m) => m != null && All.Contains(m);
}

public sealed class WidgetSize
{
    public int Width { get; set; } = 320;
    public int Height { get; set; } = 140;
    public bool Resizable { get; set; }
    /// <summary>창 가장자리의 투명 여백(그림자용, DIP). 스냅은 이 여백을 뺀 카드 기준으로 한다.</summary>
    public int Inset { get; set; }
}

public sealed class SettingSpec
{
    /// <summary>bool | number | string | choice | dates | links | numbers
    /// dates   = [{ date: "YYYY-MM-DD", name, yearly?, pinned? }] (D-day 등)
    /// links   = [{ name, target }] (앱·폴더·웹 주소)
    /// numbers = [숫자, ...] (Min~Max)</summary>
    public string Type { get; set; } = "string";
    public JsonElement? Default { get; set; }
    public string? Label { get; set; }
    /// <summary>Manager에서 설정 아래에 보여 줄 짧은 도움말</summary>
    public string? Hint { get; set; }
    public List<string>? Options { get; set; }
    /// <summary>choice의 화면 표시 이름(options와 같은 순서). 없으면 값을 그대로 보여 준다.</summary>
    public List<string>? OptionLabels { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    /// <summary>dates·links·numbers의 최대 개수</summary>
    public int? MaxItems { get; set; }

    public bool Accepts(JsonElement v) => Type switch
    {
        "bool" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => v.ValueKind == JsonValueKind.Number && InRange(v.GetDouble()),
        "string" => v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 4096,
        "choice" => v.ValueKind == JsonValueKind.String && Options != null && Options.Contains(v.GetString()!),
        "dates" => IsList(v, 50, e => Str(e, "date", 10) is { } d && DateOnly.TryParseExact(d, "yyyy-MM-dd", out _)
                                       && Str(e, "name", 60) != null && OptBool(e, "yearly") && OptBool(e, "pinned")),
        "links" => IsList(v, 12, e => Str(e, "name", 40) != null && Str(e, "target", 1024) is { Length: > 0 }),
        "numbers" => IsList(v, 8, e => e.ValueKind == JsonValueKind.Number && InRange(e.GetDouble())),
        // 장소: null(아직 안 고름) 또는 { name, address?, lat, lon }
        "place" => v.ValueKind == JsonValueKind.Null || (Str(v, "name", 40) is { Length: > 0 } && OptStr(v, "address", 160)
                   && Num(v, "lat") is >= -90 and <= 90 && Num(v, "lon") is >= -180 and <= 180),
        _ => false,
    };

    bool InRange(double d) => (Min == null || d >= Min) && (Max == null || d <= Max);

    bool IsList(JsonElement v, int hardMax, Func<JsonElement, bool> item) =>
        v.ValueKind == JsonValueKind.Array && v.GetArrayLength() <= Math.Min(MaxItems ?? hardMax, hardMax) && v.EnumerateArray().All(item);

    static string? Str(JsonElement e, string name, int max) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String && s.GetString()!.Length <= max ? s.GetString() : null;

    static bool OptStr(JsonElement e, string name, int max) =>
        !e.TryGetProperty(name, out var s) || s.ValueKind == JsonValueKind.Null || (s.ValueKind == JsonValueKind.String && s.GetString()!.Length <= max);

    static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : null;

    static bool OptBool(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var b) || b.ValueKind is JsonValueKind.True or JsonValueKind.False;
}

public sealed partial class WidgetManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Manager 목록에 보여 줄 한 줄 설명</summary>
    public string? Description { get; set; }
    public string Version { get; set; } = "0.0.0";
    public string Entry { get; set; } = "index.html";
    public WidgetSize Size { get; set; } = new();
    public string WindowMode { get; set; } = WindowModes.Desktop;
    public List<string> Subscribe { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
    public Dictionary<string, SettingSpec> Settings { get; set; } = new();
    /// <summary>"web" 권한: 위젯 안에 띄울 웹 화면이 이동할 수 있는 사이트(도메인과 그 하위 도메인).</summary>
    public WebSpec? Web { get; set; }

    [JsonIgnore] public string Folder { get; private set; } = "";
    /// <summary>가상 호스트 이름. 위젯 폴더가 https://{HostName}/ 에 매핑된다.</summary>
    [JsonIgnore] public string HostName => Id + ".darkroom";

    public const string FileName = "widget.json";
    static readonly string[] KnownSettingTypes = ["bool", "number", "string", "choice", "dates", "links", "numbers", "place"];
    static readonly string[] KnownPermissions = ["network", "launch", "notify", "calendar", "web"];
    /// <summary>호스트가 쓰는 가상 주소(assets.darkroom = 내장 글꼴, manager.darkroom, photos.darkroom)와 겹치는 id</summary>
    static readonly string[] ReservedIds = ["assets", "manager", "photos"];

    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    public bool HasPermission(string p) => Permissions.Contains(p);

    /// <summary>위젯 폴더에서 widget.json을 읽고 검증한다. 문제가 있으면 InvalidDataException.</summary>
    public static WidgetManifest Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) throw new InvalidDataException($"{FileName} 없음");
        WidgetManifest m;
        try { m = DrJson.Read<WidgetManifest>(path); }
        catch (JsonException ex) { throw new InvalidDataException($"{FileName} 형식 오류: {ex.Message}"); }

        m.Folder = Path.GetFullPath(folder);
        var errors = new List<string>();
        if (!IdPattern().IsMatch(m.Id) || m.Id.Length > 64) errors.Add($"id '{m.Id}'는 소문자·숫자·'.'·'-'만 쓸 수 있다");
        else if (ReservedIds.Contains(m.Id)) errors.Add($"id '{m.Id}'는 호스트가 쓰는 주소라 쓸 수 없다");
        if (string.IsNullOrWhiteSpace(m.Name)) m.Name = m.Id;
        var entry = Path.GetFullPath(Path.Combine(m.Folder, m.Entry));
        if (!entry.StartsWith(m.Folder, StringComparison.OrdinalIgnoreCase)) errors.Add("entry가 위젯 폴더 밖을 가리킨다");
        else if (!File.Exists(entry)) errors.Add($"entry 파일 '{m.Entry}' 없음");
        if (m.Size.Width is < 40 or > 4000 || m.Size.Height is < 40 or > 4000) errors.Add("size는 40~4000");
        if (m.Size.Inset < 0 || m.Size.Inset * 2 >= Math.Min(m.Size.Width, m.Size.Height)) errors.Add("size.inset 범위 오류");
        if (!WindowModes.IsValid(m.WindowMode)) errors.Add($"windowMode '{m.WindowMode}' (desktop|bottom|normal|topmost)");
        foreach (var k in m.Subscribe) if (!DataBus.IsValidSubscription(k)) errors.Add($"subscribe 키 '{k}' 형식 오류");
        foreach (var p in m.Permissions) if (!KnownPermissions.Contains(p)) errors.Add($"알 수 없는 permission '{p}'");
        foreach (var (key, spec) in m.Settings)
        {
            if (!KnownSettingTypes.Contains(spec.Type)) errors.Add($"설정 '{key}': type '{spec.Type}'");
            else if (spec.Default is { } d && !spec.Accepts(d)) errors.Add($"설정 '{key}': default가 type과 맞지 않음");
            else if (spec.Type == "choice" && (spec.Options == null || spec.Options.Count == 0)) errors.Add($"설정 '{key}': choice에는 options 필요");
            else if (spec.OptionLabels != null && spec.OptionLabels.Count != (spec.Options?.Count ?? 0)) errors.Add($"설정 '{key}': optionLabels 개수가 options와 다름");
        }
        if (m.HasPermission("web"))
        {
            if (m.Web?.Allow is not { Count: > 0 } allow) errors.Add("web 권한에는 web.allow(허용할 사이트) 필요");
            else foreach (var d in allow) if (!WebSpec.IsValidDomain(d)) errors.Add($"web.allow '{d}' 형식 오류");
        }
        else if (m.Web != null) errors.Add("web 항목은 web 권한이 있을 때만");
        if (errors.Count > 0) throw new InvalidDataException(string.Join("; ", errors));
        return m;
    }

    /// <summary>스키마 기본값 위에 저장된 값을 덮는다. 스키마에 없거나 타입이 틀린 값은 버린다.</summary>
    public Dictionary<string, JsonElement> EffectiveSettings(IReadOnlyDictionary<string, JsonElement>? saved)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var (key, spec) in Settings)
        {
            if (saved != null && saved.TryGetValue(key, out var v) && spec.Accepts(v)) result[key] = v;
            else if (spec.Default is { } d) result[key] = d;
            else result[key] = spec.Type switch
            {
                "bool" => DrJson.ToElement(false),
                "number" => DrJson.ToElement(spec.Min ?? 0),
                "choice" => DrJson.ToElement(spec.Options![0]),
                "dates" or "links" or "numbers" => DrJson.ToElement(Array.Empty<object>()),
                "place" => DrJson.ToElement(null),
                _ => DrJson.ToElement(""),
            };
        }
        return result;
    }
}

public sealed partial class WebSpec
{
    public List<string> Allow { get; set; } = new();

    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$")]
    private static partial Regex DomainPattern();

    public static bool IsValidDomain(string d) => d.Length <= 253 && DomainPattern().IsMatch(d);

    /// <summary>https 주소이고 허용한 사이트(또는 그 하위 도메인)인지.</summary>
    public bool Allows(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && Allow.Any(d => u.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
}
