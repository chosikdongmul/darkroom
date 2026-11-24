using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Darkroom.Core;
using Darkroom.Providers;

namespace Darkroom.App;

/// <summary>캘린더 연결(CalDAV).</summary>
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
}
