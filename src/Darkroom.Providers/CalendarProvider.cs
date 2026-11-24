using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Darkroom.Core;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace Darkroom.Providers;

public sealed record CalendarAccount(string Server, string Username, string Password);
public sealed record CalendarInfo(string Id, string Name, string? Color);
public sealed record CalendarItem(string Title, DateTime Start, DateTime End, bool AllDay, string CalendarId, string Calendar, string? Color);

public sealed class CalDavException(string message, bool authFailed = false) : Exception(message)
{
    public bool AuthFailed { get; } = authFailed;
}

/// <summary>CalDAV 읽기 전용 클라이언트(RFC 4791). 네이버 캘린더: https://caldav.calendar.naver.com + 애플리케이션 비밀번호.</summary>
public sealed class CalDavClient
{
    static readonly XNamespace D = "DAV:", C = "urn:ietf:params:xml:ns:caldav", Apple = "http://apple.com/ns/ical/";
    readonly HttpClient _http;
    readonly Uri _server;
    readonly AuthenticationHeaderValue _auth;

    public CalDavClient(HttpClient http, CalendarAccount account)
    {
        _http = http;
        _server = NormalizeServer(account.Server);
        _auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.Username}:{account.Password}")));
    }

    public static Uri NormalizeServer(string server)
    {
        var s = server.Trim();
        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new CalDavException("캘린더 서버 주소는 https:// 로 시작해야 해요.");
        return uri;
    }

    async Task<XDocument> SendAsync(string method, Uri url, string body, int depth, CancellationToken ct)
    {
        // 리다이렉트는 직접 따라간다(.NET은 리다이렉트 때 Authorization을 떼어 버린다). 같은 서버 안에서만.
        for (int hop = 0; hop < 4; hop++)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            };
            req.Headers.Authorization = _auth;
            req.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct); }
            catch (HttpRequestException) { throw new CalDavException("캘린더 서버에 연결하지 못했어요. 인터넷 연결을 확인해 주세요."); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new CalDavException("캘린더 서버가 응답하지 않아요. 잠시 뒤 다시 시도해 주세요."); }
            using (resp)
            {
                int code = (int)resp.StatusCode;
                if (code is 301 or 302 or 307 or 308 && resp.Headers.Location is { } loc)
                {
                    var next = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                    if (next.Scheme != Uri.UriSchemeHttps || !next.Host.Equals(_server.Host, StringComparison.OrdinalIgnoreCase))
                        throw new CalDavException("캘린더 서버가 다른 주소로 보내려고 해서 멈췄어요.");
                    url = next;
                    continue;
                }
                if (code is 401 or 403) throw new CalDavException("아이디나 애플리케이션 비밀번호가 맞지 않아요.", authFailed: true);
                if (code is not (207 or 200)) throw new CalDavException($"캘린더 서버 응답 오류 ({code})");
                var text = await resp.Content.ReadAsStringAsync(ct);
                try { return XDocument.Parse(text); }
                catch (XmlException) { throw new CalDavException("캘린더 서버 응답을 읽지 못했어요."); }
            }
        }
        throw new CalDavException("캘린더 서버가 너무 여러 번 다른 주소로 보냈어요.");
    }

    Uri Resolve(Uri baseUri, string href) => new(baseUri, href.Trim());

    /// <summary>응답에서 prop/name/href 하나를 찾는다(current-user-principal, calendar-home-set).</summary>
    static string? FindHref(XDocument doc, XName prop) =>
        doc.Descendants(prop).Elements(D + "href").Select(e => e.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    public async Task<List<CalendarInfo>> ListCalendarsAsync(CancellationToken ct)
    {
        const string principalBody = """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:current-user-principal/></d:prop></d:propfind>""";
        XDocument doc;
        try { doc = await SendAsync("PROPFIND", _server, principalBody, 0, ct); }
        catch (CalDavException ex) when (!ex.AuthFailed) { doc = await SendAsync("PROPFIND", new Uri(_server, "/.well-known/caldav"), principalBody, 0, ct); }
        var principal = FindHref(doc, D + "current-user-principal") is { } p ? Resolve(_server, p) : _server;

        const string homeBody = """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><c:calendar-home-set/></d:prop></d:propfind>""";
        doc = await SendAsync("PROPFIND", principal, homeBody, 0, ct);
        var home = FindHref(doc, C + "calendar-home-set") is { } h ? Resolve(principal, h) : principal;

        const string listBody = """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:a="http://apple.com/ns/ical/"><d:prop><d:resourcetype/><d:displayname/><c:supported-calendar-component-set/><a:calendar-color/></d:prop></d:propfind>""";
        doc = await SendAsync("PROPFIND", home, listBody, 1, ct);
        return ParseCalendarList(doc, home);
    }

    public static List<CalendarInfo> ParseCalendarList(XDocument doc, Uri home)
    {
        var list = new List<CalendarInfo>();
        foreach (var r in doc.Descendants(D + "response"))
        {
            var href = r.Element(D + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) continue;
            // 200 상태의 propstat만 본다
            var props = r.Elements(D + "propstat").Where(ps => (ps.Element(D + "status")?.Value ?? "200").Contains(" 200")).Elements(D + "prop").ToList();
            if (!props.Elements(D + "resourcetype").Elements(C + "calendar").Any()) continue;
            var comps = props.Elements(C + "supported-calendar-component-set").Elements(C + "comp").Select(c => (string?)c.Attribute("name")).ToList();
            if (comps.Count > 0 && !comps.Contains("VEVENT")) continue; // 할 일 목록 등
            var name = props.Elements(D + "displayname").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
            var color = props.Elements(Apple + "calendar-color").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
            if (color is { Length: 9 } && color[0] == '#') color = color[..7]; // #RRGGBBAA → #RRGGBB
            var id = new Uri(home, href.Trim()).AbsoluteUri;
            list.Add(new CalendarInfo(id, name ?? "캘린더", color is { Length: 7 } && color[0] == '#' ? color : null));
        }
        return list;
    }

    /// <summary>기간 안에 걸치는 VEVENT의 iCalendar 원문들.</summary>
    public async Task<List<string>> FetchAsync(CalendarInfo cal, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        string F(DateTime d) => d.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var body = $"""<?xml version="1.0" encoding="utf-8"?><c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><c:calendar-data/></d:prop><c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT"><c:time-range start="{F(fromUtc)}" end="{F(toUtc)}"/></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>""";
        var doc = await SendAsync("REPORT", new Uri(cal.Id), body, 1, ct);
        return doc.Descendants(C + "calendar-data").Select(e => e.Value).Where(v => v.Contains("BEGIN:VCALENDAR", StringComparison.Ordinal)).ToList();
    }
}

