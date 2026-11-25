using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using Darkroom.App.Win32;
using Darkroom.Core;
using Darkroom.Providers;
using Microsoft.Web.WebView2.Core;

namespace Darkroom.App;

/// <summary>앱 상태의 단일 출처. 트레이·Manager·위젯 Action이 모두 여기를 거친다. UI 스레드에서만 호출.</summary>
internal sealed partial class AppController : IDisposable
{
    readonly Dictionary<string, WidgetHost> _open = new();
    readonly HashSet<string> _lost = new();          // Explorer 재시작 등으로 창을 잃은 위젯
    readonly DispatcherTimer _saveTimer, _displayTimer, _lostTimer;
    ProviderHost? _providers;
    HotReloader? _reloader;
    WallpaperRotator? _wallpaper;
    ShellEvents? _shell;

    public AppPaths Paths { get; }
    public bool DevMode { get; }
    public Dispatcher Dispatcher { get; }
    public AppState State { get; private set; } = new();
    public bool FirstRun { get; private set; }
    public DataBus Bus { get; } = new();
    public CoreWebView2Environment Env { get; private set; } = null!;
    public string SdkSource { get; private set; } = "";
    public Dictionary<string, WidgetManifest> Installed { get; } = new();
    /// <summary>위젯 폴더 이름 → 오류 (manifest가 잘못된 폴더)</summary>
    public SortedDictionary<string, string> InstallErrors { get; } = new();
    public Dictionary<string, Theme> Themes { get; private set; } = new();
    public List<string> ThemeErrors { get; } = new();

    /// <summary>상태가 바뀜 → Manager 새로고침.</summary>
    public event Action? Changed;

