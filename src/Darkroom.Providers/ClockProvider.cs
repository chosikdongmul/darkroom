using Darkroom.Core;

namespace Darkroom.Providers;

/// <summary>time 네임스페이스. time.now = { epochMs, iso, offsetMinutes }</summary>
public sealed class ClockProvider : IDataProvider
{
    public string Namespace => "time";
    public TimeSpan Interval => TimeSpan.FromSeconds(1);

    public Task UpdateAsync(IDataBus bus, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        bus.Publish("time.now", new
        {
            epochMs = now.ToUnixTimeMilliseconds(),
            iso = now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            offsetMinutes = (int)now.Offset.TotalMinutes,
        });
        return Task.CompletedTask;
    }
}

public static class BuiltInProviders
{
    /// <summary>내장 Provider 전부. settings는 매번 최신 환경 설정을 돌려줘야 한다.</summary>
    public static IReadOnlyList<IDataProvider> Create(Func<ProviderSettings> settings) =>
    [
        new ClockProvider(),
        new SystemProvider(),
        new BatteryProvider(),
        new SunProvider(settings),
        new WeatherProvider(settings),
        new PhotosProvider(settings),
        new MediaProvider(),
        new AudioProvider(),
    ];
}
