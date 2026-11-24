using Darkroom.Core;
using static Darkroom.App.Win32.Native;

namespace Darkroom.App.Win32;

/// <summary>
/// 위젯 1개의 Win32 창. 테두리 없음, WS_EX_NOREDIRECTIONBITMAP(투명 합성), 작업 표시줄·Alt+Tab에서 숨김.
/// 창 모드(desktop/bottom/normal/topmost), 드래그, 스냅, 모니터별 DPI를 처리한다. WebView2는 WidgetHost가 붙인다.
/// </summary>
internal sealed class WidgetWindow : IDisposable
{
    const string ClassName = "DarkroomWidget";
    static readonly Dictionary<IntPtr, WidgetWindow> s_windows = new();

    readonly int _wDip, _hDip, _insetDip;
    IntPtr _parent;                 // desktop 모드: Progman
    bool _disposing, _showDesktopTop, _dragging;
    RECT _dragStart;
    POINT _dragCursor;
    Action<bool>? _showDesktopListener;

    public IntPtr Hwnd { get; }
    public string Mode { get; private set; } = WindowModes.Normal;   // 실제 적용된 모드 (desktop 실패 시 bottom)
    public double Scale { get; private set; }
    public bool Locked { get; set; }
    /// <summary>스냅 대상: 다른 위젯들의 카드 사각형(화면 좌표).</summary>
    public Func<IEnumerable<RECT>>? SnapTargets { get; set; }

    public event Action? MoveFinished;
    public event Action? Resized;
    public event Action? ScaleChanged;
    /// <summary>우리가 닫지 않았는데 창이 사라짐(예: Explorer 재시작으로 Progman 파괴).</summary>
    public event Action? LostUnexpectedly;

    public WidgetWindow(string title, string mode, int x, int y, WidgetSize size)
    {
        EnsureClass(ClassName, StaticProc);
        _wDip = size.Width; _hDip = size.Height; _insetDip = size.Inset;
        Scale = ScaleOf(MonitorFromPoint(new POINT { X = x + 1, Y = y + 1 }, MONITOR_DEFAULTTONEAREST));
        Hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP, ClassName, title, WS_POPUP | WS_CLIPCHILDREN,
            x, y, Px(_wDip), Px(_hDip), IntPtr.Zero, IntPtr.Zero, GetModuleHandle(IntPtr.Zero), IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx 실패");
        s_windows[Hwnd] = this;
        ApplyMode(mode);
    }

    int Px(int dip) => (int)Math.Round(dip * Scale);

    public RECT ScreenRect { get { GetWindowRect(Hwnd, out var r); return r; } }

    public RECT CardRect
    {
        get
        {
            var r = ScreenRect; int i = Px(_insetDip);
            return new RECT(r.Left + i, r.Top + i, r.Right - i, r.Bottom - i);
        }
    }

    public bool IsAlive => !_disposing && IsWindow(Hwnd);

    // ---------- 창 영역(region) ----------
    /// <summary>카드 바깥으로 남겨 두는 그림자 여백(DIP). 테마 그림자는 이 안에 들어오게 만든다. 스냅 간격도 이 값.</summary>
    public const int ShadowMarginDip = 10;
    double _cornerDip = 18;

    /// <summary>테마의 카드 모서리 반경(DIP). 창 영역의 둥근 모서리에 쓴다.</summary>
    public void SetCornerRadius(double dip)
    {
        _cornerDip = Math.Clamp(dip, 0, 64);
        ApplyRegion();
    }

    /// <summary>
    /// desktop 모드(Progman의 자식 창)에서는 형제 창의 투명한 부분이 아래 위젯을 비춰 주지 않고 바탕화면을 보여 준다
    /// → 붙여 놓은 위젯이 네모로 잘려 보인다. 그래서 창 영역을 "카드 + 그림자 여백"의 둥근 사각형으로 줄인다.
    /// 최상위 창(bottom/normal/topmost)은 DWM이 창마다 합성하므로 필요 없다.
    /// </summary>
    void ApplyRegion()
    {
        if (Mode != WindowModes.Desktop) return;
        GetClientRect(Hwnd, out var rc);
        int m = Px(Math.Max(0, _insetDip - ShadowMarginDip));
        int d = (int)Math.Round((_cornerDip + ShadowMarginDip) * Scale * 2);
        var rgn = CreateRoundRectRgn(m, m, rc.W - m + 1, rc.H - m + 1, d, d);
        if (SetWindowRgn(Hwnd, rgn, true) == 0) DeleteObject(rgn); // 성공하면 시스템이 region을 소유
    }

