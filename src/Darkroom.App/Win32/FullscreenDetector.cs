using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Darkroom.App.Win32;

/// <summary>
/// 지금 전체 화면으로 돌고 있는 앱(게임·영상·발표)이 덮고 있는 모니터를 찾는다.
/// 맨 앞 창이 모니터 전체를 덮거나(테두리 없는 전체 화면), Windows가 독점 전체 화면(D3D)이라고 알려 주면 그 모니터.
/// 최대화 창은 작업 표시줄이 보이므로 해당하지 않는다.
/// </summary>
internal static class FullscreenDetector
{
    [DllImport("user32")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("shell32")] static extern int SHQueryUserNotificationState(out int state);

    const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
    static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>전체 화면 앱이 덮은 모니터 핸들. 없으면 IntPtr.Zero.</summary>
    public static IntPtr CoveredMonitor()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero || !IsWindowVisible(h) || Native.IsIconic(h)) return IntPtr.Zero;
        GetWindowThreadProcessId(h, out var pid);
        if (pid == OwnPid) return IntPtr.Zero; // Darkroom 자신(Manager 등)
        if (ShellClasses.Contains(Native.ClassOf(h))) return IntPtr.Zero;
        var mon = MonitorFromWindow(h, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (SHQueryUserNotificationState(out var quns) == 0 && quns == QUNS_RUNNING_D3D_FULL_SCREEN) return mon;
        Native.GetWindowRect(h, out var r);
        var m = Native.MonitorInfo(mon).rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom ? mon : IntPtr.Zero;
    }

    public static IntPtr MonitorOf(IntPtr hwnd) => MonitorFromWindow(hwnd, 2);
}
