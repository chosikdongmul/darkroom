using System.Text.Json;
using Darkroom.Core;

namespace Darkroom.Providers;

/// <summary>
/// weather 네임스페이스 — Open-Meteo(키 불필요). 15분마다, 위치가 바뀌면 바로 다시 받는다.
/// 이 데이터를 구독하는 위젯은 manifest에 "network" 권한이 있어야 한다(위치가 외부로 전송되므로).
///   weather.current = { temperature, apparentTemperature, humidity, windSpeed, cloudCover, code, description, isDay, time }
///   weather.daily   = [{ date, max, min, code, description, sunrise, sunset }] (3일)
///   weather.sunset  = { date, time, score, label, low, mid, high } 다음 일몰의 노을 점수(없으면 null)
///   weather.rain    = { kind, startsAt, inMinutes, probability, mm, now } 3시간 안의 비·눈 소식(없으면 null)
///   weather.status  = { ok, updatedAt, error, location }
/// </summary>
public sealed class WeatherProvider(Func<ProviderSettings> settings, HttpClient? http = null) : IDataProvider
{
    static readonly TimeSpan Refresh = TimeSpan.FromMinutes(15), RetryAfterError = TimeSpan.FromMinutes(2);
    readonly HttpClient _http = http ?? CreateClient();
    DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    bool _lastOk;
    (double, double) _lastCoords = (double.NaN, double.NaN);

