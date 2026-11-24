using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Darkroom.App.Win32;
using Darkroom.Core;
using Darkroom.Providers;
using Microsoft.Web.WebView2.Core;

namespace Darkroom.App;

/// <summary>켜진 위젯 1개: Win32 창 + WebView2 + Data Bus 구독.</summary>
internal sealed class WidgetHost : IDisposable
{
    readonly AppController _app;
    readonly Dictionary<string, IDisposable> _subs = new();
    WidgetWindow? _win;
    CoreWebView2Controller? _ctl;
    string? _scriptId;
    bool _closed;
    string? _photosMappedTo;   // photos.darkroom에 매핑한 폴더 (photos를 구독하는 위젯만)
    EmbeddedWeb? _web;         // "web" 권한: 위젯 안에 띄운 웹 화면(네이버 지도 등)

    public WidgetManifest Manifest { get; private set; }
    public WidgetWindow? Window => _win;
    public string? LastError { get; private set; }
    WidgetState State => _app.StateOf(Manifest.Id);

    public WidgetHost(AppController app, WidgetManifest manifest) { _app = app; Manifest = manifest; }

    public async Task OpenAsync()
    {
        var st = State;
        if (st.X == null || st.Y == null) (st.X, st.Y) = _app.NextDefaultPosition();
        var mode = st.WindowMode ?? Manifest.WindowMode;
        _win = new WidgetWindow($"Darkroom - {Manifest.Name}", mode, st.X.Value, st.Y.Value, Manifest.Size)
        {
            Locked = st.Locked || _app.State.AllLocked,
            SnapTargets = () => _app.SnapTargetsExcept(this),
        };
        _win.SetCornerRadius(_app.ThemeCornerRadiusDip());
        _win.EnsureOnScreen(); // 화면 밖이면 안으로 당기되, 저장된 위치는 그대로 둔다(원래 화면으로 돌아오면 복귀)
        _win.MoveFinished += () => { SavePosition(); };
        _win.Resized += UpdateBounds;
        _win.ScaleChanged += () => { if (_ctl != null) _ctl.RasterizationScale = _win.Scale; UpdateBounds(); _web?.UpdateBounds(); };
        _win.LostUnexpectedly += () => _app.OnWidgetLost(this);
        _win.SetVisible(!_app.State.AllHidden);

        try
        {
            CoreWebView2Controller ctl;
            // 컨트롤러가 생기는 순간부터 투명하게(흰 깜빡임 방지). 실행하는 다른 프로그램에 상속되지 않게 바로 되돌린다.
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "0");
            try { ctl = await _app.Env.CreateCoreWebView2ControllerAsync(_win.Hwnd); }
            finally { Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", null); }
            if (_closed || !_win.IsAlive) { ctl.Close(); return; }
            _ctl = ctl;
            await ConfigureAsync();
            Navigate();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            LastError = "WebView2 생성 실패: " + ex.Message;
            Log.Error($"[{Manifest.Id}] {LastError}", ex);
        }
    }

