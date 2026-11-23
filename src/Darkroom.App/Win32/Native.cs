using System.Runtime.InteropServices;
using System.Text;

namespace Darkroom.App.Win32;

internal static class Native
{
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    public const int WS_POPUP = unchecked((int)0x80000000), WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;

    public const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014,
        WM_SETTINGCHANGE = 0x001A, WM_MOUSEACTIVATE = 0x0021, WM_WINDOWPOSCHANGING = 0x0046,
        WM_DISPLAYCHANGE = 0x007E, WM_NCLBUTTONDOWN = 0x00A1, WM_MOVING = 0x0216, WM_ENTERSIZEMOVE = 0x0231,
        WM_EXITSIZEMOVE = 0x0232, WM_DPICHANGED = 0x02E0;
    public const int HTCAPTION = 2, MA_NOACTIVATE = 3;

    public static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_BOTTOM = new(1), HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20,
        SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public readonly int W => Right - Left;
        public readonly int H => Bottom - Top;
        public readonly bool Intersects(RECT o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;
        public override readonly string ToString() => $"({Left},{Top} {W}x{H})";
    }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] public static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32", SetLastError = true)] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? name);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32")] public static extern bool ReleaseCapture();
    [DllImport("user32")] public static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32")] public static extern IntPtr GetCapture();
    public const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202, WM_CAPTURECHANGED = 0x0215;
    [DllImport("user32")] public static extern IntPtr LoadCursor(IntPtr inst, IntPtr id);
    [DllImport("user32")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32")] public static extern bool ValidateRect(IntPtr h, IntPtr r);
    [DllImport("user32")] public static extern short GetKeyState(int vk);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32")] public static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [DllImport("user32")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    public delegate bool MonitorEnumProc(IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data);
    [DllImport("user32")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("shcore")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("kernel32")] public static extern IntPtr GetModuleHandle(IntPtr name);

    public const uint MONITOR_DEFAULTTONEAREST = 2, MONITOR_DEFAULTTOPRIMARY = 1;

    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }

    public static MONITORINFO MonitorInfo(IntPtr mon)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        return mi;
    }

    public static double ScaleOf(IntPtr mon) => GetDpiForMonitor(mon, 0, out var dx, out _) == 0 ? dx / 96.0 : 1.0;

    public static List<RECT> WorkAreas()
    {
        var list = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref RECT _, IntPtr _) => { list.Add(MonitorInfo(m).rcWork); return true; }, IntPtr.Zero);
        return list;
    }

    public static RECT PrimaryWorkArea() => MonitorInfo(MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY)).rcWork;

    /// <summary>창 클래스 등록 헬퍼. 같은 이름은 한 번만.</summary>
    static readonly Dictionary<string, WndProc> s_classes = new();
    public static void EnsureClass(string name, WndProc proc)
    {
        if (s_classes.ContainsKey(name)) return;
        s_classes[name] = proc; // GC 방지
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = GetModuleHandle(IntPtr.Zero),
            hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)),
            lpszClassName = name,
        };
        if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx({name}) {Marshal.GetLastWin32Error()}");
    }

    /// <summary>WPF 창 제목 표시줄을 어둡게.</summary>
    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref on, 4);
    }
}
