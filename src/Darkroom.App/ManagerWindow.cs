using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Darkroom.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Darkroom.App;

/// <summary>Manager 창. UI는 HTML(Manager/web)이고 위젯과 같은 WebView2 환경을 쓴다.</summary>
internal sealed class ManagerWindow : Window
{
    const string Host = "manager.darkroom";
    readonly AppController _app;
    readonly WebView2 _web = new();
    bool _ready;

    public ManagerWindow(AppController app)
    {
        _app = app;
        Title = "Darkroom Manager";
        Width = 980; Height = 680; MinWidth = 760; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24));
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x24, 0x24, 0x24);
        Content = _web;
        SourceInitialized += (_, _) => Win32.Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += async (_, _) => await InitAsync();
        _app.Changed += PushState;
        Closed += (_, _) => { _app.Changed -= PushState; _web.Dispose(); };
    }

    async Task InitAsync()
    {
        await _web.EnsureCoreWebView2Async(_app.Env);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = _app.DevMode;
        core.Settings.AreDefaultContextMenusEnabled = _app.DevMode;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping(Host, AppPaths.ManagerWeb, CoreWebView2HostResourceAccessKind.DenyCors);
        core.AddWebResourceRequestedFilter(AppController.AssetFilter, CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => _app.TryServeAsset(e, allowIcons: true);
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += async (_, e) => await OnMessageAsync(e.WebMessageAsJson);
        core.NavigationCompleted += (_, _) => { _ready = true; PushState(); };
        core.Navigate($"https://{Host}/index.html");
    }

    void PushState()
    {
        if (!_ready) return;
        Post(new { type = "state", state = _app.Snapshot() });
    }

    void Post(object msg)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg, DrJson.Compact)); }
        catch (InvalidOperationException) { }
    }

    string? PickPhotosFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "사진 위젯에 보여 줄 폴더를 고르세요 (하위 폴더 포함)",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_app.PhotosFolder) ? _app.PhotosFolder : "",
        };
        var owner = new Win32Owner(new WindowInteropHelper(this).Handle);
        if (dlg.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return null;
        var next = _app.State.Providers.Clone();
        next.PhotosFolder = dlg.SelectedPath;
        return _app.SetProviderSettings(next);
    }

    /// <summary>바로가기 목록에 넣을 앱(시작 메뉴 바로가기·exe) 또는 폴더를 고른다 → 웹 쪽에 "picked"로 돌려준다.</summary>
    void PickLink(string id, string key, bool folder)
    {
        var owner = new Win32Owner(new WindowInteropHelper(this).Handle);
        string? path = null, name = null;
        if (folder)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "바로가기에 넣을 폴더를 고르세요", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK) { path = dlg.SelectedPath; name = Path.GetFileName(path.TrimEnd('\\')); if (name.Length == 0) name = path; }
        }
        else
        {
            using var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "바로가기에 넣을 앱을 고르세요 (시작 메뉴)",
                Filter = "앱·바로가기 (*.lnk;*.exe;*.url)|*.lnk;*.exe;*.url|모든 파일 (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                DereferenceLinks = false,
            };
            if (dlg.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK) { path = dlg.FileName; name = Path.GetFileNameWithoutExtension(path); }
        }
        if (path != null) Post(new { type = "picked", id, key, item = new { name = name!.Length > 40 ? name[..40] : name, target = path } });
    }

    sealed class Win32Owner(IntPtr handle) : System.Windows.Forms.IWin32Window { public IntPtr Handle => handle; }

    async Task OnMessageAsync(string json)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(json).RootElement; } catch (JsonException) { return; }
        string S(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        bool B(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        JsonElement V() => m.TryGetProperty("value", out var v) ? v.Clone() : default;

        string? error = null;
        try
        {
            switch (S("type"))
            {
                case "ready": PushState(); break;
                case "setEnabled": await _app.SetWidgetEnabledAsync(S("id"), B("enabled")); break;
                case "setProp": error = await _app.SetWidgetPropAsync(S("id"), S("prop"), V()); break;
                case "setSetting": error = _app.SetSetting(S("id"), S("key"), V()); break;
                case "setTheme": _app.SetTheme(S("key")); break;
                case "setFont": _app.SetFont(S("key")); break;
                case "setAllLocked": _app.SetAllLocked(B("value")); break;
                case "setAllHidden": _app.SetAllHidden(B("value")); break;
                case "saveLayout": _app.SaveLayout(S("name")); break;
                case "loadLayout": error = await _app.LoadLayoutAsync(S("key")); break;
                case "deleteLayout": _app.DeleteLayout(S("key")); break;
                case "openFolder": _app.OpenFolder(S("id") is { Length: > 0 } id ? id : null); break;
                case "setProviders":
                    var ps = V().Deserialize<ProviderSettings>(DrJson.Options);
                    error = ps == null ? "환경 설정 형식 오류" : _app.SetProviderSettings(ps);
                    break;
                case "pickPhotosFolder": error = PickPhotosFolder(); break;
                case "pickApp": PickLink(S("id"), S("key"), folder: false); break;
                case "pickFolder": PickLink(S("id"), S("key"), folder: true); break;
                case "searchCity": Post(new { type = "cities", query = S("query"), results = await AppController.SearchCitiesAsync(S("query")) }); break;
                case "setWallpaper":
                    var wv = V();
                    if (wv.ValueKind != JsonValueKind.Object) { error = "배경화면 설정 형식 오류"; break; }
                    error = _app.SetWallpaperSettings(
                        wv.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
                        wv.TryGetProperty("intervalMinutes", out var im) && im.TryGetInt32(out var mins) ? mins : 30,
                        wv.TryGetProperty("fit", out var ft) && ft.ValueKind == JsonValueKind.String ? ft.GetString()! : "fill");
                    break;
                case "nextWallpaper": _app.NextWallpaper(); break;
                case "setAutoStart": _app.SetAutoStart(B("value")); break;
                case "openLogs": _app.OpenLogs(); break;
                case "checkUpdate": await _app.CheckUpdatesAsync(); break;
                case "setCheckUpdates": _app.SetCheckUpdates(B("value")); break;
                case "setThemeAuto": _app.SetThemeAuto(B("value")); break;
                case "setAlerts":
                    var al = V().Deserialize<SunAlertSettings>(DrJson.Options);
                    error = al == null ? "알림 설정 형식 오류" : _app.SetAlerts(al);
                    break;
                case "installUpdate": await _app.InstallUpdateAsync(); break;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error("Manager 요청 처리 실패", ex);
        }
        if (error != null) Post(new { type = "error", message = error });
    }
}
