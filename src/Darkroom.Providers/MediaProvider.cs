using System.Runtime.InteropServices.WindowsRuntime;
using Darkroom.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Darkroom.Providers;

/// <summary>
/// media 네임스페이스 — Windows 미디어 재생 정보(재생 중인 앱이 시스템 미디어 컨트롤에 알린 것). 2초 주기.
///   media.nowPlaying = { active, app, title, artist, album, status, positionSec, durationSec }
///   media.artwork    = "data:image/...;base64,..." 또는 null (곡이 바뀔 때만 바뀜)
/// 제어: ControlAsync("playPause" | "next" | "previous")
/// </summary>
public sealed class MediaProvider : IDataProvider
{
    const int MaxArtworkBytes = 1024 * 1024;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    string? _artKey;

    public string Namespace => "media";
    public TimeSpan Interval => TimeSpan.FromSeconds(2);

    async Task<GlobalSystemMediaTransportControlsSessionManager> ManagerAsync() =>
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

    public async Task UpdateAsync(IDataBus bus, CancellationToken ct)
    {
        var session = (await ManagerAsync()).GetCurrentSession();
        if (session == null)
        {
            bus.Publish("media.nowPlaying", new { active = false });
            if (_artKey != null) { bus.Publish("media.artwork", (object?)null); _artKey = null; }
            return;
        }

        var props = await session.TryGetMediaPropertiesAsync();
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        string status = playback.PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "stopped",
            _ => "other",
        };
        // 타임라인 위치는 마지막 보고 시점 기준 → 재생 중이면 경과 시간을 더한다.
        var position = timeline.Position;
        if (status == "playing" && timeline.LastUpdatedTime > DateTimeOffset.MinValue)
            position += DateTimeOffset.Now - timeline.LastUpdatedTime;
        double duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;

        bus.Publish("media.nowPlaying", new
        {
            active = true,
            app = FriendlyApp(session.SourceAppUserModelId),
            title = props?.Title,
            artist = props?.Artist,
            album = props?.AlbumTitle,
            status,
            positionSec = duration > 0 ? Math.Round(Math.Clamp(position.TotalSeconds, 0, duration)) : (double?)null,
            durationSec = duration > 0 ? Math.Round(duration) : (double?)null,
        });

        var key = $"{session.SourceAppUserModelId}|{props?.Title}|{props?.Artist}|{props?.AlbumTitle}";
        if (key != _artKey)
        {
            _artKey = key;
            bus.Publish("media.artwork", props?.Thumbnail == null ? null : await ReadArtworkAsync(props.Thumbnail));
        }
    }

    static async Task<string?> ReadArtworkAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > MaxArtworkBytes) return null;
            var buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
            await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None);
            var type = string.IsNullOrEmpty(stream.ContentType) ? "image/png" : stream.ContentType;
            return $"data:{type};base64,{Convert.ToBase64String(buffer.ToArray())}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>"Spotify.exe" → "Spotify", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → "ZuneMusic"</summary>
    public static string FriendlyApp(string aumid)
    {
        var s = aumid;
        int bang = s.IndexOf('!');
        if (bang >= 0) s = s[(bang + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        return s;
    }

    /// <summary>재생 제어. 실패하면 사유.</summary>
    public async Task<string?> ControlAsync(string command)
    {
        var session = (await ManagerAsync()).GetCurrentSession();
        if (session == null) return "재생 중인 미디어 없음";
        bool ok = command switch
        {
            "playPause" => await session.TryTogglePlayPauseAsync(),
            "next" => await session.TrySkipNextAsync(),
            "previous" => await session.TrySkipPreviousAsync(),
            _ => throw new ArgumentException($"알 수 없는 명령: {command}"),
        };
        return ok ? null : "앱이 이 명령을 지원하지 않음";
    }
}
