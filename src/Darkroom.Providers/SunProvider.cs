using Darkroom.Core;

namespace Darkroom.Providers;

/// <summary>
/// 태양 위치·시각 계산 (네트워크 없이 로컬 계산). 천문 공식은 NOAA/Meeus 계열의 간이식으로 오차 수 분 이내.
/// 각도: 고도는 수평선 기준(°), 방위는 북=0° 시계 방향.
/// </summary>
public static class SunMath
{
    const double Rad = Math.PI / 180, DayMs = 86_400_000, J1970 = 2440588, J2000 = 2451545, J0 = 0.0009;
    const double Obliquity = Rad * 23.4397;

    static double ToDays(DateTimeOffset t) => t.ToUnixTimeMilliseconds() / DayMs - 0.5 + J1970 - J2000;
    static DateTimeOffset? FromJulian(double j) =>
        double.IsNaN(j) ? null : DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round((j + 0.5 - J1970) * DayMs));

    static double MeanAnomaly(double d) => Rad * (357.5291 + 0.98560028 * d);
    static double EclipticLongitude(double m)
    {
        double c = Rad * (1.9148 * Math.Sin(m) + 0.02 * Math.Sin(2 * m) + 0.0003 * Math.Sin(3 * m));
        return m + c + Rad * 102.9372 + Math.PI;
    }
    static double Declination(double l) => Math.Asin(Math.Sin(Obliquity) * Math.Sin(l));
    static double RightAscension(double l) => Math.Atan2(Math.Sin(l) * Math.Cos(Obliquity), Math.Cos(l));
    static double SiderealTime(double d, double lw) => Rad * (280.16 + 360.9856235 * d) - lw;

    /// <summary>(고도°, 방위°)</summary>
    public static (double altitude, double azimuth) Position(DateTimeOffset t, double lat, double lon)
    {
        double lw = Rad * -lon, phi = Rad * lat, d = ToDays(t);
        double l = EclipticLongitude(MeanAnomaly(d));
        double dec = Declination(l), ra = RightAscension(l);
        double h = SiderealTime(d, lw) - ra;
        double alt = Math.Asin(Math.Sin(phi) * Math.Sin(dec) + Math.Cos(phi) * Math.Cos(dec) * Math.Cos(h));
        double az = Math.Atan2(Math.Sin(h), Math.Cos(h) * Math.Sin(phi) - Math.Tan(dec) * Math.Cos(phi)); // 남=0, 서쪽 +
        return (alt / Rad, (az / Rad + 180 + 360) % 360);
    }

    /// <summary>해가 고도 angle°를 지나는 아침·저녁 시각. 그 고도에 닿지 않는 날(백야·극야)은 null.</summary>
    public static (DateTimeOffset? morning, DateTimeOffset? evening, DateTimeOffset noon) Crossing(DateOnly date, double angle, double lat, double lon, TimeZoneInfo tz)
    {
        // 그 지역 날짜의 정오 근처로 주기를 고른다.
        var localNoon = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), tz.GetUtcOffset(date.ToDateTime(new TimeOnly(12, 0))));
        double lw = Rad * -lon, phi = Rad * lat, d = ToDays(localNoon);
        double n = Math.Round(d - J0 - lw / (2 * Math.PI));
        double ds = J0 + lw / (2 * Math.PI) + n;
        double m = MeanAnomaly(ds), l = EclipticLongitude(m), dec = Declination(l);
        double jNoon = J2000 + ds + 0.0053 * Math.Sin(m) - 0.0069 * Math.Sin(2 * l);

        double cosW = (Math.Sin(angle * Rad) - Math.Sin(phi) * Math.Sin(dec)) / (Math.Cos(phi) * Math.Cos(dec));
        var noon = FromJulian(jNoon)!.Value.ToOffset(localNoon.Offset);
        if (cosW is < -1 or > 1) return (null, null, noon);
        double w = Math.Acos(cosW);
        double a = J0 + (w + lw) / (2 * Math.PI) + n;
        double jSet = J2000 + a + 0.0053 * Math.Sin(m) - 0.0069 * Math.Sin(2 * l);
        double jRise = jNoon - (jSet - jNoon);
        return (FromJulian(jRise)?.ToOffset(localNoon.Offset), FromJulian(jSet)?.ToOffset(localNoon.Offset), noon);
    }

    /// <summary>사진가 기준 구간. 골든아워 -4°~6°, 블루아워 -8°~-4°.</summary>
    public static string Phase(double altitude) => altitude switch
    {
        >= 6 => "day",
        >= -4 => "goldenHour",
        >= -8 => "blueHour",
        _ => "night",
    };
}

