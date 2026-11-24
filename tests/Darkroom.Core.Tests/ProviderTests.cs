using System.Text.Json;
using Darkroom.Core;
using Darkroom.Providers;

namespace Darkroom.Core.Tests;

public class SunMathTests
{
    static readonly TimeZoneInfo Kst = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");

    // 서울(37.57, 126.98) 공표값: 하지 일출 05:11 / 일몰 19:57, 동지 일출 07:43 / 일몰 17:17
    [Theory]
    [InlineData(2026, 6, 21, "05:11", "19:57")]
    [InlineData(2026, 12, 21, "07:43", "17:17")]
    public void Seoul_sunrise_and_sunset_within_4_minutes(int y, int m, int d, string rise, string set)
    {
        var date = new DateOnly(y, m, d);
        var (morning, evening, _) = SunMath.Crossing(date, -0.833, 37.57, 126.98, Kst);
        AssertNear(date, rise, morning!.Value);
        AssertNear(date, set, evening!.Value);
    }

    static void AssertNear(DateOnly date, string hhmm, DateTimeOffset actual)
    {
        var expected = new DateTimeOffset(date.ToDateTime(TimeOnly.Parse(hhmm)), TimeSpan.FromHours(9));
        var diff = Math.Abs((actual - expected).TotalMinutes);
        Assert.True(diff <= 4, $"expected {expected:HH:mm}, got {actual:HH:mm} (diff {diff:F1} min)");
        Assert.Equal(TimeSpan.FromHours(9), actual.Offset);
    }

    [Fact]
    public void Golden_hour_brackets_sunset_and_polar_day_has_no_sunset()
    {
        var date = new DateOnly(2026, 6, 21);
        var gold = SunMath.Crossing(date, 6, 37.57, 126.98, Kst).evening!.Value;
        var sunset = SunMath.Crossing(date, -0.833, 37.57, 126.98, Kst).evening!.Value;
        Assert.InRange((sunset - gold).TotalMinutes, 30, 80);

        var tromso = SunMath.Crossing(date, -0.833, 69.65, 18.96, TimeZoneInfo.Utc); // 백야
        Assert.Null(tromso.morning);
        Assert.Null(tromso.evening);
    }

    [Theory]
    [InlineData(30, "day")]
    [InlineData(2, "goldenHour")]
    [InlineData(-5, "blueHour")]
    [InlineData(-12, "night")]
    public void Phase_by_altitude(double alt, string phase) => Assert.Equal(phase, SunMath.Phase(alt));

    [Fact]
    public void Noon_altitude_in_seoul_summer_is_high_and_south()
    {
        var noon = SunMath.Crossing(new DateOnly(2026, 6, 21), -0.833, 37.57, 126.98, Kst).noon;
        var (alt, az) = SunMath.Position(noon, 37.57, 126.98);
        Assert.InRange(alt, 74, 77);     // 90 - 37.57 + 23.44 ≈ 75.9
        Assert.InRange(az, 170, 190);    // 남쪽
    }
}

