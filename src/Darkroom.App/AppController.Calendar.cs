using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Darkroom.Core;
using Darkroom.Providers;

namespace Darkroom.App;

/// <summary>캘린더 연결(CalDAV)과 장소 찾기.</summary>
internal sealed partial class AppController
{
    static readonly HttpClient CalendarHttp = CalendarProvider.CreateClient();
    CalendarProvider? _calendar;
    string? _calError, _calUpdated;
    int? _calCount;
    bool _calBusy;

    static string CalendarCachePath => Path.Combine(AppPaths.AppData, "calendar-cache.json");

    CalendarProvider CreateCalendarProvider()
    {
        _calendar = new CalendarProvider(CalendarAccountOrNull, () => State.Calendar.Hidden.ToHashSet(), CalendarCachePath, CalendarHttp);
        _calendar.Synced += r => Dispatcher.BeginInvoke(() => OnCalendarSynced(r));
        return _calendar;
    }

    /// <summary>Provider 스레드에서도 부른다. 비밀번호는 쓸 때만 풀어서 넘긴다.</summary>
    CalendarAccount? CalendarAccountOrNull()
    {
        var c = State.Calendar;
        if (!c.Connected) return null;
        var password = Secrets.Unprotect(c.Secret);
        return password == null ? null : new CalendarAccount(c.Server, c.Username!, password);
    }

    void OnCalendarSynced(CalendarSyncResult r)
    {
        _calError = r.Error;
        _calUpdated = r.UpdatedAt;
        if (r.Error == null)
        {
            _calCount = r.Items.Count;
            RememberCalendars(r.Calendars);
        }
        Changed?.Invoke();
    }

    void RememberCalendars(List<CalendarInfo> list)
    {
        State.Calendar.Calendars = list.Select(c => new CalendarRef { Id = c.Id, Name = c.Name, Color = c.Color }).ToList();
        State.Calendar.Hidden.RemoveAll(id => !list.Any(c => c.Id == id));
        SaveStateSoon();
    }

