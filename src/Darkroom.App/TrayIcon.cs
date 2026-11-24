using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace Darkroom.App;

/// <summary>트레이 아이콘. 메뉴는 열 때마다 현재 상태로 다시 만든다.</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly AppController _app;
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.ContextMenuStrip _menu;
    readonly IntPtr _hIcon;

    public TrayIcon(AppController app, Action openManager, Action exit)
    {
        _app = app;
        _menu = new WinForms.ContextMenuStrip { Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColors()), ShowImageMargin = false, ShowCheckMargin = true };
        _menu.Opening += (_, _) => Build(openManager, exit);
        _hIcon = DrawIcon();
        _icon = new WinForms.NotifyIcon
        {
            Icon = Icon.FromHandle(_hIcon),
            Text = "Darkroom",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) openManager(); };
        _icon.BalloonTipClicked += (_, _) => openManager();
        _app.Updates.Found += OnUpdateFound;
        _app.Notify += OnNotify;
        Build(openManager, exit);
    }

    void OnNotify(string title, string text) => _app.Dispatcher.BeginInvoke(() =>
        _icon.ShowBalloonTip(10000, title, text, WinForms.ToolTipIcon.None));

    void OnUpdateFound(string version) => _app.Dispatcher.BeginInvoke(() =>
        _icon.ShowBalloonTip(8000, "Darkroom 업데이트", $"새 버전 {version}이 있습니다. 눌러서 Manager에서 업데이트하세요.", WinForms.ToolTipIcon.Info));

    void Build(Action openManager, Action exit)
    {
        _menu.Items.Clear();
        var fg = Color.FromArgb(242, 242, 242);

        WinForms.ToolStripMenuItem Item(string text, Action onClick, bool isChecked = false, bool enabled = true)
        {
            var i = new WinForms.ToolStripMenuItem(text) { Checked = isChecked, Enabled = enabled, ForeColor = fg };
            i.Click += (_, _) => onClick();
            return i;
        }
        WinForms.ToolStripMenuItem Sub(string text) => new(text) { ForeColor = fg };

        var widgets = Sub("위젯");
        foreach (var m in _app.Installed.Values.OrderBy(m => m.Name))
        {
            var id = m.Id;
            bool on = _app.StateOf(id).Enabled;
            widgets.DropDownItems.Add(Item(m.Name, () => _ = _app.SetWidgetEnabledAsync(id, !on), on));
        }
        if (widgets.DropDownItems.Count == 0) widgets.DropDownItems.Add(Item("(설치된 위젯 없음)", () => { }, enabled: false));

        var layouts = Sub("레이아웃");
        foreach (var l in _app.Layouts())
        {
            var key = l.Key;
            layouts.DropDownItems.Add(Item(l.Name, () => _ = _app.LoadLayoutAsync(key)));
        }
        layouts.DropDownItems.Add(new WinForms.ToolStripSeparator());
        layouts.DropDownItems.Add(Item("현재 배치를 레이아웃으로 저장", () => _app.SaveLayout($"레이아웃 {DateTime.Now:yyyy-MM-dd HH.mm}")));

        var themes = Sub("색");
        foreach (var t in _app.Themes.Values.OrderBy(t => t.Name))
        {
            var key = t.Key;
            themes.DropDownItems.Add(Item(t.Name, () => _app.SetTheme(key), _app.State.Theme == key));
        }

        var fonts = Sub("글꼴");
        foreach (var f in _app.Fonts.Values)
        {
            var key = f.Key;
            fonts.DropDownItems.Add(Item(f.Name, () => _app.SetFont(key), _app.State.Font == key));
        }

        _menu.Items.Add(widgets);
        _menu.Items.Add(layouts);
        _menu.Items.Add(themes);
        _menu.Items.Add(fonts);
        var wp = _app.State.Wallpaper;
        var wall = Sub("배경화면");
        wall.DropDownItems.Add(Item("사진 폴더로 바꾸기", () => _app.SetWallpaperSettings(!wp.Enabled, wp.IntervalMinutes, wp.Fit), wp.Enabled));
        wall.DropDownItems.Add(Item("다음 사진", _app.NextWallpaper, enabled: wp.Enabled));
        _menu.Items.Add(wall);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(Item("전체 잠금", () => _app.SetAllLocked(!_app.State.AllLocked), _app.State.AllLocked));
        _menu.Items.Add(Item("전체 숨기기", () => _app.SetAllHidden(!_app.State.AllHidden), _app.State.AllHidden));
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(Item("Manager 열기", openManager));
        bool auto = AutoStart.IsEnabled;
        _menu.Items.Add(Item("Windows 시작 시 실행", () => _app.SetAutoStart(!auto), auto));
        _menu.Items.Add(Item("위젯 폴더 열기", () => _app.OpenFolder(null)));
        if (_app.Updates.State == Updater.Status.Available)
            _menu.Items.Add(Item($"업데이트 ({_app.Updates.Latest})", () => _ = _app.InstallUpdateAsync()));
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(Item("종료", exit));
    }

    /// <summary>조리개 모양 아이콘을 코드로 그린다(이미지 파일 불필요).</summary>
    static IntPtr DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(255, 43, 43, 43));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var ring = new Pen(Color.FromArgb(224, 122, 46), 3f);
            g.DrawEllipse(ring, 3, 3, 26, 26);
            using var blade = new Pen(Color.FromArgb(200, 242, 242, 242), 1.6f);
            for (int i = 0; i < 6; i++)
            {
                double a = i * Math.PI / 3;
                float x1 = 16 + (float)(11 * Math.Cos(a)), y1 = 16 + (float)(11 * Math.Sin(a));
                float x2 = 16 + (float)(5 * Math.Cos(a + 1.9)), y2 = 16 + (float)(5 * Math.Sin(a + 1.9));
                g.DrawLine(blade, x1, y1, x2, y2);
            }
        }
        return bmp.GetHicon();
    }

    [DllImport("user32")] static extern bool DestroyIcon(IntPtr h);

    public void Dispose()
    {
        _app.Updates.Found -= OnUpdateFound;
        _app.Notify -= OnNotify;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        DestroyIcon(_hIcon);
    }

    sealed class DarkColors : WinForms.ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(43, 43, 43), Hover = Color.FromArgb(64, 64, 64), Line = Color.FromArgb(70, 70, 70);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
        public override Color CheckBackground => Color.FromArgb(90, 224, 122, 46);
        public override Color CheckSelectedBackground => Color.FromArgb(120, 224, 122, 46);
        public override Color CheckPressedBackground => Color.FromArgb(120, 224, 122, 46);
    }
}
