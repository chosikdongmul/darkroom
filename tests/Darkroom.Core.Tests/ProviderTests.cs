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
}
