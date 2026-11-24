using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Darkroom.App.Win32;
using Darkroom.Core;
using Microsoft.Web.WebView2.Core;

namespace Darkroom.App;

/// <summary>
/// "web" 권한 위젯 안에 띄우는 두 번째 웹 화면(예: 네이버 지도 모바일 웹). 위젯 문서가 자리(사각형)와 주소를 정하면
/// 그 자리에 겹쳐 띄운다. 위젯 SDK·호스트 메시지는 없고, manifest의 web.allow 사이트 안에서만 이동한다.
/// 그 밖의 링크는 사용자가 직접 눌렀을 때만 기본 브라우저로 연다.
/// </summary>
internal sealed class EmbeddedWeb : IDisposable
{
    readonly AppController _app;
    readonly WidgetManifest _manifest;
    readonly WidgetWindow _win;
    CoreWebView2Controller? _ctl;
    IntPtr _child;             // 이 웹 화면의 창(위젯 본체 웹 화면 위로 올리고 모서리를 둥글린다)
    Task? _creating;
    (double x, double y, double w, double h) _rect;
    bool _shown, _hiddenByUser, _closed;

    public EmbeddedWeb(AppController app, WidgetManifest manifest, WidgetWindow win)
    {
        _app = app; _manifest = manifest; _win = win;
        _hiddenByUser = app.State.AllHidden;
    }

    WebSpec Spec => _manifest.Web!;

    /// <summary>위젯 → { type: "web", op: "show"|"navigate"|"hide", url?, rect?: { x, y, width, height } }(CSS 픽셀)</summary>
    public async Task HandleAsync(JsonElement m)
    {
        string op = m.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "";
        string? url = m.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        if (m.TryGetProperty("rect", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            double N(string n) => r.TryGetProperty(n, out var v) && v.TryGetDouble(out var d) && double.IsFinite(d) ? Math.Max(0, d) : 0;
            _rect = (N("x"), N("y"), N("width"), N("height"));
        }
        try
        {
            switch (op)
            {
                case "show":
                case "navigate":
                    if (url != null && !Spec.Allows(url)) { Log.Warn($"[{_manifest.Id}] 허용하지 않은 주소: {url}"); return; }
                    await EnsureCreatedAsync();
                    if (_ctl == null) return;
                    _shown = true;
                    UpdateBounds();
                    ApplyVisibility();
                    if (url != null) _ctl.CoreWebView2.Navigate(url);
                    break;
                case "hide":
                    _shown = false;
                    ApplyVisibility();
                    break;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Log.Error($"[{_manifest.Id}] 웹 화면 오류", ex);
        }
    }

    Task EnsureCreatedAsync() => _ctl != null || _closed ? Task.CompletedTask : (_creating ??= CreateAsync());

    async Task CreateAsync()
    {
        var before = Children(_win.Hwnd);
        var ctl = await _app.Env.CreateCoreWebView2ControllerAsync(_win.Hwnd);
        if (_closed || !_win.IsAlive) { ctl.Close(); return; }
        _ctl = ctl;
        // 새로 생긴 자식 창 = 이 웹 화면. 위젯 본체(창 전체를 덮는 투명 웹 화면)보다 위에 둔다.
        _child = Children(_win.Hwnd).FirstOrDefault(h => !before.Contains(h));
        if (_child != IntPtr.Zero) SetWindowPos(_child, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        else Log.Warn($"[{_manifest.Id}] 웹 화면 창을 찾지 못함(겹침 순서를 못 맞춤)");
        ctl.ShouldDetectMonitorScaleChanges = false;
        ctl.RasterizationScale = _win.Scale;
        ctl.IsVisible = false;
        var core = ctl.CoreWebView2;
        var s = core.Settings;
        // 모바일 화면을 받도록 휴대폰 브라우저처럼 보인다(좁은 위젯에 맞는 화면)
        var major = _app.Env.BrowserVersionString.Split('.')[0];
        s.UserAgent = $"Mozilla/5.0 (Linux; Android 14; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Mobile Safari/537.36";
        s.AreDevToolsEnabled = _app.DevMode;
        s.AreDefaultContextMenusEnabled = _app.DevMode;
        s.AreBrowserAcceleratorKeysEnabled = _app.DevMode;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;

        core.NavigationStarting += (_, e) =>
        {
            if (Spec.Allows(e.Uri)) return;
            e.Cancel = true;
            if (e.IsUserInitiated) OpenInBrowser(e.Uri);
            else Log.Info($"[{_manifest.Id}] 웹 화면 밖으로 이동 막음: {Shorten(e.Uri)}");
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Spec.Allows(e.Uri)) core.Navigate(e.Uri);
            else if (e.IsUserInitiated) OpenInBrowser(e.Uri);
        };
        // 위치·카메라·알림 등은 모두 거절(위치는 위젯이 주소에 좌표를 넣어 준다)
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.ProcessFailed += (_, e) => Log.Warn($"[{_manifest.Id}] 웹 화면 프로세스 실패: {e.ProcessFailedKind}");
        // 위젯 안에서는 스크롤바를 숨긴다(내용은 그대로 스크롤된다)
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "addEventListener('DOMContentLoaded',()=>{const s=document.createElement('style');" +
            "s.textContent='::-webkit-scrollbar{width:0!important;height:0!important}';(document.head||document.documentElement).append(s)});");
        if (_app.DevMode) core.SourceChanged += (_, _) => Log.Info($"[{_manifest.Id}] 웹 화면: {core.Source}");
    }

    static string Shorten(string uri) => uri.Length > 120 ? uri[..120] + "…" : uri;

    void OpenInBrowser(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Warn($"브라우저 열기 실패: {ex.Message}"); }
    }