public class ExifTests
{
    /// <summary>EXIF(리틀 엔디안 TIFF) + SOF0만 있는 최소 JPEG.</summary>
    static byte[] MakeJpeg()
    {
        var t = new List<byte>();
        void U16(int v) { t.Add((byte)v); t.Add((byte)(v >> 8)); }
        void U32(long v) { for (int i = 0; i < 4; i++) t.Add((byte)(v >> (8 * i))); }
        var make = "FUJIFILM\0"u8.ToArray();
        var model = "X-T5\0"u8.ToArray();
        var dt = "2026:11:23 17:40:00\0"u8.ToArray();

        t.AddRange("II"u8.ToArray()); U16(42); U32(8);
        int makeOff = 8 + 2 + 3 * 12 + 4, modelOff = makeOff + make.Length, exifOff = modelOff + model.Length;
        U16(3);
        U16(0x010F); U16(2); U32(make.Length); U32(makeOff);
        U16(0x0110); U16(2); U32(model.Length); U32(modelOff);
        U16(0x8769); U16(4); U32(1); U32(exifOff);
        U32(0);
        t.AddRange(make); t.AddRange(model);

        int data = exifOff + 2 + 5 * 12 + 4;
        U16(5);
        U16(0x829A); U16(5); U32(1); U32(data);        // 1/250
        U16(0x829D); U16(5); U32(1); U32(data + 8);    // f/2.8
        U16(0x8827); U16(3); U32(1); U16(400); U16(0); // ISO 400 (값이 4바이트 안에)
        U16(0x920A); U16(5); U32(1); U32(data + 16);   // 35mm
        U16(0x9003); U16(2); U32(dt.Length); U32(data + 24);
        U32(0);
        U32(1); U32(250); U32(28); U32(10); U32(35); U32(1); t.AddRange(dt);

        var j = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1 };
        int len = 2 + 6 + t.Count;
        j.Add((byte)(len >> 8)); j.Add((byte)len);
        j.AddRange("Exif\0\0"u8.ToArray()); j.AddRange(t);
        j.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x0F, 0xA0, 0x17, 0x70, 0x03, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 });
        j.AddRange(new byte[] { 0xFF, 0xD9 });
        return j.ToArray();
    }

    [Fact]
    public void Reads_camera_exposure_and_size()
    {
        var e = ExifReader.Read(MakeJpeg())!;
        Assert.Equal("FUJIFILM", e.Make);
        Assert.Equal("X-T5", e.Model);
        Assert.Equal("1/250", e.Shutter);
        Assert.Equal("f/2.8", e.Aperture);
        Assert.Equal(400, e.Iso);
        Assert.Equal(35, e.FocalLength);
        Assert.Equal("2026-11-23T17:40:00", e.TakenAt);
        Assert.Equal(6000, e.Width);
        Assert.Equal(4000, e.Height);
    }

    [Fact]
    public void Truncated_or_non_jpeg_input_does_not_throw()
    {
        var jpeg = MakeJpeg();
        for (int n = 0; n < jpeg.Length; n += 7) ExifReader.Read(jpeg.AsSpan(0, n));
        Assert.Null(ExifReader.Read("not a jpeg"u8));
    }
}

public class WallpaperTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "darkroom-wall-" + Guid.NewGuid().ToString("N"));
    public WallpaperTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Settings_validation()
    {
        Assert.Null(new WallpaperSettings().Validate());
        Assert.NotNull(new WallpaperSettings { IntervalMinutes = 0 }.Validate());
        Assert.NotNull(new WallpaperSettings { Fit = "tile" }.Validate());
    }

    [Fact]
    public void Reads_png_and_bmp_size_and_lists_only_wallpaper_formats()
    {
        // PNG: 시그니처 + IHDR(가로 2880, 세로 1800)
        var png = new byte[32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 2880);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 1800);
        File.WriteAllBytes(Path.Combine(_dir, "a.png"), png);
        // BMP: 'BM' + 가로 640, 세로 -480(위에서 아래로 저장)
        var bmp = new byte[32]; bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 640);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -480);
        File.WriteAllBytes(Path.Combine(_dir, "b.bmp"), bmp);
        File.WriteAllText(Path.Combine(_dir, "c.webp"), "x"); // 배경화면 형식 아님

        Assert.Equal((2880, 1800), PhotoLibrary.TryReadSize(Path.Combine(_dir, "a.png")));
        Assert.Equal((640, 480), PhotoLibrary.TryReadSize(Path.Combine(_dir, "b.bmp")));
        var files = PhotoLibrary.Enumerate(_dir, PhotoLibrary.WallpaperExtensions).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["a.png", "b.bmp"], files);
    }
}