    [System.Runtime.InteropServices.DllImport("gdi32")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [System.Runtime.InteropServices.DllImport("gdi32")] static extern bool DeleteObject(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);

    // ---------- 창 모드 ----------
    void ApplyMode(string mode)
    {
        if (mode == WindowModes.Desktop)
        {
            var progman = DesktopLayer.FindProgman();
            if (progman != IntPtr.Zero)
            {
                var r = ScreenRect;
                long style = (long)GetWindowLongPtr(Hwnd, GWL_STYLE);
                // 위젯 창은 GDI로 그리지 않고 DirectComposition으로만 합성되므로 형제 클리핑(WS_CLIPSIBLINGS)은 쓰지 않는다.
                // 겹침 처리는 ApplyRegion 참고.
                style = (style & ~(long)(WS_POPUP | WS_CLIPSIBLINGS)) | WS_CHILD;
                SetWindowLongPtr(Hwnd, GWL_STYLE, new IntPtr(style));
                SetParent(Hwnd, progman);
                _parent = progman;
                Mode = WindowModes.Desktop;
                MoveTo(r.Left, r.Top);
                SetWindowPos(Hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                ApplyRegion();
                return;
            }
            mode = WindowModes.Bottom;
        }
        Mode = mode;
        if (mode == WindowModes.Bottom)
        {
            SetWindowPos(Hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            _showDesktopListener = OnShowDesktopChanged;
            ShowDesktopWatcher.Add(_showDesktopListener);
        }
        else if (mode == WindowModes.Topmost)
            SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    void OnShowDesktopChanged(bool showing)
    {
        _showDesktopTop = showing;
        if (showing)
            SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        else
        {
            SetWindowPos(Hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowPos(Hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    // ---------- 위치·표시 ----------
    /// <summary>화면 좌표로 이동. desktop 모드에선 Progman 클라이언트 좌표로 바꿔 넣는다.</summary>
    public void MoveTo(int x, int y)
    {
        if (_parent != IntPtr.Zero)
        {
            var p = new POINT { X = x, Y = y };
            ScreenToClient(_parent, ref p);
            x = p.X; y = p.Y;
        }
        SetWindowPos(Hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void SetVisible(bool visible)
    {
        ShowWindow(Hwnd, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
        if (visible && Mode == WindowModes.Desktop)
            SetWindowPos(Hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 드래그 시작. 제목 표시줄 끌기(WM_NCLBUTTONDOWN+HTCAPTION)는 Progman 자식 창에서 끄는 동안 화면이 따라오지 않아,
    /// 마우스를 직접 붙잡고(SetCapture) 움직일 때마다 창을 옮긴다. 위치는 매번 "시작 위치 + 커서 이동량"으로 다시 구해 스냅한다.
    /// </summary>
    public void BeginDrag()
    {
        if (Locked || _dragging) return;
        if (GetKeyState(0x01 /*VK_LBUTTON*/) >= 0) return; // 메시지가 늦게 와서 이미 버튼을 뗐다
        _dragStart = ScreenRect;
        GetCursorPos(out _dragCursor);
        _dragging = true;
        ReleaseCapture();   // WebView2 쪽이 잡고 있던 마우스를 놓게 하고
        SetCapture(Hwnd);   // 이 창이 받는다
    }

    unsafe void DragTo()
    {
        GetCursorPos(out var c);
        var raw = new RECT(_dragStart.Left + c.X - _dragCursor.X, _dragStart.Top + c.Y - _dragCursor.Y, 0, 0);
        raw.Right = raw.Left + _dragStart.W; raw.Bottom = raw.Top + _dragStart.H;
        Snap(&raw);
        MoveTo(raw.Left, raw.Top);
    }

    void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (GetCapture() == Hwnd) ReleaseCapture();
        UpdateScaleFromPosition();
        MoveFinished?.Invoke();
    }

    /// <summary>창이 있는 모니터의 배율로 크기를 맞춘다. 바뀌었으면 true.</summary>
    public bool UpdateScaleFromPosition()
    {
        var r = ScreenRect;
        double s = ScaleOf(MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST));
        if (Math.Abs(s - Scale) < 0.001) return false;
        Scale = s;
        SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, Px(_wDip), Px(_hDip), SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        ScaleChanged?.Invoke();
        return true;
    }

    /// <summary>카드가 어느 모니터 작업 영역에도 40px 이상 걸쳐 있지 않으면 가장 가까운 모니터 안쪽 가장자리로 당긴다
    /// (여러 위젯이 한곳에 겹치지 않고 배치가 유지되게).</summary>
    public bool EnsureOnScreen()
    {
        var card = CardRect;
        foreach (var a in WorkAreas())
        {
            int ix = Math.Min(card.Right, a.Right) - Math.Max(card.Left, a.Left);
            int iy = Math.Min(card.Bottom, a.Bottom) - Math.Max(card.Top, a.Top);
            if (ix >= 40 && iy >= 40) return false;
        }
        var wa = MonitorInfo(MonitorFromRect(ref card, MONITOR_DEFAULTTONEAREST)).rcWork;
        int left = Math.Max(wa.Left, Math.Min(card.Left, wa.Right - card.W));
        int top = Math.Max(wa.Top, Math.Min(card.Top, wa.Bottom - card.H));
        int inset = Px(_insetDip);
        MoveTo(left - inset, top - inset);
        UpdateScaleFromPosition();
        Log.Info($"화면 밖 위젯을 화면 안으로 당김: {Hwnd:X}");
        return true;
    }

    // ---------- 스냅 ----------
    unsafe void Snap(RECT* r)
    {
        if (GetKeyState(0x10 /*VK_SHIFT*/) < 0) return; // Shift를 누르면 스냅 없이 자유 이동
        // 이웃 카드와의 간격 = 그림자 여백 → 이웃 창 영역이 이 카드를 덮지 않는다.
        int inset = Px(_insetDip), th = Px(10), gap = Px(ShadowMarginDip), grid = Px(8);
        var card = new RECT(r->Left + inset, r->Top + inset, r->Right - inset, r->Bottom - inset);
        var work = MonitorInfo(MonitorFromRect(ref card, MONITOR_DEFAULTTONEAREST)).rcWork;
        var others = SnapTargets?.Invoke().ToList() ?? new List<RECT>();

        int dx = Best(card.Left, card.Right, work.Left, work.Right, others.Select(o => (o.Left, o.Right)), th, gap, grid);
        int dy = Best(card.Top, card.Bottom, work.Top, work.Bottom, others.Select(o => (o.Top, o.Bottom)), th, gap, grid);
        r->Left += dx; r->Right += dx; r->Top += dy; r->Bottom += dy;
    }

    /// <summary>한 축에서 가장 가까운 스냅 위치까지의 이동량. 화면 가장자리·다른 위젯이 우선, 없으면 8px 그리드.</summary>
    internal static int Best(int lo, int hi, int workLo, int workHi, IEnumerable<(int lo, int hi)> others, int th, int gap, int grid)
    {
        var candidates = new List<int> { workLo - lo, workHi - hi };
        foreach (var (olo, ohi) in others)
        {
            candidates.Add(olo - lo);          // 시작 가장자리 맞춤
            candidates.Add(ohi - hi);          // 끝 가장자리 맞춤
            candidates.Add(ohi + gap - lo);    // 오른쪽/아래에 붙이기
            candidates.Add(olo - gap - hi);    // 왼쪽/위에 붙이기
        }
        int best = int.MaxValue;
        foreach (var c in candidates) if (Math.Abs(c) <= th && Math.Abs(c) < Math.Abs(best)) best = c;
        if (best != int.MaxValue) return best;
        int rel = lo - workLo;
        return (int)Math.Round(rel / (double)grid) * grid - rel;
    }

    // ---------- 창 프로시저 ----------
    static IntPtr StaticProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        => s_windows.TryGetValue(h, out var win) ? win.Proc(h, msg, w, l) : DefWindowProc(h, msg, w, l);

    unsafe IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return new IntPtr(1);
            case WM_PAINT:
                ValidateRect(h, IntPtr.Zero);
                return IntPtr.Zero;
            case WM_SIZE:
                ApplyRegion();
                Resized?.Invoke();
                break;
            case WM_MOUSEMOVE when _dragging:
                if (GetKeyState(0x01 /*VK_LBUTTON*/) >= 0) EndDrag(); // 버튼을 뗀 알림을 놓쳤을 때
                else DragTo();
                return IntPtr.Zero;
            case WM_LBUTTONUP when _dragging:
                DragTo();
                EndDrag();
                return IntPtr.Zero;
            case WM_CAPTURECHANGED when _dragging:
                EndDrag(); // 다른 창이 마우스를 가져감(Alt+Tab 등)
                break;
            case WM_DPICHANGED:
                UpdateScaleFromPosition(); // 제안 사각형 대신 직접 계산(위치는 유지)
                return IntPtr.Zero;
            case WM_WINDOWPOSCHANGING when Mode == WindowModes.Bottom && !_showDesktopTop:
                ((WINDOWPOS*)l)->hwndInsertAfter = HWND_BOTTOM;
                break;
            case WM_MOUSEACTIVATE when Mode is WindowModes.Bottom or WindowModes.Desktop:
                return new IntPtr(MA_NOACTIVATE);
            case WM_DESTROY:
                s_windows.Remove(h);
                if (_showDesktopListener != null) { ShowDesktopWatcher.Remove(_showDesktopListener); _showDesktopListener = null; }
                if (!_disposing) LostUnexpectedly?.Invoke();
                break;
        }
        return DefWindowProc(h, msg, w, l);
    }

    public void Dispose()
    {
        if (_disposing) return;
        _disposing = true;
        if (IsWindow(Hwnd)) DestroyWindow(Hwnd);
        if (_showDesktopListener != null) { ShowDesktopWatcher.Remove(_showDesktopListener); _showDesktopListener = null; }
        s_windows.Remove(Hwnd);
    }
}