    public void UpdateBounds()
    {
        if (_ctl == null) return;
        _ctl.RasterizationScale = _win.Scale;
        double k = _win.Scale;
        Native.GetClientRect(_win.Hwnd, out var rc);
        int x = (int)Math.Round(_rect.x * k), y = (int)Math.Round(_rect.y * k);
        int w = Math.Min((int)Math.Round(_rect.w * k), rc.W - x), h = Math.Min((int)Math.Round(_rect.h * k), rc.H - y);
        _ctl.Bounds = new System.Drawing.Rectangle(x, y, Math.Max(0, w), Math.Max(0, h));
        // 모바일 웹은 폭 360px 안팎을 기준으로 만든다 → 더 좁으면 살짝 축소해서 가로 스크롤이 생기지 않게
        if (_rect.w > 0) _ctl.ZoomFactor = Math.Clamp(_rect.w / MobileWidth, 0.75, 1.0);
        if (_child != IntPtr.Zero && w > 0 && h > 0)
        {
            int d = (int)Math.Round(CornerDip * 2 * k);
            var rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d);
            if (SetWindowRgn(_child, rgn, true) == 0) DeleteObject(rgn);
        }
    }

    const double CornerDip = 12, MobileWidth = 360;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("gdi32")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("user32")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32")] static extern bool DeleteObject(IntPtr o);

    /// <summary>직계 자식 창들.</summary>
    static HashSet<IntPtr> Children(IntPtr parent)
    {
        var set = new HashSet<IntPtr>();
        for (var h = GetWindow(parent, 5 /* GW_CHILD */); h != IntPtr.Zero && set.Count < 64; h = GetWindow(h, 2 /* GW_HWNDNEXT */)) set.Add(h);
        return set;
    }

    public void SetHidden(bool hidden)
    {
        _hiddenByUser = hidden;
        ApplyVisibility();
    }

    public void Hide()
    {
        _shown = false;
        ApplyVisibility();
    }

    void ApplyVisibility()
    {
        if (_ctl == null) return;
        try { _ctl.IsVisible = _shown && !_hiddenByUser; }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { _ctl?.Close(); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        _ctl = null;
    }
}