public class PhotoPackTests
{
    [Fact]
    public void Shot_stats_count_today_week_month_and_ignore_future()
    {
        var now = new DateTime(2026, 11, 25, 15, 0, 0); // 수요일
        var times = new[]
        {
            now.AddHours(-1),                    // 오늘
            now.Date.AddMinutes(1),              // 오늘 00:01
            new DateTime(2026, 11, 23, 9, 0, 0), // 월요일 → 이번 주
            new DateTime(2026, 11, 22, 9, 0, 0), // 일요일 → 지난주, 이번 달
            new DateTime(2026, 10, 31, 9, 0, 0), // 지난달
            now.AddDays(3),                      // 미래(카메라 시계 오류) → 기간 집계 제외
        };
        var s = DrJson.ToElement(PhotoStats.Compute(times, now));
        Assert.Equal(6, s.GetProperty("total").GetInt32());
        Assert.Equal(2, s.GetProperty("today").GetInt32());
        Assert.Equal(3, s.GetProperty("week").GetInt32());
        Assert.Equal(4, s.GetProperty("month").GetInt32());
        Assert.Equal("2026-11-25T14:00:00", s.GetProperty("lastShotAt").GetString());
    }

    [Fact]
    public void Histogram_bins_channels_and_reports_clipping()
    {
        // 픽셀 4개(BGRA): 순수 검정, 순수 흰색, 순수 빨강, 중간 회색
        byte[] px = [0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 128, 128, 128, 255];
        var h = DrJson.ToElement(Histogram.Compute(px));
        Assert.Equal(64, h.GetProperty("r").GetArrayLength());
        Assert.Equal(25.0, h.GetProperty("shadowsClipped").GetDouble());    // 검정 1/4
        Assert.Equal(25.0, h.GetProperty("highlightsClipped").GetDouble()); // 흰색 1/4
        Assert.True(h.GetProperty("r")[63].GetInt32() > 0);                 // 빨강·흰색의 R=255
        Assert.True(h.GetProperty("g")[32].GetInt32() > 0);                 // 회색 128 → 32번 칸
        Assert.Equal(1000, h.GetProperty("l")[32].GetInt32());              // 가운데 칸 최댓값으로 정규화
    }