    async Task ConfigureAsync()
    {
        var ctl = _ctl!;
        ctl.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        ctl.ShouldDetectMonitorScaleChanges = false;
        ctl.RasterizationScale = _win!.Scale;
        UpdateBounds();
        ctl.IsVisible = !_app.State.AllHidden;

        var core = ctl.CoreWebView2;
        // 위젯은 늘 떠 있고 상호작용이 적어 저메모리 모드로 둔다. DARKROOM_MEMORY_NORMAL=1이면 끈다.
        if (Environment.GetEnvironmentVariable("DARKROOM_MEMORY_NORMAL") != "1")
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        var s = core.Settings;
        s.AreDevToolsEnabled = _app.DevMode;                 // 개발 모드: F12
        s.AreBrowserAcceleratorKeysEnabled = _app.DevMode;
        s.AreDefaultContextMenusEnabled = _app.DevMode;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;

        // 위젯 파일: 폴더 매핑 대신 호스트가 직접 응답한다. 응답 헤더에 CSP를 붙여야 WebSocket·EventSource까지 막을 수 있어서다
        // (WebResourceRequested는 WebSocket 연결을 보지 못한다).
        core.AddWebResourceRequestedFilter($"https://{Manifest.HostName}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => TryServeWidgetFile(e);
        // 내장 글꼴: https://assets.darkroom/fonts/... → sdk/fonts (호스트가 직접 응답)
        core.AddWebResourceRequestedFilter(AppController.AssetFilter, CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => _app.TryServeAsset(e, allowIcons: Manifest.HasPermission("launch"));
        MapPhotosCache();
        if (!Manifest.HasPermission("network"))
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += BlockExternalRequest;
        }
        core.NavigationStarting += (_, e) =>
        {
            if (IsOwnOrInternal(e.Uri)) return;
            e.Cancel = true;
            Log.Warn($"[{Manifest.Id}] 위젯 밖으로 이동 차단: {e.Uri}");
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                ShowError($"페이지를 열 수 없음 ({e.WebErrorStatus})");
        };
        core.ProcessFailed += (_, e) =>
        {
            Log.Warn($"[{Manifest.Id}] WebView2 프로세스 실패: {e.ProcessFailedKind} {e.Reason}");
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                ShowError("위젯이 응답하지 않아 멈췄습니다. 파일을 저장하면 다시 불러옵니다.");
        };

        foreach (var key in Manifest.Subscribe) SubscribeKey(key);
        await RefreshInitScriptAsync();
    }

    // ---------- 탐색 ----------
    string EntryUrl => $"https://{Manifest.HostName}/{Manifest.Entry.Replace('\\', '/').TrimStart('/')}";

    bool IsOwnOrInternal(string uri) =>
        uri.StartsWith($"https://{Manifest.HostName}/", StringComparison.OrdinalIgnoreCase)
        || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase);

    bool UsesPhotos => Manifest.Subscribe.Any(k => k.StartsWith("photos.", StringComparison.Ordinal));