/// <summary>
/// sun 네임스페이스 (1분 주기, 위치 설정이 바뀌면 바로)
///   sun.today    = { date, location, sunrise, sunset, solarNoon, dawn, dusk,
///                    morning: { blueStart, goldenStart, goldenEnd }, evening: { goldenStart, blueStart, blueEnd } }  (ISO 문자열, 없으면 null)
///   sun.tomorrow = sun.today와 같은 형태(내일). 오늘 저녁이 지난 뒤 "다음 골든아워"를 찾을 때 쓴다.
///   sun.position = { altitude, azimuth, altitudeIn30Min }
///   sun.phase    = "day" | "goldenHour" | "blueHour" | "night"
///   sun.goldenHourStart / sun.goldenHourEnd = 오늘 저녁 골든아워 시작 / 오늘 아침 골든아워 끝
/// </summary>
public sealed class SunProvider(Func<ProviderSettings> settings) : IDataProvider
{
    public string Namespace => "sun";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    static string? Iso(DateTimeOffset? t) => t?.ToString("yyyy-MM-ddTHH:mm:sszzz");

    /// <summary>하루치 시각 (sun.today / sun.tomorrow 형태)</summary>
    public static object Day(DateOnly date, ProviderSettings s, TimeZoneInfo tz)
    {
        var rise = SunMath.Crossing(date, -0.833, s.Latitude, s.Longitude, tz);
        var civil = SunMath.Crossing(date, -6, s.Latitude, s.Longitude, tz);
        var blue = SunMath.Crossing(date, -8, s.Latitude, s.Longitude, tz);
        var gold = SunMath.Crossing(date, -4, s.Latitude, s.Longitude, tz);
        var high = SunMath.Crossing(date, 6, s.Latitude, s.Longitude, tz);
        return new
        {
            date = date.ToString("yyyy-MM-dd"),
            location = new { name = s.LocationName, latitude = s.Latitude, longitude = s.Longitude },
            sunrise = Iso(rise.morning),
            sunset = Iso(rise.evening),
            solarNoon = Iso(rise.noon),
            dawn = Iso(civil.morning),
            dusk = Iso(civil.evening),
            morning = new { blueStart = Iso(blue.morning), goldenStart = Iso(gold.morning), goldenEnd = Iso(high.morning) },
            evening = new { goldenStart = Iso(high.evening), blueStart = Iso(gold.evening), blueEnd = Iso(blue.evening) },
        };
    }

    public Task UpdateAsync(IDataBus bus, CancellationToken ct)
    {
        var s = settings();
        var tz = TimeZoneInfo.Local;
        var now = DateTimeOffset.Now;
        var date = DateOnly.FromDateTime(now.LocalDateTime);

        bus.Publish("sun.today", Day(date, s, tz));
        bus.Publish("sun.tomorrow", Day(date.AddDays(1), s, tz));
        var high = SunMath.Crossing(date, 6, s.Latitude, s.Longitude, tz);
        bus.Publish("sun.goldenHourStart", Iso(high.evening));
        bus.Publish("sun.goldenHourEnd", Iso(high.morning));

        var (alt, az) = SunMath.Position(now, s.Latitude, s.Longitude);
        var (alt30, _) = SunMath.Position(now.AddMinutes(30), s.Latitude, s.Longitude);
        bus.Publish("sun.position", new { altitude = Math.Round(alt, 1), azimuth = Math.Round(az, 1), altitudeIn30Min = Math.Round(alt30, 1) });
        bus.Publish("sun.phase", SunMath.Phase(alt));
        return Task.CompletedTask;
    }
}