    public string Namespace => "weather";
    public TimeSpan Interval => TimeSpan.FromMinutes(1); // 1분마다 확인만, 실제 요청은 Refresh 주기

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Darkroom/0.1");
        return c;
    }

    public async Task UpdateAsync(IDataBus bus, CancellationToken ct)
    {
        var s = settings();
        var coords = (Math.Round(s.Latitude, 3), Math.Round(s.Longitude, 3));
        var age = DateTimeOffset.Now - _lastAttempt;
        bool due = coords != _lastCoords || age >= (_lastOk ? Refresh : RetryAfterError);
        if (!due) return;
        _lastAttempt = DateTimeOffset.Now;
        _lastCoords = coords;

        var url = FormattableString.Invariant(
            $"https://api.open-meteo.com/v1/forecast?latitude={coords.Item1}&longitude={coords.Item2}") +
            "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,cloud_cover,is_day" +
            "&daily=temperature_2m_max,temperature_2m_min,weather_code,sunrise,sunset" +
            "&hourly=cloud_cover_low,cloud_cover_mid,cloud_cover_high,visibility,precipitation_probability,precipitation,weather_code&timezone=auto&forecast_days=3";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            Parse(doc.RootElement, out var current, out var daily);
            bus.Publish("weather.current", current);
            bus.Publish("weather.daily", daily);
            bus.Publish("weather.sunset", SunsetForecast(doc.RootElement, DateTime.Now));
            bus.Publish("weather.rain", RainForecast(doc.RootElement, DateTime.Now));
            bus.Publish("weather.status", new { ok = true, updatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"), error = (string?)null, location = s.LocationName });
            _lastOk = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            _lastOk = false;
            Log.Warn($"날씨 요청 실패: {ex.Message}");
            bus.Publish("weather.status", new { ok = false, updatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"), error = ex.Message, location = s.LocationName });
        }
    }

    /// <summary>Open-Meteo 응답 → 게시할 값. 테스트에서 직접 부른다.</summary>
    public static void Parse(JsonElement root, out object current, out object daily)
    {
        var c = root.GetProperty("current");
        int code = c.GetProperty("weather_code").GetInt32();
        current = new
        {
            temperature = c.GetProperty("temperature_2m").GetDouble(),
            apparentTemperature = c.GetProperty("apparent_temperature").GetDouble(),
            humidity = c.GetProperty("relative_humidity_2m").GetDouble(),
            windSpeed = c.GetProperty("wind_speed_10m").GetDouble(),
            cloudCover = c.GetProperty("cloud_cover").GetDouble(),
            code,
            description = Describe(code),
            isDay = c.GetProperty("is_day").GetInt32() == 1,
            time = c.GetProperty("time").GetString(),
        };
        var d = root.GetProperty("daily");
        var dates = d.GetProperty("time").EnumerateArray().Select(x => x.GetString()).ToArray();
        var list = new List<object>();
        for (int i = 0; i < dates.Length; i++)
        {
            int dc = d.GetProperty("weather_code")[i].GetInt32();
            list.Add(new
            {
                date = dates[i],
                max = d.GetProperty("temperature_2m_max")[i].GetDouble(),
                min = d.GetProperty("temperature_2m_min")[i].GetDouble(),
                code = dc,
                description = Describe(dc),
                sunrise = d.GetProperty("sunrise")[i].GetString(),
                sunset = d.GetProperty("sunset")[i].GetString(),
            });
        }
        daily = list;
    }

    /// <summary>다음 일몰 시각의 하늘로 매긴 노을 점수. 시간별 예보가 없으면 null.
    /// { date, time, score(0~100), label, low, mid, high }</summary>
    public static object? SunsetForecast(JsonElement root, DateTime now)
    {
        if (!root.TryGetProperty("hourly", out var h) || !root.TryGetProperty("daily", out var d)) return null;
        DateTime? sunset = null;
        foreach (var s in d.GetProperty("sunset").EnumerateArray())
            if (DateTime.TryParse(s.GetString(), out var t) && t > now) { sunset = t; break; }
        if (sunset is not { } ss) return null;

        var times = h.GetProperty("time").EnumerateArray().Select(x => DateTime.TryParse(x.GetString(), out var t) ? t : DateTime.MinValue).ToList();
        int i = -1; double best = double.MaxValue;
        for (int k = 0; k < times.Count; k++)
        {
            double diff = Math.Abs((times[k] - ss).TotalMinutes);
            if (diff < best) { best = diff; i = k; }
        }
        if (i < 0 || best > 90) return null;

        static double At(JsonElement h, string name, int i, double fallback) =>
            h.TryGetProperty(name, out var a) && a.GetArrayLength() > i && a[i].ValueKind == JsonValueKind.Number ? a[i].GetDouble() : fallback;
        double low = At(h, "cloud_cover_low", i, 0), mid = At(h, "cloud_cover_mid", i, 0), high = At(h, "cloud_cover_high", i, 0);
        int score = SunsetScore(low, mid, high, At(h, "visibility", i, 20000), At(h, "precipitation_probability", i, 0));
        return new
        {
            date = ss.ToString("yyyy-MM-dd"),
            time = ss.ToString("HH:mm"),
            score,
            label = score >= 75 ? "기대해도 좋음" : score >= 50 ? "괜찮음" : score >= 25 ? "평범" : "어려움",
            low, mid, high,
        };
    }

    public const int RainHours = 3, RainProbability = 60;
    public const double RainMm = 0.3;

    static bool IsPrecip(int code) => code is >= 51 and <= 67 or >= 71 and <= 86 or >= 95;
    static bool IsSnow(int code) => code is >= 71 and <= 77 or 85 or 86;

    /// <summary>
    /// 앞으로 3시간 안에 비·눈이 올 것 같은 첫 시간(강수확률 60% 이상이거나 강수량 0.3mm 이상).
    /// { kind: "비"|"눈"|"소나기", startsAt: "HH:mm", inMinutes, probability, mm, now: 지금 오는 중 } 없으면 null.
    /// </summary>
    public static object? RainForecast(JsonElement root, DateTime now)
    {
        if (!root.TryGetProperty("hourly", out var h) || !h.TryGetProperty("time", out var timeArr)) return null;
        static double At(JsonElement h, string name, int i) =>
            h.TryGetProperty(name, out var a) && a.GetArrayLength() > i && a[i].ValueKind == JsonValueKind.Number ? a[i].GetDouble() : 0;
        int currentCode = root.TryGetProperty("current", out var c) && c.TryGetProperty("weather_code", out var cc) && cc.TryGetInt32(out var v) ? v : 0;
        var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        int k = 0;
        foreach (var t in timeArr.EnumerateArray())
        {
            if (DateTime.TryParse(t.GetString(), out var at) && at >= hourStart && at <= now.AddHours(RainHours))
            {
                double p = At(h, "precipitation_probability", k), mm = At(h, "precipitation", k);
                int code = (int)At(h, "weather_code", k);
                if (p >= RainProbability || mm >= RainMm)
                {
                    var start = at < now ? now : at;
                    string kind = IsSnow(code) ? "눈" : code is >= 80 and <= 82 ? "소나기" : "비";
                    return new
                    {
                        kind,
                        startsAt = at.ToString("HH:mm"),
                        inMinutes = (int)Math.Max(0, Math.Round((start - now).TotalMinutes)),
                        probability = (int)Math.Round(p),
                        mm = Math.Round(mm, 1),
                        now = IsPrecip(currentCode),
                    };
                }
            }
            k++;
        }
        return null;
    }

    /// <summary>노을 점수(0~100). 중·상층 구름이 적당히(30~70%) 있으면 하늘이 물들 캔버스가 되고,
    /// 하층 구름·비·낮은 시정은 지평선의 빛을 막는다.</summary>
    public static int SunsetScore(double low, double mid, double high, double visibilityM, double precipProb)
    {
        double canvas = Math.Max(mid, high);
        double s = canvas switch { < 10 => 35, < 30 => 60, <= 70 => 90, <= 90 => 60, _ => 30 };
        s *= low switch { > 70 => 0.2, > 40 => 0.5, > 20 => 0.8, _ => 1 };
        s *= precipProb switch { > 60 => 0.4, > 30 => 0.7, _ => 1 };
        if (visibilityM < 5000) s *= 0.7;
        return (int)Math.Round(Math.Clamp(s, 0, 100));
    }

    /// <summary>WMO 날씨 코드 → 한국어</summary>
    public static string Describe(int code) => code switch
    {
        0 => "맑음",
        1 => "대체로 맑음",
        2 => "구름 조금",
        3 => "흐림",
        45 or 48 => "안개",
        51 or 53 or 55 => "이슬비",
        56 or 57 => "어는 이슬비",
        61 => "약한 비",
        63 => "비",
        65 => "강한 비",
        66 or 67 => "어는 비",
        71 => "약한 눈",
        73 => "눈",
        75 => "많은 눈",
        77 => "싸락눈",
        80 or 81 or 82 => "소나기",
        85 or 86 => "소낙눈",
        95 => "뇌우",
        96 or 99 => "우박 동반 뇌우",
        _ => "알 수 없음",
    };
}