/// <summary>iCalendar 원문 → 기간 안의 일정(반복 일정은 펼친다). 시각은 이 PC의 현지 시각.</summary>
public static class CalendarIcs
{
    public static IEnumerable<CalendarItem> Expand(string ics, CalendarInfo cal, DateTime fromLocal, DateTime timedUntilLocal, DateTime allDayUntilLocal, int maxPerSeries = 400)
    {
        Ical.Net.Calendar? parsed;
        try { parsed = Ical.Net.Calendar.Load(ics); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warn($"일정 하나를 읽지 못함: {ex.Message}"); yield break; }
        if (parsed == null) yield break;

        var start = new CalDateTime(fromLocal.ToUniversalTime(), "UTC");
        var untilUtc = (allDayUntilLocal > timedUntilLocal ? allDayUntilLocal : timedUntilLocal).ToUniversalTime();
        var perSeries = new Dictionary<string, int>();
        IEnumerable<Occurrence> occurrences;
        try { occurrences = parsed.GetOccurrences<CalendarEvent>(start).TakeWhile(o => o.Period.StartTime.AsUtc < untilUtc).ToList(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warn($"반복 일정을 펼치지 못함: {ex.Message}"); yield break; }

        foreach (var o in occurrences)
        {
            if (o.Source is not CalendarEvent ev) continue;
            if (string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            var s = o.Period.StartTime;
            var e = o.Period.EffectiveEndTime ?? s;
            bool allDay = !s.HasTime;
            DateTime startLocal = allDay ? s.Date.ToDateTime(TimeOnly.MinValue) : s.AsUtc.ToLocalTime();
            DateTime endLocal = allDay ? e.Date.ToDateTime(TimeOnly.MinValue) : e.AsUtc.ToLocalTime();
            if (allDay && endLocal <= startLocal) endLocal = startLocal.AddDays(1);
            if (endLocal <= fromLocal) continue;                               // 이미 끝남
            if (startLocal >= (allDay ? allDayUntilLocal : timedUntilLocal)) continue;
            var uid = ev.Uid ?? ev.Summary ?? "";
            int n = perSeries.GetValueOrDefault(uid);
            if (n >= maxPerSeries) continue;
            perSeries[uid] = n + 1;
            var title = (ev.Summary ?? "").Trim();
            if (title.Length > 120) title = title[..120];
            yield return new CalendarItem(title.Length == 0 ? "(제목 없음)" : title, startLocal, endLocal, allDay, cal.Id, cal.Name, cal.Color);
        }
    }
}

/// <summary>
/// calendar 네임스페이스 — CalDAV 계정의 일정. 15분마다 다시 받는다. 받은 일정은 파일로 남겨 두어
/// 인터넷이 끊겨도 마지막으로 받은 일정을 보여 준다. 구독하려면 manifest에 "calendar" 권한이 있어야 한다.
///   calendar.events = { connected, ok, stale, error, updatedAt, events: [{ title, start, end, allDay, calendar, color }] }
///     start/end: 하루 종일이면 "yyyy-MM-dd"(end는 다음 날), 아니면 현지 시각 "yyyy-MM-ddTHH:mm"
/// </summary>
public sealed class CalendarProvider : IDataProvider
{
    static readonly TimeSpan Refresh = TimeSpan.FromMinutes(15), RetryAfterError = TimeSpan.FromMinutes(3);
    public const int TimedDays = 60, AllDayDays = 400, MaxEvents = 1500;

    readonly Func<CalendarAccount?> _account;
    readonly Func<IReadOnlyCollection<string>> _hidden;
    readonly string _cachePath;
    readonly HttpClient _http;
    DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    bool _lastOk, _dirty = true, _cacheLoaded;
    string? _accountKey;

    /// <summary>동기화가 끝날 때마다(성공·실패) 부른다. 캘린더 목록·오류를 Manager에 보여 주는 데 쓴다.</summary>
    public event Action<CalendarSyncResult>? Synced;

    public CalendarProvider(Func<CalendarAccount?> account, Func<IReadOnlyCollection<string>> hidden, string cachePath, HttpClient? http = null)
    {
        _account = account; _hidden = hidden; _cachePath = cachePath;
        _http = http ?? CreateClient();
    }

    public static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Darkroom/1.0");
        return c;
    }

    public string Namespace => "calendar";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    /// <summary>계정·숨긴 캘린더가 바뀌었을 때: 다음 주기에 바로 다시 받는다.</summary>
    public void Invalidate() => _dirty = true;

    public async Task UpdateAsync(IDataBus bus, CancellationToken ct)
    {
        var account = _account();
        var key = account == null ? null : $"{account.Server}|{account.Username}";
        if (key != _accountKey) { _accountKey = key; _dirty = true; _cacheLoaded = false; }

        if (account == null)
        {
            if (!_dirty) return;
            _dirty = false;
            TryDeleteCache();
            bus.Publish("calendar.events", new { connected = false, ok = false, stale = false, error = (string?)null, updatedAt = (string?)null, events = Array.Empty<object>() });
            return;
        }

        if (!_cacheLoaded)
        {
            _cacheLoaded = true;
            if (LoadCache(key!) is { } cached) bus.Publish("calendar.events", cached with { Stale = true });
        }

        var age = DateTimeOffset.Now - _lastAttempt;
        if (!_dirty && age < (_lastOk ? Refresh : RetryAfterError)) return;
        _dirty = false;
        _lastAttempt = DateTimeOffset.Now;

        var result = await SyncAsync(account, _hidden(), ct);
        _lastOk = result.Error == null;
        if (result.Error == null)
        {
            var payload = new CalendarPayload(true, true, false, null, result.UpdatedAt, result.Items.Select(ToJson).ToList());
            bus.Publish("calendar.events", payload);
            SaveCache(key!, payload);
        }
        else
        {
            // 받아 둔 일정이 있으면 그대로 두고 오류만 알린다
            var cached = LoadCache(key!);
            bus.Publish("calendar.events", (cached ?? new CalendarPayload(true, false, true, null, null, [])) with { Ok = false, Stale = true, Error = result.Error });
        }
        Synced?.Invoke(result);
    }

    public async Task<CalendarSyncResult> SyncAsync(CalendarAccount account, IReadOnlyCollection<string> hidden, CancellationToken ct)
    {
        var now = DateTime.Now;
        var from = now.Date.AddDays(-1);
        var timedUntil = now.Date.AddDays(TimedDays);
        var allDayUntil = now.Date.AddDays(AllDayDays);
        try
        {
            var client = new CalDavClient(_http, account);
            var calendars = await client.ListCalendarsAsync(ct);
            if (calendars.Count == 0) return new CalendarSyncResult(calendars, [], "캘린더를 찾지 못했어요.", Stamp());
            var items = new List<CalendarItem>();
            foreach (var cal in calendars.Where(c => !hidden.Contains(c.Id)))
            {
                foreach (var ics in await client.FetchAsync(cal, from, allDayUntil, ct))
                    items.AddRange(CalendarIcs.Expand(ics, cal, from, timedUntil, allDayUntil));
            }
            var sorted = items
                .DistinctBy(i => (i.Title, i.Start, i.CalendarId))
                .OrderBy(i => i.Start).ThenBy(i => i.AllDay ? 0 : 1)
                .Take(MaxEvents).ToList();
            Log.Info($"캘린더 동기화: 캘린더 {calendars.Count}개, 일정 {sorted.Count}개");
            return new CalendarSyncResult(calendars, sorted, null, Stamp());
        }
        catch (CalDavException ex)
        {
            Log.Warn($"캘린더 동기화 실패: {ex.Message}");
            return new CalendarSyncResult([], [], ex.Message, Stamp());
        }
    }

    static string Stamp() => DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    public static CalendarEventJson ToJson(CalendarItem i) => new(
        i.Title,
        i.AllDay ? i.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : i.Start.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
        i.AllDay ? i.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : i.End.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
        i.AllDay, i.Calendar, i.Color);

    // ---------- 받아 둔 일정(인터넷이 끊겼을 때·앱을 다시 켰을 때) ----------
    sealed record CacheFile(string Account, CalendarPayload Payload);

    CalendarPayload? LoadCache(string account)
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath), DrJson.Options);
            return c != null && c.Account == account ? c.Payload : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    void SaveCache(string account, CalendarPayload payload)
    {
        try { DrJson.WriteAtomic(_cachePath, new CacheFile(account, payload)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"캘린더 캐시 저장 실패: {ex.Message}"); }
    }

    void TryDeleteCache()
    {
        try { if (File.Exists(_cachePath)) File.Delete(_cachePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public sealed record CalendarEventJson(string Title, string Start, string End, bool AllDay, string Calendar, string? Color);
public sealed record CalendarPayload(bool Connected, bool Ok, bool Stale, string? Error, string? UpdatedAt, List<CalendarEventJson> Events);
public sealed record CalendarSyncResult(List<CalendarInfo> Calendars, List<CalendarItem> Items, string? Error, string UpdatedAt);