    /// <summary>photos를 구독하는 위젯에만 사진 캐시(축소본) 폴더를 https://photos.darkroom/ 으로 보여 준다.
    /// 원본 사진 폴더는 매핑하지 않는다.</summary>
    void MapPhotosCache()
    {
        if (_ctl == null || _closed || !UsesPhotos || _photosMappedTo != null) return;
        Directory.CreateDirectory(PhotosProvider.CacheFolder);
        _ctl.CoreWebView2.SetVirtualHostNameToFolderMapping(PhotosProvider.HostName, PhotosProvider.CacheFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        _photosMappedTo = PhotosProvider.CacheFolder;
    }

    /// <summary>network 권한이 없는 위젯 문서에 붙이는 CSP. fetch·XHR·WebSocket·EventSource·sendBeacon을
    /// 자기 파일·내장 리소스·사진 캐시로만 제한한다(그 밖의 이미지·스크립트 요청은 BlockExternalRequest가 막는다).</summary>
    const string NoNetworkCsp = "connect-src 'self' https://" + AppController.AssetHost + " https://" + PhotosProvider.HostName + " data: blob:";

    static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".json"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8", [".svg"] = "image/svg+xml",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".avif"] = "image/avif", [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2", [".woff"] = "font/woff", [".ttf"] = "font/ttf", [".otf"] = "font/otf",
        [".wasm"] = "application/wasm", [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg",
        [".mp4"] = "video/mp4", [".webm"] = "video/webm",
    };

    /// <summary>https://&lt;위젯 주소&gt;/ 요청에 위젯 폴더의 파일로 응답한다. 폴더 밖 경로는 404.</summary>
    void TryServeWidgetFile(CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
            || !uri.Host.Equals(Manifest.HostName, StringComparison.OrdinalIgnoreCase)) return;
        if (e.Request.Method is not ("GET" or "HEAD"))
        {
            e.Response = _app.Env.CreateWebResourceResponse(null, 405, "Method Not Allowed", "");
            return;
        }
        string? path = null;
        try
        {
            var rel = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(Manifest.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) path = full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        byte[]? bytes = null;
        if (path != null)
        {
            try { bytes = File.ReadAllBytes(path); }
            catch (IOException) { bytes = null; } // 저장 중(Hot Reload)이면 잠깐 잠겨 있을 수 있다
        }
        if (bytes == null)
        {
            e.Response = _app.Env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain; charset=utf-8");
            return;
        }
        var type = MimeTypes.GetValueOrDefault(Path.GetExtension(path!), "application/octet-stream");
        var headers = $"Content-Type: {type}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff";
        if (!Manifest.HasPermission("network")) headers += "\r\nContent-Security-Policy: " + NoNetworkCsp;
        e.Response = _app.Env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), 200, "OK", headers);
    }

    void BlockExternalRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = e.Request.Uri;
        if (IsOwnOrInternal(uri) || uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return;
        if (AppController.IsAssetUri(uri)) return; // 내장 글꼴은 TryServeAsset이 응답
        if (_photosMappedTo != null && uri.StartsWith($"https://{PhotosProvider.HostName}/", StringComparison.OrdinalIgnoreCase)) return;
        e.Response = _app.Env.CreateWebResourceResponse(null, 403, "Blocked by Darkroom (network permission)", "");
        Log.Warn($"[{Manifest.Id}] network 권한 없음, 요청 차단: {uri}");
    }

    void Navigate()
    {
        LastError = null;
        _ctl?.CoreWebView2.Navigate(EntryUrl);
    }

    /// <summary>초기화 스크립트(설정·테마·창 옵션 + SDK)를 최신으로 바꾼다. 다음 문서부터 적용.</summary>
    async Task RefreshInitScriptAsync()
    {
        var core = _ctl!.CoreWebView2;
        if (_scriptId != null) core.RemoveScriptToExecuteOnDocumentCreated(_scriptId);
        var init = new
        {
            widget = new { id = Manifest.Id, name = Manifest.Name, version = Manifest.Version },
            settings = Manifest.EffectiveSettings(State.Settings),
            theme = _app.ThemePayload(),
            window = WindowPayload(),
            dev = _app.DevMode,
            network = Manifest.HasPermission("network"),
            web = Manifest.HasPermission("web"),
        };
        // JSON을 <script> 밖 JS 리터럴로 넣는다. U+2028/2029는 JSON 직렬화기가 이스케이프한다.
        var json = JsonSerializer.Serialize(init, DrJson.Compact);
        _scriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.__darkroomInit = {json};\n{_app.SdkSource}");
    }

    /// <summary>Hot Reload: 위치·설정은 그대로, 문서만 다시 읽는다.</summary>
    public async Task ReloadAsync()
    {
        if (_ctl == null || _closed) return;
        try
        {
            await RefreshInitScriptAsync();
            _web?.Hide(); // 새 문서가 다시 띄운다
            await _ctl.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
            Navigate();
            Log.Info($"[{Manifest.Id}] 다시 불러옴");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { Log.Error($"[{Manifest.Id}] 다시 불러오기 실패", ex); }
    }

    public void ShowError(string message)
    {
        LastError = message;
        Log.Warn($"[{Manifest.Id}] 오류 카드: {message}");
        if (_ctl == null || _closed) return;
        try { _ctl.CoreWebView2.NavigateToString(ErrorPage.Html(Manifest.Name, message)); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        _app.NotifyChanged();
    }

    // ---------- 위젯 → 호스트 ----------
    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsOwnOrInternal(e.Source)) return;
        JsonElement m;
        try { m = JsonDocument.Parse(e.WebMessageAsJson).RootElement; }
        catch (JsonException) { return; }
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("type", out var t)) return;
        string? key = m.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;

        switch (t.GetString())
        {
            case "drag":
                _win?.BeginDrag();
                break;
            case "subscribe" when key != null:
                SubscribeKey(key);
                break;
            case "unsubscribe" when key != null && !Manifest.Subscribe.Contains(key):
                if (_subs.Remove(key, out var sub)) sub.Dispose();
                break;
            case "action" when m.TryGetProperty("action", out var a):
                ActionRequest? req = null;
                try { req = a.Deserialize<ActionRequest>(DrJson.Options); } catch (JsonException) { }
                if (req != null) _ = _app.ExecuteActionAsync(this, req);
                break;
            case "web" when Manifest.HasPermission("web") && _win != null:
                _web ??= new EmbeddedWeb(_app, Manifest, _win);
                _ = _web.HandleAsync(m);
                break;
            case "log" when m.TryGetProperty("message", out var msg):
                var text = msg.ToString();
                if (text.Length > 2000) text = text[..2000];
                var level = m.TryGetProperty("level", out var lv) ? lv.ToString() : "info";
                if (level == "error") Log.Warn($"[{Manifest.Id}] JS: {text}"); else Log.Info($"[{Manifest.Id}] JS: {text}");
                break;
        }
    }

    /// <summary>구독에 권한이 필요한 네임스페이스. weather: 위치가 외부로 전송됨, calendar: 개인 일정.</summary>
    static readonly Dictionary<string, string> NamespacePermissions = new() { ["weather"] = "network", ["calendar"] = "calendar" };

    /// <summary>manifest의 subscribe에 선언한 키(또는 "ns.*")만 구독할 수 있다.</summary>
    bool MaySubscribe(string key)
    {
        if (!DataBus.IsValidKey(key)) return false;
        if (!Manifest.Subscribe.Any(s => s == key || (s.EndsWith(".*") && key.StartsWith(s[..^1], StringComparison.Ordinal)))) return false;
        if (NamespacePermissions.TryGetValue(DataBus.NamespaceOf(key), out var perm) && !Manifest.HasPermission(perm))
        {
            Log.Warn($"[{Manifest.Id}] '{key}' 구독에는 {perm} 권한이 필요");
            return false;
        }
        return true;
    }

    void SubscribeKey(string key)
    {
        if (key.EndsWith(".*")) return; // 와일드카드는 실제 키를 구독할 때 허용 범위로만 쓴다
        if (!_subs.ContainsKey(key))
        {
            if (!MaySubscribe(key)) { Log.Warn($"[{Manifest.Id}] 구독 거부: {key} (manifest subscribe·권한 확인)"); return; }
            var dispatcher = _app.Dispatcher;
            _subs[key] = _app.Bus.Subscribe(key, (k, v) => dispatcher.BeginInvoke(() => PostData(k, v)));
        }
        if (_app.Bus.TryGet(key, out var last)) PostData(key, last);
    }

    void PostData(string key, JsonElement value) => Post(new { type = "data", key, value });

    public void Post(object message)
    {
        if (_ctl == null || _closed) return;
        try { _ctl.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, DrJson.Compact)); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
    }

    // ---------- 호스트 → 위젯 ----------
    object WindowPayload()
    {
        var st = State;
        return new { opacity = Math.Clamp(st.Opacity, 0.1, 1.0), hoverFade = st.HoverFade, locked = IsLocked };
    }

    bool IsLocked => State.Locked || _app.State.AllLocked;

    public void PushSettings() => Post(new { type = "settings", settings = Manifest.EffectiveSettings(State.Settings) });
    public void PushTheme()
    {
        _win?.SetCornerRadius(_app.ThemeCornerRadiusDip());
        Post(new { type = "theme", theme = _app.ThemePayload() });
    }
    public void PushWindow()
    {
        if (_win != null) _win.Locked = IsLocked;
        Post(new { type = "window", window = WindowPayload() });
    }

    public void SetHidden(bool hidden)
    {
        _win?.SetVisible(!hidden);
        if (_ctl != null) _ctl.IsVisible = !hidden; // 숨기면 렌더링도 멈춘다
        _web?.SetHidden(hidden);
    }

    // ---------- 창 ----------
    void UpdateBounds()
    {
        if (_ctl == null || _win == null) return;
        Win32.Native.GetClientRect(_win.Hwnd, out var rc);
        _ctl.Bounds = new System.Drawing.Rectangle(0, 0, rc.W, rc.H);
    }

    void SavePosition()
    {
        if (_win == null) return;
        var r = _win.ScreenRect;
        var st = State;
        if (st.X == r.Left && st.Y == r.Top) return;
        st.X = r.Left; st.Y = r.Top;
        _app.SaveStateSoon();
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        foreach (var s in _subs.Values) s.Dispose();
        _subs.Clear();
        _web?.Dispose();
        _web = null;
        try { _ctl?.Close(); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        _ctl = null;
        _win?.Dispose();
    }
}

internal static class ErrorPage
{
    public static string Html(string widgetName, string message) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>
        html,body{margin:0;height:100%;background:transparent;overflow:hidden;font:13px "Segoe UI",sans-serif;user-select:none}
        .card{position:absolute;inset:8px;border-radius:14px;background:rgba(40,20,20,.85);border:1px solid rgba(224,80,60,.6);
              color:#f2dede;padding:14px 16px;box-sizing:border-box;overflow:hidden}
        b{color:#ff8a70;display:block;margin-bottom:6px}
        small{color:#b09a9a;display:block;margin-top:8px}
        </style></head><body><div class="card" data-drag>
        <b>⚠ {{WebUtility.HtmlEncode(widgetName)}} 로드 실패</b>{{WebUtility.HtmlEncode(message)}}
        <small>파일을 고쳐 저장하면 자동으로 다시 불러옵니다. 로그: %APPDATA%\Darkroom\logs</small>
        </div></body></html>
        """;
}
