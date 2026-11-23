using Darkroom.Core;
using static Darkroom.App.Win32.Native;

namespace Darkroom.App.Win32;

/// <summary>바탕화면 층(Progman). 위젯을 Progman의 자식으로 넣어 아이콘(DefView) 위에 둔다.</summary>
internal static class DesktopLayer
{
    /// <summary>Progman을 찾는다. 0x052C는 Explorer가 WorkerW를 준비하게 하는 비공개 메시지(배경화면 도구들이 공통으로 사용).</summary>
    public static IntPtr FindProgman()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0x2 /*SMTO_ABORTIFHUNG*/, 1000, out _);
        // 24H2+ 구조: DefView가 Progman의 자식. 구형 구조(DefView가 별도 WorkerW 안)에선 Progman 자식 방식이 아이콘 아래로 가므로 쓰지 않는다.
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            Log.Warn("바탕화면 구조가 24H2+ 형태가 아님 → desktop 모드는 bottom 방식으로 대체");
            return IntPtr.Zero;
        }
        return progman;
    }
}

/// <summary>bottom 방식(대체 경로)용: 전경 창이 바탕화면이고 보이는 앱 창이 없으면 "바탕화면 보기" 상태로 본다.</summary>
internal static class ShowDesktopWatcher
{
    static readonly List<Action<bool>> s_listeners = new();
    static WinEventProc? s_proc;
    static IntPtr s_hook;
    static bool s_showing;

    public static bool Showing => s_showing;

    public static void Add(Action<bool> listener)
    {
        s_listeners.Add(listener);
        if (s_hook != IntPtr.Zero) return;
        s_proc = (_, _, hwnd, _, _, _, _) => Evaluate(hwnd);
        s_hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, s_proc, 0, 0, 0);
    }

    public static void Remove(Action<bool> listener)
    {
        s_listeners.Remove(listener);
        if (s_listeners.Count == 0 && s_hook != IntPtr.Zero) { UnhookWinEvent(s_hook); s_hook = IntPtr.Zero; s_showing = false; }
    }

    static void Evaluate(IntPtr fg)
    {
        bool showing = ClassOf(fg) is "Progman" or "WorkerW" && !AnyAppWindowVisible();
        if (showing == s_showing) return;
        s_showing = showing;
        foreach (var l in s_listeners.ToArray()) l(showing);
    }

    static bool AnyAppWindowVisible()
    {
        bool any = false;
        int myPid = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if (((long)GetWindowLongPtr(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            if (GetWindowLongPtr(h, GWLP_HWNDPARENT) != IntPtr.Zero) return true;
            DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out int cloaked, 4);
            if (cloaked != 0) return true;
            GetWindowThreadProcessId(h, out int pid);
            if (pid == myPid) return true;
            if (ClassOf(h) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            GetWindowRect(h, out var r);
            if (r.W <= 1 || r.H <= 1) return true;
            any = true;
            return false;
        }, IntPtr.Zero);
        return any;
    }
}

/// <summary>Explorer 재시작(TaskbarCreated)·디스플레이 변경을 받는 숨은 최상위 창.
/// 브로드캐스트 메시지는 메시지 전용 창(HWND_MESSAGE)에 오지 않으므로 보이지 않는 일반 창을 쓴다.</summary>
internal sealed class ShellEvents : IDisposable
{
    const string ClassName = "DarkroomShellEvents";
    static ShellEvents? s_instance;
    readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    readonly IntPtr _hwnd;

    public event Action? ExplorerRestarted;
    public event Action? DisplayChanged;

    public ShellEvents()
    {
        s_instance = this;
        EnsureClass(ClassName, (h, m, w, l) => s_instance != null ? s_instance.Proc(h, m, w, l) : DefWindowProc(h, m, w, l));
        _hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, ClassName, "Darkroom", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(IntPtr.Zero), IntPtr.Zero);
    }

    IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == _taskbarCreated) ExplorerRestarted?.Invoke();
        else if (msg == WM_DISPLAYCHANGE || (msg == WM_SETTINGCHANGE && (long)w == 0x2F /*SPI_SETWORKAREA*/)) DisplayChanged?.Invoke();
        else if (msg == WM_DPICHANGED) DisplayChanged?.Invoke();
        return DefWindowProc(h, msg, w, l);
    }

    public void Dispose()
    {
        DestroyWindow(_hwnd);
        s_instance = null;
    }
}
