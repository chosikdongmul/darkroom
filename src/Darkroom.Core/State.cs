using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Darkroom.Core;

/// <summary>위젯 하나의 사용자 상태. state.json과 레이아웃에 같은 형태로 저장한다.</summary>
public sealed class WidgetState
{
    public bool Enabled { get; set; }
    /// <summary>창 왼쪽 위, 가상 화면 좌표(물리 픽셀). null이면 아직 배치 안 됨.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    /// <summary>null이면 manifest의 windowMode.</summary>
    public string? WindowMode { get; set; }
    public double Opacity { get; set; } = 1.0;
    public bool Locked { get; set; }
    public bool HoverFade { get; set; }
    public Dictionary<string, JsonElement> Settings { get; set; } = new();

    public WidgetState Clone() => new()
    {
        Enabled = Enabled, X = X, Y = Y, WindowMode = WindowMode, Opacity = Opacity, Locked = Locked,
        HoverFade = HoverFade, Settings = new(Settings),
    };
}

/// <summary>Provider 공통 환경 설정(위치, 사진 폴더 등). Manager의 "환경"에서 바꾼다.</summary>
public sealed class ProviderSettings
{
    /// <summary>기본: 서울</summary>
    public double Latitude { get; set; } = 37.57;
    public double Longitude { get; set; } = 126.98;
    public string LocationName { get; set; } = "서울";
    /// <summary>null이면 사용자 사진 폴더</summary>
    public string? PhotosFolder { get; set; }
    public int PhotosIntervalSeconds { get; set; } = 60;

    public const int MinPhotosInterval = 5, MaxPhotosInterval = 3600;

    /// <summary>범위 검사. 문제가 있으면 사유.</summary>
    public string? Validate()
    {
        if (double.IsNaN(Latitude) || Latitude is < -90 or > 90) return "위도는 -90~90";
        if (double.IsNaN(Longitude) || Longitude is < -180 or > 180) return "경도는 -180~180";
        if (LocationName.Length > 60) return "위치 이름이 너무 김";
        if (PhotosIntervalSeconds is < MinPhotosInterval or > MaxPhotosInterval) return $"사진 넘김 간격은 {MinPhotosInterval}~{MaxPhotosInterval}초";
        if (PhotosFolder != null && PhotosFolder.Length > 0 && !Path.IsPathFullyQualified(PhotosFolder)) return "사진 폴더는 전체 경로여야 함";
        return null;
    }

    public ProviderSettings Clone() => (ProviderSettings)MemberwiseClone();
}

/// <summary>배경화면 바꾸기: 사진 폴더(ProviderSettings.PhotosFolder)의 사진으로 Windows 바탕화면 배경을 돌린다.</summary>
public sealed class WallpaperSettings
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 30;
    /// <summary>fill | fit | center | stretch</summary>
    public string Fit { get; set; } = "fill";
    /// <summary>켜기 전 배경화면(끄면 되돌림). 켜져 있는 동안만 값이 있다.</summary>
    public string? OriginalPath { get; set; }
    public int? OriginalPosition { get; set; }
    public DateTimeOffset? LastChangedAt { get; set; }
    /// <summary>마지막으로 지정한 사진(전체 경로). 레지스트리 WallPaper 값은 IDesktopWallpaper로 바꾸면 갱신되지 않아 따로 기억한다.</summary>
    public string? CurrentPath { get; set; }

    public const int MinInterval = 1, MaxInterval = 1440;
    public static readonly string[] Fits = ["fill", "fit", "center", "stretch"];

    public string? Validate()
    {
        if (IntervalMinutes is < MinInterval or > MaxInterval) return $"배경화면 간격은 {MinInterval}~{MaxInterval}분";
        if (!Fits.Contains(Fit)) return "맞춤 방식은 fill|fit|center|stretch";
        return null;
    }
}

/// <summary>%APPDATA%\Darkroom\state.json</summary>
/// <summary>골든아워 시작 전 알림.</summary>
public sealed class SunAlertSettings
{
    public bool Enabled { get; set; } = true;
    public int LeadMinutes { get; set; } = 30;
    /// <summary>아침 골든아워도 알림(기본은 저녁만)</summary>
    public bool Morning { get; set; }