    public AppController(AppPaths paths, bool devMode, Dispatcher dispatcher)
    {
        Paths = paths; DevMode = devMode; Dispatcher = dispatcher;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveNow(), dispatcher) { IsEnabled = false };
        _displayTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Normal, (_, _) => OnDisplayChanged(), dispatcher) { IsEnabled = false };
        _lostTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal, (_, _) => _ = ReopenLostAsync(), dispatcher) { IsEnabled = false };
        Updates.Changed += () => Dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    // =========================================================== 업데이트
    public Updater Updates { get; } = new();
    DispatcherTimer? _updateTimer;

    /// <summary>시작 30초 뒤, 그 뒤 12시간마다 새 버전을 확인한다. 개발 빌드는 DARKROOM_UPDATE_VERSION이 있을 때만.</summary>
    void StartUpdateChecks()
    {
        _updateTimer?.Stop();
        _updateTimer = null;
        if (!State.CheckUpdates) return;
        if (DevMode && Environment.GetEnvironmentVariable("DARKROOM_UPDATE_VERSION") == null) return;
        _updateTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            _updateTimer!.Interval = TimeSpan.FromHours(12);
            _ = Updates.CheckAsync();
        }, Dispatcher);
    }

    public void SetCheckUpdates(bool on)
    {
        if (State.CheckUpdates == on) return;
        State.CheckUpdates = on;
        StartUpdateChecks();
        SaveStateSoon();
        Changed?.Invoke();
    }

    public Task CheckUpdatesAsync() => Updates.CheckAsync();
    public Task InstallUpdateAsync() => Updates.InstallAsync();

    public void NotifyChanged() => Changed?.Invoke();

    // =========================================================== 시작·종료
    public async Task InitializeAsync()
    {
        Log.Info($"Darkroom 시작. root={Paths.Root} dev={DevMode}");
        LoadSdk();
        LoadThemes();
        ScanWidgets();

        var saved = AppState.TryLoad(AppPaths.StateFile);
        if (saved != null) State = saved;
        else
        {
            FirstRun = true;
            var def = Path.Combine(Paths.Layouts, "default.json");
            if (File.Exists(def))
            {
                try { ApplyLayoutToState(Layout.Load(def)); }
                catch (Exception ex) { Log.Error("default 레이아웃 읽기 실패", ex); }
            }
        }
        if (!Themes.ContainsKey(State.Theme)) State.Theme = Themes.Keys.FirstOrDefault() ?? "";
        if (!Fonts.ContainsKey(State.Font ?? "")) State.Font = FontPack.DefaultKey;
        State.Providers ??= new ProviderSettings();
        State.Wallpaper ??= new WallpaperSettings();
        if (State.Wallpaper.Validate() is { } wbad) { Log.Warn($"배경화면 설정 오류({wbad}) → 기본값"); State.Wallpaper.IntervalMinutes = 30; State.Wallpaper.Fit = "fill"; }
        if (State.Providers.Validate() is { } bad) { Log.Warn($"환경 설정 오류({bad}) → 기본값"); State.Providers = new ProviderSettings(); }
        State.Calendar ??= new CalendarSettings();
        if (State.ThemeCustom?.Validate() != null || State.ThemeCustom == null) State.ThemeCustom = new ThemeCustom();
        State.Alerts ??= new SunAlertSettings();
        if (State.Alerts.Validate() != null) State.Alerts = new SunAlertSettings();
        _phase = CurrentPhase(); // 위젯을 열기 전에 정해 둬야 처음부터 맞는 색으로 뜬다

        // DARKROOM_WEBVIEW2_ARGS: 추가 브라우저 인자(디버깅용)
        // --process-per-site: 같은 사이트(w.darkroom)의 위젯들이 렌더러 프로세스 하나를 함께 쓴다(위젯 7개 기준 약 130MB 절약).
        // DARKROOM_PROCESS_PER_WIDGET=1이면 위젯마다 따로 둔다(비교·문제 확인용).
        var extraArgs = Environment.GetEnvironmentVariable("DARKROOM_WEBVIEW2_ARGS");
        var shared = Environment.GetEnvironmentVariable("DARKROOM_PROCESS_PER_WIDGET") == "1" ? "" : "--process-per-site";
        // 숨긴 위젯(전체 화면 앱 중·전체 숨기기)의 타이머를 5분 뒤 1분 단위로 몰아 깨우는 절약 기능을 끈다:
        // 타이머 위젯의 "끝났어요" 알림이 늦지 않게. 1초 단위 동작은 그대로라 부담은 거의 없다.
        const string timers = "--disable-features=IntensiveWakeUpThrottling";
        var args = string.Join(' ', new[] { shared, timers, extraArgs }.Where(a => !string.IsNullOrWhiteSpace(a)));
        var envOptions = new CoreWebView2EnvironmentOptions(args.Length == 0 ? null : args);
        Env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData, envOptions);
        Log.Info($"WebView2 런타임 {Env.BrowserVersionString}");
        _providers = new ProviderHost(Bus, [.. BuiltInProviders.Create(() => State.Providers), new WallpaperProvider(() => State.Wallpaper.Enabled ? State.Wallpaper.CurrentPath : null), CreateCalendarProvider()]);

        foreach (var id in State.Widgets.Where(kv => kv.Value.Enabled).Select(kv => kv.Key).ToList())
        {
            if (Installed.ContainsKey(id)) await OpenWidgetAsync(id);
            else
            {
                // 지워진 위젯(업데이트로 빠진 내장 위젯 등): 꺼 둔다. 같은 id의 위젯을 다시 넣으면 Manager에서 켤 수 있다.
                Log.Warn($"켜진 위젯 '{id}'이(가) 설치돼 있지 않아 끔");
                State.Widgets[id].Enabled = false;
            }
        }

        _shell = new ShellEvents();
        _shell.ExplorerRestarted += () => { Log.Info("Explorer 재시작 감지"); CollectLost(); _ = ReopenLostAsync(); };
        _shell.DisplayChanged += () => { _displayTimer.Stop(); _displayTimer.Start(); };
        _reloader = new HotReloader([Paths.Widgets, Paths.Themes, Paths.Sdk, Paths.Layouts],
            batch => Dispatcher.BeginInvoke(() => _ = OnFilesChangedAsync(batch)));
        _wallpaper = new WallpaperRotator(this, Dispatcher);
        _wallpaper.Apply();
        StartUpdateChecks();
        StartSunTimer();
        StartMemoryLog();
        StartFullscreenWatch();
        UpdateRainWatch();
        SaveStateSoon();
        Changed?.Invoke();
    }

    public void Shutdown()
    {
        SaveNow();
        _sunTimer?.Stop();
        _fullscreenTimer?.Stop();
        _wallpaper?.Dispose(); // 앱을 꺼도 지금 배경화면은 그대로 둔다(끄기를 눌렀을 때만 되돌림)
        _reloader?.Dispose();
        foreach (var h in _open.Values) h.Dispose();
        _open.Clear();
        _providers?.Dispose();
        _shell?.Dispose();
        Log.Info("Darkroom 종료");
    }

    public void Dispose() => Shutdown();

    // =========================================================== 불러오기
    void LoadSdk()
    {
        try { SdkSource = File.ReadAllText(Paths.SdkFile); }
        catch (Exception ex) { SdkSource = ""; Log.Error($"SDK 읽기 실패: {Paths.SdkFile}", ex); }
        FontErrors.Clear();
        Fonts = FontPack.LoadAll(Path.Combine(Paths.Sdk, "fonts"), FontErrors);
        foreach (var e in FontErrors) Log.Warn("글꼴 팩 오류: " + e);
        lock (_assets) _assets.Clear();
    }

    /// <summary>글꼴 조합(sdk/fonts/packs.json). 고른 팩의 @font-face만 위젯에 넘기므로 안 고른 팩은 메모리를 쓰지 않는다.</summary>
    public Dictionary<string, FontPack> Fonts { get; private set; } = new();
    public List<string> FontErrors { get; } = new();
    FontPack? CurrentFont => Fonts.GetValueOrDefault(State.Font) ?? Fonts.GetValueOrDefault(FontPack.DefaultKey) ?? Fonts.Values.FirstOrDefault();

    // =========================================================== 내장 리소스(글꼴)
    // 위젯·Manager가 https://assets.darkroom/fonts/<파일> 을 요청하면 호스트가 sdk/fonts에서 직접 내려준다.
    // (폴더 매핑된 주소 아래 요청은 WebResourceRequested가 오지 않아서, 매핑하지 않은 별도 주소를 쓴다.)
    // 다른 출처라 CORS 헤더를 붙인다. 인터넷은 필요 없다.
    public const string AssetHost = "assets.darkroom";
    public const string AssetFilter = "https://" + AssetHost + "/*";
    readonly Dictionary<string, byte[]> _assets = new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Text.RegularExpressions.Regex SafeAssetName = new(@"^fonts/(packs/[a-z0-9-]+/)?[A-Za-z0-9._-]+\.(woff2|woff|ttf|otf|css)$");

    public static bool IsAssetUri(string uri) => uri.StartsWith("https://" + AssetHost + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>https://assets.darkroom/ 요청에 sdk 폴더의 파일로 응답했으면 true.</summary>
    public bool TryServeAsset(CoreWebView2WebResourceRequestedEventArgs e, bool allowIcons = false)
    {
        if (!IsAssetUri(e.Request.Uri) || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        var rel = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        // https://assets.darkroom/icon?t=<경로> → 그 앱·폴더의 Windows 아이콘(launch 권한이 있는 위젯과 Manager만)
        if (rel == "icon")
        {
            var t = System.Web.HttpUtility.ParseQueryString(uri.Query)["t"];
            var png = allowIcons && t != null ? ShellIcons.Png(t) : null;
            e.Response = png == null
                ? Env.CreateWebResourceResponse(null, 404, "Not Found", "Access-Control-Allow-Origin: *")
                : Env.CreateWebResourceResponse(new MemoryStream(png, writable: false), 200, "OK",
                    "Content-Type: image/png\r\nCache-Control: max-age=3600\r\nAccess-Control-Allow-Origin: *");
            return true;
        }
        byte[]? bytes = null;
        if (SafeAssetName.IsMatch(rel))
        {
            lock (_assets)
            {
                if (!_assets.TryGetValue(rel, out bytes))
                {
                    var path = Path.Combine(Paths.Sdk, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path)) _assets[rel] = bytes = File.ReadAllBytes(path);
                }
            }
        }
        if (bytes == null)
        {
            Log.Warn($"내장 리소스 없음: {host}{uri.AbsolutePath}");
            e.Response = Env.CreateWebResourceResponse(null, 404, "Not Found", "");
            return true;
        }
        var type = Path.GetExtension(rel).ToLowerInvariant() switch { ".woff2" => "font/woff2", ".woff" => "font/woff", ".otf" => "font/otf", ".css" => "text/css; charset=utf-8", _ => "font/ttf" };
        e.Response = Env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), 200, "OK",
            $"Content-Type: {type}\r\nCache-Control: max-age=86400\r\nAccess-Control-Allow-Origin: *");
        return true;
    }

    void LoadThemes()
    {
        ThemeErrors.Clear();
        Themes = Theme.LoadAll(Paths.Themes, ThemeErrors);
        foreach (var e in ThemeErrors) Log.Warn("테마 오류: " + e);
        if (Themes.Count == 0)
            Themes["fallback"] = new Theme { Key = "fallback", Name = "기본", Tokens = new() { ["bg"] = "rgba(43,43,43,.72)", ["fg"] = "#f2f2f2", ["muted"] = "#8a8a8a", ["accent"] = "#e07a2e", ["radius"] = "18px" } };
    }

    void ScanWidgets()
    {
        Installed.Clear();
        InstallErrors.Clear();
        if (!Directory.Exists(Paths.Widgets)) return;
        foreach (var dir in Directory.EnumerateDirectories(Paths.Widgets).Order())
            TryInstall(dir);
    }

    WidgetManifest? TryInstall(string dir)
    {
        var folder = Path.GetFileName(dir);
        try
        {
            var m = WidgetManifest.Load(dir);
            if (Installed.TryGetValue(m.Id, out var other) && !SameFolder(other.Folder, m.Folder))
                throw new InvalidDataException($"id '{m.Id}'가 '{Path.GetFileName(other.Folder)}' 폴더와 겹침");
            Installed[m.Id] = m;
            InstallErrors.Remove(folder);
            return m;
        }
        catch (InvalidDataException ex)
        {
            InstallErrors[folder] = ex.Message;
            Log.Warn($"위젯 폴더 '{folder}' 무시: {ex.Message}");
            return null;
        }
    }

    static bool SameFolder(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // =========================================================== 위젯
    public WidgetState StateOf(string id)
    {
        if (!State.Widgets.TryGetValue(id, out var st)) State.Widgets[id] = st = new WidgetState();
        return st;
    }

    public bool IsOpen(string id) => _open.ContainsKey(id);

    async Task OpenWidgetAsync(string id)
    {
        if (_open.ContainsKey(id) || !Installed.TryGetValue(id, out var m)) return;
        var host = new WidgetHost(this, m);
        _open[id] = host;
        _lost.Remove(id);
        await host.OpenAsync();
        Changed?.Invoke();
    }

    void CloseWidget(string id)
    {
        if (_open.Remove(id, out var h)) h.Dispose();
    }

    async Task RecreateAsync(string id)
    {
        if (!_open.ContainsKey(id)) return;
        CloseWidget(id);
        await OpenWidgetAsync(id);
    }

    public async Task SetWidgetEnabledAsync(string id, bool enabled)
    {
        if (!Installed.ContainsKey(id)) return;
        StateOf(id).Enabled = enabled;
        if (enabled) await OpenWidgetAsync(id); else CloseWidget(id);
        SaveStateSoon();
        Changed?.Invoke();
    }

    /// <summary>Manager에서 바꾸는 창 옵션. 오류면 사유 반환.</summary>
    public async Task<string?> SetWidgetPropAsync(string id, string prop, JsonElement v)
    {
        if (!Installed.TryGetValue(id, out var m)) return "설치되지 않은 위젯";
        var st = StateOf(id);
        _open.TryGetValue(id, out var host);
        switch (prop)
        {
            case "windowMode" when v.ValueKind == JsonValueKind.String && WindowModes.IsValid(v.GetString()):
                var mode = v.GetString()!;
                st.WindowMode = mode == m.WindowMode ? null : mode;
                await RecreateAsync(id);
                break;
            case "opacity" when v.ValueKind == JsonValueKind.Number:
                st.Opacity = Math.Clamp(v.GetDouble(), 0.1, 1.0);
                host?.PushWindow();
                break;
            case "locked" when v.ValueKind is JsonValueKind.True or JsonValueKind.False:
                st.Locked = v.GetBoolean();
                host?.PushWindow();
                break;
            case "hoverFade" when v.ValueKind is JsonValueKind.True or JsonValueKind.False:
                st.HoverFade = v.GetBoolean();
                host?.PushWindow();
                break;
            default:
                return $"알 수 없는 속성 또는 값: {prop}";
        }
        SaveStateSoon();
        Changed?.Invoke();
        return null;
    }

    public string? SetSetting(string id, string key, JsonElement value)
    {
        if (!Installed.TryGetValue(id, out var m)) return "설치되지 않은 위젯";
        if (!m.Settings.TryGetValue(key, out var spec)) return $"설정 '{key}' 없음";
        if (!spec.Accepts(value)) return $"설정 '{key}' 값이 형식({spec.Type})과 맞지 않음";
        StateOf(id).Settings[key] = value.Clone();
        if (_open.TryGetValue(id, out var h)) h.PushSettings();
        SaveStateSoon();
        Changed?.Invoke();
        return null;
    }

    public (int x, int y) NextDefaultPosition()
    {
        var wa = Native.PrimaryWorkArea();
        int n = _open.Count;
        return (wa.Left + 60 + n * 40, wa.Top + 60 + n * 40);
    }

    public IEnumerable<Native.RECT> SnapTargetsExcept(WidgetHost self) =>
        _open.Values.Where(h => h != self && h.Window is { IsAlive: true }).Select(h => h.Window!.CardRect).ToList();

    /// <summary>창이 예기치 않게 사라짐(대개 Explorer 재시작). 잠시 뒤 다시 만든다.</summary>
    public void OnWidgetLost(WidgetHost host)
    {
        var id = host.Manifest.Id;
        if (!_open.TryGetValue(id, out var h) || h != host) return;
        Log.Warn($"[{id}] 창을 잃음 → 다시 만들 예정");
        _open.Remove(id);
        Dispatcher.BeginInvoke(host.Dispose);
        _lost.Add(id);
        _lostTimer.Start();
    }

    /// <summary>Explorer가 죽으면 Progman의 자식인 위젯 창도 사라지지만 WM_DESTROY가 오지 않는다.
    /// 재시작을 감지하면 창이 살아 있는지 직접 확인한다.</summary>
    void CollectLost()
    {
        foreach (var h in _open.Values.ToList())
            if (h.Window is { IsAlive: false }) OnWidgetLost(h);
    }

    async Task ReopenLostAsync()
    {
        _lostTimer.Stop();
        if (_lost.Count == 0) return;
        // desktop 모드 위젯이 있으면 Progman이 다시 생길 때까지 기다린다.
        bool needDesktop = _lost.Any(id => (StateOf(id).WindowMode ?? Installed.GetValueOrDefault(id)?.WindowMode) == WindowModes.Desktop);
        if (needDesktop && DesktopLayer.FindProgman() == IntPtr.Zero) { _lostTimer.Start(); return; }
        foreach (var id in _lost.ToList())
            if (StateOf(id).Enabled) await OpenWidgetAsync(id);
        _lost.Clear();
    }

    void OnDisplayChanged()
    {
        _displayTimer.Stop();
        // 저장된 위치로 되돌린 뒤, 화면 밖이면 가장 가까운 가장자리로 당긴다. 당긴 위치는 저장하지 않아서
        // 원래 화면(예: 원격 접속을 끝낸 노트북 화면)으로 돌아오면 원래 자리로 간다.
        foreach (var h in _open.Values)
        {
            if (h.Window is not { IsAlive: true } w) continue;
            var st = StateOf(h.Manifest.Id);
            if (st.X is int x && st.Y is int y) w.MoveTo(x, y);
            w.UpdateScaleFromPosition();
            w.EnsureOnScreen();
        }
        _wallpaper?.EnsureShown(); // 새로 생긴 화면에 배경 사진이 없으면 다시 지정
    }

    // =========================================================== 테마·전체 옵션
    /// <summary>색 테마 + 글꼴 조합을 합친 것. 글꼴 변수가 테마의 fontUi/fontNum을 덮는다. fontCss는 고른 팩의 @font-face.</summary>
    public object ThemePayload()
    {
        var t = (Themes.GetValueOrDefault(EffectiveThemeKey) ?? Themes.Values.First()).WithCustom(State.ThemeCustom);
        var vars = t.CssVariables();
        var f = CurrentFont;
        if (f != null) foreach (var (k, v) in f.CssVariables()) vars[k] = v;
        return new { key = t.Key, name = t.Name, tokens = t.Tokens, vars, font = f?.Key, fontCss = f?.Css ?? "" };
    }

    /// <summary>강조색(#RRGGBB, null=테마 기본)·카드 진하기(null=테마 기본).</summary>
    public string? SetThemeCustom(ThemeCustom next)
    {
        if (next.Validate() is { } err) return err;
        State.ThemeCustom = new ThemeCustom { Accent = next.Accent?.ToLowerInvariant(), CardAlpha = next.CardAlpha is double a ? Math.Round(a, 2) : null };
        foreach (var h in _open.Values) h.PushTheme();
        SaveStateSoon();
        Changed?.Invoke();
        return null;
    }

    public void SetFont(string key)
    {
        if (!Fonts.ContainsKey(key) || State.Font == key) return;
        State.Font = key;
        foreach (var h in _open.Values) h.PushTheme();
        SaveStateSoon();
        Changed?.Invoke();
    }

    /// <summary>테마 radius 토큰("18px")을 DIP 숫자로. 위젯 창 영역의 둥근 모서리에 쓴다.</summary>
    public double ThemeCornerRadiusDip()
    {
        var t = Themes.GetValueOrDefault(EffectiveThemeKey);
        var raw = t?.Tokens.GetValueOrDefault("radius")?.Trim() ?? "";
        if (raw.EndsWith("px", StringComparison.OrdinalIgnoreCase)) raw = raw[..^2];
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 18;
    }

    public void SetTheme(string key)
    {
        if (!Themes.ContainsKey(key) || State.Theme == key) return;
        State.Theme = key;
        foreach (var h in _open.Values) h.PushTheme();
        SaveStateSoon();
        Changed?.Invoke();
    }

    // =========================================================== 해 시각: 색 자동 전환·골든아워 알림
    /// <summary>시간 따라 바꿀 때 쓰는 색. 낮에는 사용자가 고른 색.</summary>
    public static readonly Dictionary<string, string> AutoThemes = new()
    {
        ["goldenHour"] = "warm-negative", ["blueHour"] = "blue-hour", ["night"] = "safelight",
    };
    string _phase = "day";
    DispatcherTimer? _memTimer, _fullscreenTimer;
    IntPtr _coveredMonitor;

    /// <summary>2초마다 전체 화면 앱을 확인해 그 모니터의 위젯 그리기를 멈추거나 다시 시작한다.</summary>
    void StartFullscreenWatch()
    {
        _fullscreenTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => CheckFullscreen(), Dispatcher);
        _fullscreenTimer.Start();
    }

    void CheckFullscreen()
    {
        var mon = State.PauseOnFullscreen ? FullscreenDetector.CoveredMonitor() : IntPtr.Zero;
        if (mon != _coveredMonitor)
        {
            Log.Info(mon != IntPtr.Zero ? "전체 화면 앱 감지: 그 화면의 위젯 그리기를 멈춤" : "전체 화면 앱 끝: 위젯 다시 그림");
            _coveredMonitor = mon;
        }
        foreach (var h in _open.Values)
            // "항상 위"·"일반 창" 위젯은 일부러 띄워 둔 것이라 멈추지 않는다(바탕화면·맨 아래 위젯은 어차피 가려져 있다)
            h.SetPaused(mon != IntPtr.Zero && h.Window is { IsAlive: true } w && w.Mode is WindowModes.Desktop or WindowModes.Bottom
                        && FullscreenDetector.MonitorOf(w.Hwnd) == mon);
    }

    public void SetPauseOnFullscreen(bool on)
    {
        State.PauseOnFullscreen = on;
        CheckFullscreen();
        SaveStateSoon();
        Changed?.Invoke();
    }

    /// <summary>1분 뒤, 그다음 30분마다 메모리 사용량을 로그에 남긴다(오래 켜 둘 때 새는지 보려고).</summary>
    void StartMemoryLog()
    {
        _memTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) =>
        {
            _memTimer!.Interval = TimeSpan.FromMinutes(30);
            var gc = GC.GetGCMemoryInfo();
            using var p = Process.GetCurrentProcess();
            Log.Info($"메모리: 전용 {p.PrivateMemorySize64 >> 20}MB, 관리 힙 {GC.GetTotalMemory(false) >> 20}MB(커밋 {gc.TotalCommittedBytes >> 20}MB, 조각 {gc.FragmentedBytes >> 20}MB), 위젯 {_open.Count}개");
        }, Dispatcher);
    }

    DispatcherTimer? _sunTimer;
    readonly HashSet<string> _alerted = new();
    /// <summary>(제목, 내용) 알림 → 트레이가 Windows 알림으로 띄운다.</summary>
    public event Action<string, string>? Notify;
    /// <summary>위젯이 Manager를 열어 달라고 함(보여 줄 칸, 없으면 null).</summary>
    public event Action<string?>? OpenManagerRequested;

    public string EffectiveThemeKey =>
        State.ThemeAuto && AutoThemes.TryGetValue(_phase, out var k) && Themes.ContainsKey(k) ? k : State.Theme;

    string CurrentPhase()
    {
        var s = State.Providers;
        return SunMath.Phase(SunMath.Position(DateTimeOffset.Now, s.Latitude, s.Longitude).altitude);
    }

    void StartSunTimer()
    {
        _sunTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => SunTick(), Dispatcher);
        SunTick();
    }

    void SunTick()
    {
        var phase = CurrentPhase();
        if (phase != _phase)
        {
            var before = EffectiveThemeKey;
            _phase = phase;
            if (EffectiveThemeKey != before) { foreach (var h in _open.Values) h.PushTheme(); Changed?.Invoke(); }
        }

        var a = State.Alerts;
        if (!a.Enabled) return;
        var s = State.Providers;
        var now = DateTimeOffset.Now;
        var date = DateOnly.FromDateTime(now.LocalDateTime);
        var tz = TimeZoneInfo.Local;
        var high = SunMath.Crossing(date, 6, s.Latitude, s.Longitude, tz);   // 해가 6°를 지나는 때
        var low = SunMath.Crossing(date, -4, s.Latitude, s.Longitude, tz);   // 해가 -4°를 지나는 때
        Check("evening", "저녁 골든아워", high.evening, low.evening);
        if (a.Morning) Check("morning", "아침 골든아워", low.morning, high.morning);

        void Check(string kind, string label, DateTimeOffset? start, DateTimeOffset? end)
        {
            if (start is not { } st) return;
            var key = $"{date:yyyyMMdd}-{kind}";
            var left = st - now;
            if (_alerted.Contains(key) || now >= st || left > TimeSpan.FromMinutes(a.LeadMinutes)) return;
            _alerted.Add(key);
            var mins = Math.Max(1, (int)Math.Round(left.TotalMinutes));
            Log.Info($"알림: {label} {mins}분 전");
            Notify?.Invoke($"{label} {mins}분 전", $"{st:HH:mm}–{end:HH:mm} · {s.LocationName}");
        }
    }

    // ---------- 비·눈 예보 알림 ----------
    IDisposable? _rainSub;
    DateTimeOffset _rainAlertedAt = DateTimeOffset.MinValue;

    /// <summary>알림을 켜 두면 weather.rain을 구독한다(위젯이 없어도 날씨를 받게 된다).</summary>
    void UpdateRainWatch()
    {
        if (State.Alerts.Rain && _rainSub == null)
            _rainSub = Bus.Subscribe("weather.rain", (_, v) => Dispatcher.BeginInvoke(() => OnRainForecast(v)));
        else if (!State.Alerts.Rain && _rainSub != null) { _rainSub.Dispose(); _rainSub = null; }
    }

    void OnRainForecast(JsonElement v)
    {
        if (!State.Alerts.Rain || v.ValueKind != JsonValueKind.Object) return;
        if (v.TryGetProperty("now", out var n) && n.ValueKind == JsonValueKind.True) return; // 이미 오는 중
        var now = DateTimeOffset.Now;
        if (now - _rainAlertedAt < TimeSpan.FromHours(3)) return; // 한 번 알리면 3시간은 조용히
        _rainAlertedAt = now;
        string kind = v.TryGetProperty("kind", out var k) ? k.GetString() ?? "비" : "비";
        int mins = v.TryGetProperty("inMinutes", out var m) && m.TryGetInt32(out var mi) ? mi : 0;
        int prob = v.TryGetProperty("probability", out var p) && p.TryGetInt32(out var pi) ? pi : 0;
        string at = v.TryGetProperty("startsAt", out var s) ? s.GetString() ?? "" : "";
        string when = mins < 10 ? "곧" : mins < 60 ? $"{mins}분 뒤" : $"약 {(int)Math.Round(mins / 60.0)}시간 뒤";
        Log.Info($"알림: {when} {kind}");
        Notify?.Invoke($"{when} {kind} 소식", $"{at}쯤부터 · 강수확률 {prob}% · {State.Providers.LocationName}");
    }

    public void SetThemeAuto(bool on)
    {
        if (State.ThemeAuto == on) return;
        var before = EffectiveThemeKey;
        State.ThemeAuto = on;
        if (EffectiveThemeKey != before) foreach (var h in _open.Values) h.PushTheme();
        SaveStateSoon();
        Changed?.Invoke();
    }

    public string? SetAlerts(SunAlertSettings next)
    {
        if (next.Validate() is { } err) return err;
        State.Alerts = next;
        UpdateRainWatch();
        SaveStateSoon();
        Changed?.Invoke();
        return null;
    }

    // =========================================================== 도시 검색(위치 설정용)
    static readonly System.Net.Http.HttpClient GeoHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>도시 이름 → 후보(Open-Meteo 지오코딩, 키 불필요). 실패하면 빈 목록.</summary>
    public static async Task<List<object>> SearchCitiesAsync(string query)
    {
        query = query.Trim();
        if (query.Length < 1 || query.Length > 60) return new List<object>();
        // 한국 도시는 "부산광역시"·"서울특별시"처럼 전체 이름으로 등록돼 있어 "부산"만으로는 안 나온다 → 붙여서 같이 찾는다
        var queries = new List<string> { query };
        if (query.Any(c => c is >= '가' and <= '힣') && !query.EndsWith('시')) queries.AddRange([query + "시", query + "광역시", query + "특별시"]);
        var found = new Dictionary<string, (object item, long pop)>();
        foreach (var q in queries)
        {
            try
            {
                var json = await GeoHttp.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(q)}&count=6&language=ko&format=json");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var results)) continue;
                foreach (var r in results.EnumerateArray())
                {
                    string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                    double lat = Math.Round(r.GetProperty("latitude").GetDouble(), 4), lon = Math.Round(r.GetProperty("longitude").GetDouble(), 4);
                    var key = $"{Math.Round(lat, 1)},{Math.Round(lon, 1)}|{S("name")}";
                    long pop = r.TryGetProperty("population", out var p) && p.TryGetInt64(out var pv) ? pv : 0;
                    found.TryAdd(key, (new
                    {
                        name = S("name"), region = string.Join(", ", new[] { S("admin1"), S("country") }.Where(x => !string.IsNullOrEmpty(x)).Distinct()),
                        latitude = lat, longitude = lon,
                    }, pop));
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            {
                Log.Warn($"도시 검색 실패({q}): {ex.Message}");
            }
        }
        // 사람이 많이 사는 곳(큰 도시)이 먼저
        return found.Values.OrderByDescending(x => x.pop).Take(6).Select(x => x.item).ToList();
    }

    // =========================================================== Provider 환경 설정
    public string PhotosFolder => PhotosProvider.ResolveFolder(State.Providers);

    public string? SetProviderSettings(ProviderSettings next)
    {
        if (next.PhotosFolder is { Length: 0 }) next.PhotosFolder = null;
        if (next.Validate() is { } err) return err;
        var prev = State.Providers;
        State.Providers = next.Clone();
        if (prev.Latitude != next.Latitude || prev.Longitude != next.Longitude || prev.LocationName != next.LocationName)
        {
            _providers?.Kick("sun");
            _providers?.Kick("weather");
        }
        if (PhotosProvider.ResolveFolder(prev) != PhotosFolder || prev.PhotosIntervalSeconds != next.PhotosIntervalSeconds)
            _providers?.Kick("photos");
        if (PhotosProvider.ResolveFolder(prev) != PhotosFolder && State.Wallpaper.Enabled) _wallpaper?.Next();
        SaveStateSoon();
        Changed?.Invoke();
        return null;
    }

    // =========================================================== 배경화면 바꾸기
    /// <summary>켜기/끄기·간격·맞춤 방식. 켜는 순간 바로 한 장 바꾸고, 끄면 원래 배경화면으로 되돌린다.</summary>
    public string? SetWallpaperSettings(bool enabled, int intervalMinutes, string fit)
    {
        var w = State.Wallpaper;
        var next = new WallpaperSettings { Enabled = enabled, IntervalMinutes = intervalMinutes, Fit = fit };
        if (next.Validate() is { } err) return err;
        bool turnedOn = enabled && !w.Enabled;
        bool fitChanged = fit != w.Fit;
        w.Enabled = enabled; w.IntervalMinutes = intervalMinutes; w.Fit = fit;
        _wallpaper?.Apply(changeNow: turnedOn || (enabled && fitChanged));
        SaveStateSoon();
        Changed?.Invoke();
        return null;
    }

    /// <summary>Provider를 기다리지 않고 바로 다시 돌린다(배경화면이 바뀐 직후 등).</summary>
    public void KickProvider(string ns) => _providers?.Kick(ns);

    public void NextWallpaper()
    {
        if (State.Wallpaper.Enabled) _wallpaper?.Next();
    }

    public void SetAllLocked(bool locked)
    {
        State.AllLocked = locked;
        foreach (var h in _open.Values) h.PushWindow();
        SaveStateSoon();
        Changed?.Invoke();
    }

    public void SetAllHidden(bool hidden)
    {
        State.AllHidden = hidden;
        foreach (var h in _open.Values) h.SetHidden(hidden);
        SaveStateSoon();
        Changed?.Invoke();
    }

    // =========================================================== 레이아웃
    public List<Layout> Layouts() => Layout.LoadAll(Paths.Layouts);

    public string SaveLayout(string name)
    {
        var layout = new Layout { Name = name.Trim().Length == 0 ? "레이아웃" : name.Trim(), Theme = State.Theme, Font = State.Font };
        foreach (var id in _open.Keys) layout.Widgets[id] = StateOf(id).Clone();
        var key = Layout.ToKey(layout.Name);
        DrJson.WriteAtomic(Path.Combine(Paths.Layouts, key + ".json"), layout);
        Log.Info($"레이아웃 저장: {key} (위젯 {layout.Widgets.Count}개)");
        Changed?.Invoke();
        return key;
    }

    public async Task<string?> LoadLayoutAsync(string key)
    {
        var path = Path.Combine(Paths.Layouts, Layout.ToKey(key) + ".json");
        if (!File.Exists(path)) return $"레이아웃 '{key}' 없음";
        Layout layout;
        try { layout = Layout.Load(path); }
        catch (Exception ex) { return $"레이아웃 읽기 실패: {ex.Message}"; }

        foreach (var id in _open.Keys.ToList())
            if (!layout.Widgets.ContainsKey(id)) { StateOf(id).Enabled = false; CloseWidget(id); }
        ApplyLayoutToState(layout);
        foreach (var id in layout.Widgets.Keys.Where(Installed.ContainsKey))
        {
            if (_open.ContainsKey(id)) await RecreateAsync(id);
            else await OpenWidgetAsync(id);
        }
        if (layout.Theme != null && Themes.ContainsKey(layout.Theme)) SetTheme(layout.Theme);
        if (layout.Font != null && Fonts.ContainsKey(layout.Font)) SetFont(layout.Font);
        Log.Info($"레이아웃 불러옴: {layout.Key}");
        SaveStateSoon();
        Changed?.Invoke();
        return null;
    }

    void ApplyLayoutToState(Layout layout)
    {
        foreach (var (id, ws) in layout.Widgets)
        {
            var st = ws.Clone();
            st.Enabled = true;
            State.Widgets[id] = st;
        }
        if (layout.Theme != null) State.Theme = layout.Theme;
        if (layout.Font != null) State.Font = layout.Font;
    }

    public void DeleteLayout(string key)
    {
        var path = Path.Combine(Paths.Layouts, Layout.ToKey(key) + ".json");
        if (File.Exists(path)) File.Delete(path);
        Changed?.Invoke();
    }

    // =========================================================== Action
    public async Task ExecuteActionAsync(WidgetHost source, ActionRequest req)
    {
        string? error = null;
        switch (req.Type)
        {
            case "open":
                error = ActionPolicy.CheckOpen(req.Target, source.Manifest);
                if (error == null)
                {
                    try { Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(req.Target!.Trim())) { UseShellExecute = true }); }
                    catch (Exception ex) { error = ex.Message; }
                }
                break;
            case "notify":
                if (!source.Manifest.HasPermission("notify")) { error = "notify 권한이 없는 위젯"; break; }
                var title = (req.Title ?? source.Manifest.Name).Trim();
                var text = (req.Text ?? "").Trim();
                Log.Info($"[{source.Manifest.Id}] 알림: {title}");
                Notify?.Invoke(title.Length > 60 ? title[..60] : title, text.Length > 200 ? text[..200] : text);
                break;
            case "openManager":
                // 위젯에서 설정이 필요한 곳으로 바로 간다(예: 일정 위젯의 "캘린더 연결")
                OpenManagerRequested?.Invoke(req.Section is "calendar" ? req.Section : null);
                break;
            case "toggleWidget" when req.Id != null && Installed.ContainsKey(req.Id):
                await SetWidgetEnabledAsync(req.Id, req.Enabled ?? !StateOf(req.Id).Enabled);
                break;
            case "loadLayout" when req.Name != null:
                error = await LoadLayoutAsync(req.Name);
                break;
            case "setSetting" when req.Key != null:
                error = SetSetting(source.Manifest.Id, req.Key, req.Value);
                break;
            case "media" when req.Command is "playPause" or "next" or "previous":
                // media 데이터를 구독하는 위젯만 재생을 제어할 수 있다.
                if (!source.Manifest.Subscribe.Any(k => k.StartsWith("media.", StringComparison.Ordinal))) { error = "media 구독이 없는 위젯"; break; }
                var media = _providers?.Get<MediaProvider>();
                try { error = media == null ? "media provider 없음" : await media.ControlAsync(req.Command); }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { error = ex.Message; }
                break;
            default:
                error = $"알 수 없는 action: {req.Type}";
                break;
        }
        if (error != null)
        {
            Log.Warn($"[{source.Manifest.Id}] action '{req.Type}' 거부: {error}");
            source.Post(new { type = "actionError", action = req.Type, reason = error });
        }
    }

    public void SetAutoStart(bool enabled)
    {
        AutoStart.Set(enabled);
        Log.Info($"Windows 시작 시 실행: {(enabled ? "켬" : "끔")}");
        Changed?.Invoke();
    }

    public void OpenLogs()
    {
        Directory.CreateDirectory(AppPaths.Logs);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Logs}\"") { UseShellExecute = false });
    }

    public void OpenFolder(string? widgetId)
    {
        var path = widgetId != null && Installed.TryGetValue(widgetId, out var m) ? m.Folder : Paths.Widgets;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
    }

    // =========================================================== Hot Reload
    async Task OnFilesChangedAsync(IReadOnlyCollection<string> paths)
    {
        bool sdk = false, themes = false, layouts = false;
        var folders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // 위젯 폴더 → manifest 변경 여부
        foreach (var p in paths)
        {
            var rel = Path.GetRelativePath(Paths.Root, p);
            var seg = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length == 0) continue;
            switch (seg[0].ToLowerInvariant())
            {
                case "sdk": sdk = true; break;
                case "themes": themes = true; break;
                case "layouts": layouts = true; break;
                case "widgets":
                    if (seg.Length == 1) { foreach (var d in Directory.EnumerateDirectories(Paths.Widgets)) folders[Path.GetFileName(d)] = true; break; }
                    bool manifest = seg.Length == 2 || seg[^1].Equals(WidgetManifest.FileName, StringComparison.OrdinalIgnoreCase);
                    folders[seg[1]] = folders.GetValueOrDefault(seg[1]) || manifest;
                    break;
            }
        }
        Log.Info($"파일 변경: sdk={sdk} themes={themes} widgets=[{string.Join(", ", folders.Select(f => f.Key + (f.Value ? "*" : "")))}]");

        if (themes)
        {
            LoadThemes();
            if (!Themes.ContainsKey(State.Theme)) State.Theme = Themes.Keys.First();
            foreach (var h in _open.Values) h.PushTheme();
        }
        if (sdk)
        {
            LoadSdk();
            foreach (var h in _open.Values.ToList()) await h.ReloadAsync();
        }
        foreach (var (folder, manifestChanged) in folders)
        {
            if (sdk && !manifestChanged) continue; // 이미 다시 불러옴
            await ReloadWidgetFolderAsync(folder, manifestChanged);
        }
        if (themes || sdk || layouts || folders.Count > 0) Changed?.Invoke();
    }

    async Task ReloadWidgetFolderAsync(string folder, bool manifestChanged)
    {
        var dir = Path.Combine(Paths.Widgets, folder);
        var oldId = Installed.FirstOrDefault(kv => SameFolder(kv.Value.Folder, dir)).Key;

        if (!manifestChanged)
        {
            if (oldId != null && _open.TryGetValue(oldId, out var host)) await host.ReloadAsync();
            return;
        }

        if (!Directory.Exists(dir))
        {
            InstallErrors.Remove(folder);
            if (oldId != null) { CloseWidget(oldId); Installed.Remove(oldId); Log.Info($"위젯 제거됨: {oldId}"); }
            return;
        }

        if (oldId != null) Installed.Remove(oldId);
        var m = TryInstall(dir);
        if (m == null)
        {
            // manifest가 깨짐: 열려 있던 창은 두고 오류 카드를 보여 준다.
            if (oldId != null && _open.TryGetValue(oldId, out var host))
            {
                Installed[oldId] = host.Manifest;
                host.ShowError(InstallErrors.GetValueOrDefault(folder) ?? "widget.json 오류");
            }
            return;
        }
        if (oldId != null && oldId != m.Id) CloseWidget(oldId);
        if (_open.ContainsKey(m.Id)) { await RecreateAsync(m.Id); Log.Info($"[{m.Id}] widget.json 변경 → 창 다시 생성"); }
        else if (StateOf(m.Id).Enabled) await OpenWidgetAsync(m.Id);
    }

    // =========================================================== 상태 저장
    public void SaveStateSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void SaveNow()
    {
        _saveTimer.Stop();
        try { State.Save(AppPaths.StateFile); }
        catch (Exception ex) { Log.Error("state.json 저장 실패", ex); }
    }

    // =========================================================== Manager용 스냅샷
    public object Snapshot() => new
    {
        widgets = Installed.Values.OrderBy(m => m.Name, StringComparer.CurrentCulture).Select(m =>
        {
            var st = StateOf(m.Id);
            _open.TryGetValue(m.Id, out var h);
            return new
            {
                id = m.Id, name = m.Name, description = m.Description, version = m.Version, folder = m.Folder,
                enabled = st.Enabled, running = h != null, error = h?.LastError,
                windowMode = st.WindowMode ?? m.WindowMode, effectiveMode = h?.Window?.Mode,
                opacity = st.Opacity, locked = st.Locked, hoverFade = st.HoverFade,
                permissions = m.Permissions, subscribe = m.Subscribe,
                schema = m.Settings, settingOrder = m.Settings.Keys.ToList(), settings = m.EffectiveSettings(st.Settings),
            };
        }).ToList(),
        invalid = InstallErrors.Select(kv => new { folder = kv.Key, error = kv.Value }).ToList(),
        themes = Themes.Values.OrderBy(t => t.Name).Select(t => new { key = t.Key, name = t.Name, tokens = t.Tokens }).ToList(),
        theme = State.Theme,
        themeCustom = State.ThemeCustom,
        themeErrors = ThemeErrors,
        fonts = Fonts.Values.Select(f => new { key = f.Key, name = f.Name, description = f.Description }).ToList(),
        font = CurrentFont?.Key,
        layouts = Layouts().Select(l => new { key = l.Key, name = l.Name, theme = l.Theme, widgets = l.Widgets.Keys.ToList() }).ToList(),
        allLocked = State.AllLocked,
        allHidden = State.AllHidden,
        providers = State.Providers,
        photosFolder = PhotosFolder,
        wallpaper = new
        {
            enabled = State.Wallpaper.Enabled,
            intervalMinutes = State.Wallpaper.IntervalMinutes,
            fit = State.Wallpaper.Fit,
            current = _wallpaper?.CurrentName,
            count = _wallpaper?.Count ?? 0,
            error = _wallpaper?.LastError,
        },
        root = Paths.Root,
        dev = DevMode,
        app = new
        {
            version = typeof(AppController).Assembly.GetName().Version?.ToString(3),
            autoStart = AutoStart.IsEnabled,
            logs = AppPaths.Logs,
            update = Updates.Snapshot(),
            checkUpdates = State.CheckUpdates,
            pauseOnFullscreen = State.PauseOnFullscreen,
        },
        calendar = CalendarSnapshot(),
        sunTime = new
        {
            themeAuto = State.ThemeAuto,
            effectiveTheme = Themes.GetValueOrDefault(EffectiveThemeKey)?.Name,
            alerts = State.Alerts,
        },
    };
}