    /// <summary>아이디·애플리케이션 비밀번호로 접속해 보고, 되면 저장한다. 실패하면 사유.</summary>
    public async Task<string?> ConnectCalendarAsync(string server, string username, string password)
    {
        username = username.Trim();
        password = password.Trim();
        server = string.IsNullOrWhiteSpace(server) ? CalendarSettings.NaverServer : server.Trim();
        // 네이버 아이디는 @naver.com 없이도, 붙여도 된다
        if (username.Length == 0) return "아이디를 넣어 주세요.";
        if (password.Length == 0) return "애플리케이션 비밀번호를 넣어 주세요.";
        if (username.Length > 100 || password.Length > 200 || server.Length > 300) return "입력이 너무 깁니다.";

        _calBusy = true;
        Changed?.Invoke();
        try
        {
            var account = new CalendarAccount(server, username, password);
            List<CalendarInfo> calendars;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                calendars = await new CalDavClient(CalendarHttp, account).ListCalendarsAsync(cts.Token);
            }
            catch (CalDavException ex) { return ex.Message; }
            catch (OperationCanceledException) { return "캘린더 서버가 응답하지 않아요. 잠시 뒤 다시 시도해 주세요."; }
            if (calendars.Count == 0) return "접속은 됐지만 캘린더를 찾지 못했어요.";

            var c = State.Calendar;
            bool sameAccount = c.Username == username && c.Server == CalDavClient.NormalizeServer(server).AbsoluteUri.TrimEnd('/');
            c.Server = CalDavClient.NormalizeServer(server).AbsoluteUri.TrimEnd('/');
            c.Username = username;
            c.Secret = Secrets.Protect(password);
            if (!sameAccount) c.Hidden.Clear();
            RememberCalendars(calendars);
            _calError = null;
            _calCount = null;
            _calUpdated = null;
            Log.Info($"캘린더 연결: 캘린더 {calendars.Count}개");
            _calendar?.Invalidate();
            _providers?.Kick("calendar");
            SaveStateSoon();
            return null;
        }
        finally
        {
            _calBusy = false;
            Changed?.Invoke();
        }
    }

    public void DisconnectCalendar()
    {
        State.Calendar = new CalendarSettings();
        _calError = _calUpdated = null;
        _calCount = null;
        _calendar?.Invalidate();
        _providers?.Kick("calendar");
        try { if (File.Exists(CalendarCachePath)) File.Delete(CalendarCachePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Log.Info("캘린더 연결 해제");
        SaveStateSoon();
        Changed?.Invoke();
    }

    public void SetCalendarShown(string id, bool shown)
    {
        var hidden = State.Calendar.Hidden;
        if (shown) hidden.Remove(id);
        else if (!hidden.Contains(id) && State.Calendar.Calendars.Any(c => c.Id == id)) hidden.Add(id);
        _calendar?.Invalidate();
        _providers?.Kick("calendar");
        SaveStateSoon();
        Changed?.Invoke();
    }

    public void RefreshCalendar()
    {
        _calendar?.Invalidate();
        _providers?.Kick("calendar");
    }

    object CalendarSnapshot()
    {
        var c = State.Calendar;
        return new
        {
            connected = c.Connected,
            username = c.Username,
            server = c.Server,
            isNaver = c.Server.Equals(CalendarSettings.NaverServer, StringComparison.OrdinalIgnoreCase),
            busy = _calBusy,
            error = _calError,
            updatedAt = _calUpdated,
            count = _calCount,
            calendars = c.Calendars.Select(x => new { id = x.Id, name = x.Name, color = x.Color, shown = !c.Hidden.Contains(x.Id) }).ToList(),
            used = Installed.Values.Any(m => StateOf(m.Id).Enabled && m.Subscribe.Any(k => k.StartsWith("calendar.", StringComparison.Ordinal))),
        };
    }

    // =========================================================== 장소 찾기(길찾기 출발지·도착지)
    static readonly HttpClient PlaceHttp = CreatePlaceClient();
    static DateTime _lastPlaceSearch = DateTime.MinValue;

    static HttpClient CreatePlaceClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // OpenStreetMap Nominatim 이용 정책: 앱을 알아볼 수 있는 User-Agent, 초당 1회 이하, 자동 완성 금지(찾기 버튼으로만 검색)
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Darkroom/1.0 (Windows desktop widgets)");
        return c;
    }

    /// <summary>주소·역·건물 이름 → 후보(OpenStreetMap Nominatim, 키 불필요). 실패하면 빈 목록.</summary>
    public static async Task<List<object>> SearchPlacesAsync(string query)
    {
        query = query.Trim();
        if (query.Length < 1 || query.Length > 100) return new List<object>();
        var wait = _lastPlaceSearch.AddSeconds(1.1) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        _lastPlaceSearch = DateTime.UtcNow;
        try
        {
            var url = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=6&addressdetails=0&accept-language=ko&countrycodes=kr&q=" + Uri.EscapeDataString(query);
            using var doc = JsonDocument.Parse(await PlaceHttp.GetStringAsync(url));
            var list = new List<object>();
            var seen = new HashSet<string>();
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                if (!double.TryParse(S("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                    || !double.TryParse(S("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
                var parts = S("display_name").Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                var name = S("name") is { Length: > 0 } n ? n : parts.FirstOrDefault() ?? query;
                var address = KoreanAddress(parts, name);
                if (!seen.Add(name + "|" + address)) continue; // 같은 이름·주소(역 출입구 여러 개 등)는 하나만
                list.Add(new
                {
                    name = name.Length > 40 ? name[..40] : name,
                    address,
                    lat = Math.Round(lat, 6),
                    lon = Math.Round(lon, 6),
                });
            }
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Log.Warn($"장소 찾기 실패: {ex.Message}");
            return new List<object>();
        }
    }

    /// <summary>"강남역, 강남대로, 역삼동, 강남구, 서울특별시, 06232, 대한민국" → "서울특별시 강남구 역삼1동 강남대로"</summary>
    public static string KoreanAddress(List<string> parts, string name)
    {
        // 이름·나라·우편번호(5자리)는 빼고, 큰 단위부터: "… 테헤란로 152"처럼 번지는 도로 이름 뒤에 남는다
        var rest = parts.Where(p => p != name && p != "대한민국" && !(p.Length == 5 && p.All(char.IsAsciiDigit))).Reverse().ToList();
        var s = string.Join(" ", rest);
        return s.Length > 160 ? s[..160] : s;
    }
}