    public string? Validate() => LeadMinutes is < 5 or > 180 ? "알림 시간은 5~180분" : null;
}

/// <summary>캘린더(CalDAV) 계정. 비밀번호는 Windows 사용자 계정으로 암호화(DPAPI)해서만 저장한다.</summary>
public sealed class CalendarSettings
{
    public const string NaverServer = "https://caldav.calendar.naver.com";
    public string Server { get; set; } = NaverServer;
    public string? Username { get; set; }
    /// <summary>DPAPI(CurrentUser)로 암호화한 비밀번호, base64</summary>
    public string? Secret { get; set; }
    /// <summary>마지막으로 찾은 캘린더 목록(Manager 표시용)</summary>
    public List<CalendarRef> Calendars { get; set; } = new();
    /// <summary>위젯에 보여 주지 않을 캘린더 id</summary>
    public List<string> Hidden { get; set; } = new();

    [JsonIgnore] public bool Connected => !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Secret);
}

public sealed class CalendarRef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
}

public sealed class AppState
{
    public int Version { get; set; } = 1;
    public string Theme { get; set; } = "darkroom-gray";
    /// <summary>글꼴 조합 키(sdk/fonts/packs.json). 색 테마와 따로 고른다.</summary>
    public string Font { get; set; } = FontPack.DefaultKey;
    public ProviderSettings Providers { get; set; } = new();
    public WallpaperSettings Wallpaper { get; set; } = new();
    public bool AllLocked { get; set; }
    public bool AllHidden { get; set; }
    /// <summary>새 버전을 자동으로 확인할지(시작 30초 뒤, 그 뒤 12시간마다). 꺼도 Manager에서 직접 확인할 수 있다.</summary>
    public bool CheckUpdates { get; set; } = true;
    /// <summary>해 위치에 따라 색 테마를 바꾼다(골든아워·블루아워·밤). 낮에는 Theme.</summary>
    public bool ThemeAuto { get; set; }
    public SunAlertSettings Alerts { get; set; } = new();
    public CalendarSettings Calendar { get; set; } = new();
    public Dictionary<string, WidgetState> Widgets { get; set; } = new();

    public static AppState? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try { return DrJson.Read<AppState>(path); }
        catch (Exception ex)
        {
            Log.Error($"state.json 읽기 실패, 백업 후 새로 시작", ex);
            try { File.Copy(path, path + ".broken", overwrite: true); } catch (IOException) { }
            return null;
        }
    }

    public void Save(string path) => DrJson.WriteAtomic(path, this);
}

/// <summary>layouts/*.json = 켜진 위젯 + 위치 + 창 모드 + 설정 + 테마.</summary>
public sealed partial class Layout
{
    public string Name { get; set; } = "";
    public string? Theme { get; set; }
    public string? Font { get; set; }
    /// <summary>위젯 id → 상태. 여기 있는 위젯만 켜진다.</summary>
    public Dictionary<string, WidgetState> Widgets { get; set; } = new();

    [JsonIgnore] public string Key { get; set; } = "";

    [GeneratedRegex(@"[^\p{L}\p{N} _.-]")]
    private static partial Regex UnsafeChars();

    /// <summary>레이아웃 이름 → 파일 이름(확장자 제외). 경로 문자를 제거한다.</summary>
    public static string ToKey(string name)
    {
        var k = UnsafeChars().Replace(name.Trim(), "").Trim('.', ' ');
        return k.Length == 0 ? "layout" : k.Length > 60 ? k[..60] : k;
    }

    public static Layout Load(string path)
    {
        var l = DrJson.Read<Layout>(path);
        l.Key = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(l.Name)) l.Name = l.Key;
        return l;
    }

    public static List<Layout> LoadAll(string dir)
    {
        var list = new List<Layout>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json").Order())
        {
            try { list.Add(Load(f)); }
            catch (Exception ex) { Log.Warn($"레이아웃 {Path.GetFileName(f)} 무시: {ex.Message}"); }
        }
        return list;
    }
}