    [Fact]
    public void Sun_day_object_contains_tomorrow_shape()
    {
        var s = new ProviderSettings();
        var day = DrJson.ToElement(SunProvider.Day(new DateOnly(2026, 11, 24), s, TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time")));
        Assert.Equal("2026-11-24", day.GetProperty("date").GetString());
        Assert.StartsWith("2026-11-24T", day.GetProperty("evening").GetProperty("goldenStart").GetString());
    }
}

public class BusAndHostTests
{
    [Fact]
    public void Identical_values_are_not_republished()
    {
        var bus = new DataBus();
        int calls = 0;
        using var _ = bus.Subscribe("battery.status", (_, _) => calls++);
        bus.Publish("battery.status", new { percent = 80 });
        bus.Publish("battery.status", new { percent = 80 });
        bus.Publish("battery.status", new { percent = 79 });
        Assert.Equal(2, calls);
    }

    sealed class SlowProvider : IDataProvider
    {
        public int Calls;
        public string Namespace => "slow";
        public TimeSpan Interval => TimeSpan.FromSeconds(30);
        public Task UpdateAsync(IDataBus bus, CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Kick_updates_immediately_instead_of_waiting_for_interval()
    {
        var bus = new DataBus();
        var p = new SlowProvider();
        using var host = new ProviderHost(bus, [p]);
        using var sub = bus.Subscribe("slow.x", (_, _) => { });
        await Task.Delay(200);
        Assert.Equal(1, p.Calls);
        host.Kick("slow");
        await Task.Delay(200);
        Assert.Equal(2, p.Calls);
    }

    [Fact]
    public void Weather_response_is_parsed()
    {
        var json = """
        { "current": { "time": "2026-11-23T17:45", "temperature_2m": 6.2, "relative_humidity_2m": 55, "apparent_temperature": 3.1,
                       "weather_code": 3, "wind_speed_10m": 9.4, "cloud_cover": 90, "is_day": 0 },
          "daily": { "time": ["2026-11-23", "2026-11-24"], "temperature_2m_max": [9.1, 8], "temperature_2m_min": [1.5, 0.2],
                     "weather_code": [3, 61], "sunrise": ["2026-11-23T07:20", "2026-11-24T07:21"], "sunset": ["2026-11-23T17:16", "2026-11-24T17:16"] } }
        """;
        WeatherProvider.Parse(JsonDocument.Parse(json).RootElement, out var current, out var daily);
        var c = DrJson.ToElement(current);
        Assert.Equal("흐림", c.GetProperty("description").GetString());
        Assert.False(c.GetProperty("isDay").GetBoolean());
        var d = DrJson.ToElement(daily);
        Assert.Equal(2, d.GetArrayLength());
        Assert.Equal("약한 비", d[1].GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "ZuneMusic")]
    [InlineData("chrome", "chrome")]
    public void Media_app_names_are_shortened(string aumid, string name) => Assert.Equal(name, MediaProvider.FriendlyApp(aumid));

    [Fact]
    public void Provider_settings_validation()
    {
        Assert.Null(new ProviderSettings().Validate());
        Assert.NotNull(new ProviderSettings { Latitude = 91 }.Validate());
        Assert.NotNull(new ProviderSettings { PhotosIntervalSeconds = 1 }.Validate());
        Assert.NotNull(new ProviderSettings { PhotosFolder = @"relative\path" }.Validate());
    }

    [Theory]
    [InlineData(0, 50, 40, 20000, 0, 90)]     // 중층 구름이 캔버스, 지평선 맑음
    [InlineData(0, 0, 0, 20000, 0, 35)]       // 구름 없음: 맑지만 밋밋함
    [InlineData(90, 50, 40, 20000, 0, 18)]    // 하층 구름이 지평선을 막음
    [InlineData(0, 50, 40, 20000, 80, 36)]    // 비 올 확률 높음
    [InlineData(0, 100, 100, 3000, 0, 21)]    // 꽉 찬 구름 + 낮은 시정
    public void Sunset_score(double low, double mid, double high, double vis, double rain, int expected) =>
        Assert.Equal(expected, WeatherProvider.SunsetScore(low, mid, high, vis, rain));

    [Fact]
    public void List_setting_types_validate_items()
    {
        JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        var dates = new SettingSpec { Type = "dates", MaxItems = 2 };
        Assert.True(dates.Accepts(J("""[{"date":"2026-12-25","name":"크리스마스","yearly":true,"pinned":false}]""")));
        Assert.False(dates.Accepts(J("""[{"date":"2026-13-40","name":"x"}]""")));          // 없는 날짜
        Assert.False(dates.Accepts(J("""[{"date":"2026-12-25"}]""")));                     // 이름 없음
        Assert.False(dates.Accepts(J("""[{"date":"2026-01-01","name":"a"},{"date":"2026-01-02","name":"b"},{"date":"2026-01-03","name":"c"}]"""))); // 최대 개수 초과

        var links = new SettingSpec { Type = "links" };
        Assert.True(links.Accepts(J("""[{"name":"메모장","target":"C:\\Windows\\notepad.exe"}]""")));
        Assert.False(links.Accepts(J("""[{"name":"빈 경로","target":""}]""")));

        var nums = new SettingSpec { Type = "numbers", Min = 1, Max = 600 };
        Assert.True(nums.Accepts(J("[5, 25, 60]")));
        Assert.False(nums.Accepts(J("[0]")));
        Assert.False(nums.Accepts(J("\"5,25\"")));
    }

    [Fact]
    public void Audio_spectrum_peaks_at_the_tone()
    {
        const int rate = 48000;
        var samples = Enumerable.Range(0, 2048).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / rate))).ToArray();
        var bands = AudioProvider.Spectrum(samples, rate);
        Assert.Equal(AudioProvider.Bands, bands.Length);
        int peak = Array.IndexOf(bands, bands.Max());
        // 1kHz는 40Hz~16kHz 로그 눈금에서 32칸 중 17번째 근처
        Assert.InRange(peak, 15, 18);
        Assert.True(bands[peak] > 0.8);
        Assert.True(bands[2] < 0.3);
        Assert.All(AudioProvider.Spectrum(new float[2048], rate), v => Assert.Equal(0, v));
    }

    [Fact]
    public void Sunset_forecast_uses_the_next_sunset_hour()
    {
        var json = """
        { "daily": { "sunset": ["2026-11-24T17:16", "2026-11-25T17:16"] },
          "hourly": { "time": ["2026-11-24T16:00", "2026-11-24T17:00", "2026-11-25T17:00"],
                      "cloud_cover_low": [0, 10, 80], "cloud_cover_mid": [0, 50, 0], "cloud_cover_high": [0, 20, 0],
                      "visibility": [24000, 24000, 24000], "precipitation_probability": [0, 0, 0] } }
        """;
        var root = JsonDocument.Parse(json).RootElement;
        var today = DrJson.ToElement(WeatherProvider.SunsetForecast(root, new DateTime(2026, 11, 24, 12, 0, 0)));
        Assert.Equal("17:16", today.GetProperty("time").GetString());
        Assert.Equal(90, today.GetProperty("score").GetInt32());
        // 오늘 일몰이 지나면 내일 일몰
        var next = DrJson.ToElement(WeatherProvider.SunsetForecast(root, new DateTime(2026, 11, 24, 18, 0, 0)));
        Assert.Equal("2026-11-25", next.GetProperty("date").GetString());
        Assert.Equal(7, next.GetProperty("score").GetInt32());
    }
}

public class CalendarTests
{
    /// <summary>CalDAV 서버 흉내: 경로·메서드별로 정해 둔 응답을 돌려주고 받은 요청을 남긴다.</summary>
    sealed class FakeCalDav : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? Auth, string? Depth)> Requests = new();
        public bool RejectAuth;
        public string Ics = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct);
            Requests.Add((req.Method.Method, req.RequestUri!.AbsolutePath, req.Headers.Authorization?.ToString(), req.Headers.TryGetValues("Depth", out var d) ? d.First() : null));
            if (RejectAuth) return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
            string xml = (req.Method.Method, req.RequestUri.AbsolutePath) switch
            {
                ("PROPFIND", "/") => """<d:multistatus xmlns:d="DAV:"><d:response><d:href>/</d:href><d:propstat><d:prop><d:current-user-principal><d:href>/principals/tester/</d:href></d:current-user-principal></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>""",
                ("PROPFIND", "/principals/tester/") => """<d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:response><d:href>/principals/tester/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>/calendars/tester/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>""",
                ("PROPFIND", "/calendars/tester/") => """
                    <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:a="http://apple.com/ns/ical/">
                      <d:response><d:href>/calendars/tester/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                      <d:response><d:href>/calendars/tester/home/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/><c:calendar/></d:resourcetype><d:displayname>내 캘린더</d:displayname>
                        <c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set><a:calendar-color>#2DB400FF</a:calendar-color></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                      <d:response><d:href>/calendars/tester/todo/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/><c:calendar/></d:resourcetype><d:displayname>할 일</d:displayname>
                        <c:supported-calendar-component-set><c:comp name="VTODO"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    </d:multistatus>
                    """,
                ("REPORT", "/calendars/tester/home/") => $"""<d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:response><d:href>/calendars/tester/home/1.ics</d:href><d:propstat><d:prop><c:calendar-data>{System.Security.SecurityElement.Escape(Ics)}</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>""",
                _ => "",
            };
            if (xml.Length == 0) return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            return new HttpResponseMessage((System.Net.HttpStatusCode)207) { Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml") };
        }
    }

    static string Ics(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:test\r\n" + string.Concat(events.Select(e => "BEGIN:VEVENT\r\n" + e + "END:VEVENT\r\n")) + "END:VCALENDAR\r\n";

    static string D(DateTime d) => d.ToString("yyyyMMdd");

    [Fact]
    public async Task Discovers_event_calendars_and_reads_events_with_basic_auth()
    {
        var today = DateTime.Today;
        var fake = new FakeCalDav
        {
            Ics = Ics($"UID:a\r\nSUMMARY:엄마 생신\r\nDTSTART;VALUE=DATE:{D(today.AddDays(3).AddYears(-5))}\r\nDTEND;VALUE=DATE:{D(today.AddDays(4).AddYears(-5))}\r\nRRULE:FREQ=YEARLY\r\n")
        };
        var http = new HttpClient(fake);
        var provider = new CalendarProvider(() => null, () => Array.Empty<string>(), Path.GetTempFileName(), http);
        var r = await provider.SyncAsync(new CalendarAccount("caldav.example.com", "tester", "app-pw"), Array.Empty<string>(), CancellationToken.None);

        Assert.Null(r.Error);
        var cal = Assert.Single(r.Calendars);                       // 할 일 목록(VTODO)은 뺀다
        Assert.Equal("내 캘린더", cal.Name);
        Assert.Equal("#2DB400", cal.Color);                         // #RRGGBBAA → #RRGGBB
        Assert.Equal(2, r.Items.Count);                             // 하루 종일 일정은 400일 앞까지 → 올해와 내년
        var ev = r.Items[0];
        Assert.Equal("엄마 생신", ev.Title);
        Assert.True(ev.AllDay);
        Assert.Equal(today.AddDays(3), ev.Start);                   // 매년 반복 → 올해 날짜
        Assert.All(fake.Requests, q => Assert.Equal("Basic " + Convert.ToBase64String("tester:app-pw"u8.ToArray()), q.Auth));
        Assert.Contains(fake.Requests, q => q.Method == "REPORT" && q.Depth == "1");

        var json = CalendarProvider.ToJson(ev);
        Assert.Equal(today.AddDays(3).ToString("yyyy-MM-dd"), json.Start); // 하루 종일: 날짜만
    }

    [Fact]
    public async Task Wrong_password_gives_a_plain_message()
    {
        var fake = new FakeCalDav { RejectAuth = true };
        var provider = new CalendarProvider(() => null, () => Array.Empty<string>(), Path.GetTempFileName(), new HttpClient(fake));
        var r = await provider.SyncAsync(new CalendarAccount("https://caldav.example.com", "tester", "wrong"), Array.Empty<string>(), CancellationToken.None);
        Assert.Contains("비밀번호", r.Error);
        Assert.Single(fake.Requests); // 인증 실패면 다른 주소로 다시 시도하지 않는다
    }

    [Fact]
    public void Server_must_be_https()
    {
        Assert.Equal("https://caldav.calendar.naver.com/", CalDavClient.NormalizeServer("caldav.calendar.naver.com").AbsoluteUri);
        Assert.Throws<CalDavException>(() => CalDavClient.NormalizeServer("http://caldav.example.com"));
    }

    [Fact]
    public void Expands_weekly_events_skips_cancelled_and_converts_to_local_time()
    {
        var monday = DateTime.Today.AddDays(((int)DayOfWeek.Monday - (int)DateTime.Today.DayOfWeek + 7) % 7 + 7); // 다음다음 주 월요일 이후
        var ics = Ics(
            $"UID:w\r\nSUMMARY:주간 회의\r\nDTSTART:{monday.ToUniversalTime().AddHours(10):yyyyMMdd'T'HHmmss'Z'}\r\nDURATION:PT1H\r\nRRULE:FREQ=WEEKLY;COUNT=3\r\n",
            $"UID:x\r\nSUMMARY:취소된 약속\r\nSTATUS:CANCELLED\r\nDTSTART;VALUE=DATE:{D(DateTime.Today.AddDays(2))}\r\n");
        var cal = new CalendarInfo("https://x/cal/", "내 캘린더", null);
        var items = CalendarIcs.Expand(ics, cal, DateTime.Today, DateTime.Today.AddDays(60), DateTime.Today.AddDays(400)).ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal("주간 회의", i.Title));
        Assert.Equal(monday.ToUniversalTime().AddHours(10).ToLocalTime(), items[0].Start);
        Assert.Equal(TimeSpan.FromHours(1), items[0].End - items[0].Start);
        Assert.Equal(TimeSpan.FromDays(7), items[1].Start - items[0].Start);
    }
}
