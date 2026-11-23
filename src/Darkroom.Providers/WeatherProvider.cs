using System.Text.Json;
using Darkroom.Core;

namespace Darkroom.Providers;

/// <summary>
/// weather 네임스페이스 — Open-Meteo(키 불필요). 15분마다, 위치가 바뀌면 바로 다시 받는다.
/// 이 데이터를 구독하는 위젯은 manifest에 "network" 권한이 있어야 한다(위치가 외부로 전송되므로).
///   weather.current = { temperature, apparentTemperature, humidity, windSpeed, cloudCover, code, description, isDay, time }
///   weather.daily   = [{ date, max, min, code, description, sunrise, sunset }] (3일)
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
            "&daily=temperature_2m_max,temperature_2m_min,weather_code,sunrise,sunset&timezone=auto&forecast_days=3";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            Parse(doc.RootElement, out var current, out var daily);
            bus.Publish("weather.current", current);
            bus.Publish("weather.daily", daily);
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
