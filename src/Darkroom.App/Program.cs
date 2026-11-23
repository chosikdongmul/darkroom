using System.Windows;
using Darkroom.Core;

namespace Darkroom.App;

internal static class Program
{
    const string MutexName = @"Local\Darkroom.SingleInstance";
    const string OpenManagerEvent = @"Local\Darkroom.OpenManager";
    public const string QuitEvent = @"Local\Darkroom.Quit";

    [System.Runtime.InteropServices.DllImport("user32")] static extern bool AllowSetForegroundWindow(int processId);

    [STAThread]
    static int Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (args.Contains("--quit"))
        {
            // 설치·제거 프로그램용: 실행 중인 Darkroom을 정상 종료시키고(상태 저장) 끝날 때까지 최대 15초 기다린다.
            if (first) return 0;
            try { using var ev = EventWaitHandle.OpenExisting(QuitEvent); ev.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            try { if (mutex.WaitOne(TimeSpan.FromSeconds(15))) { mutex.ReleaseMutex(); return 0; } }
            catch (AbandonedMutexException) { return 0; }
            return 1;
        }
        if (!first)
        {
            // 이미 실행 중 → 기존 인스턴스의 Manager를 연다.
            // 방금 사용자가 실행한 이 프로세스만 창을 앞으로 가져올 권한이 있으므로, 그 권한을 기존 인스턴스에 넘긴다.
            AllowSetForegroundWindow(-1 /*ASFW_ANY*/);
            try { using var ev = EventWaitHandle.OpenExisting(OpenManagerEvent); ev.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }
        using var openManager = new EventWaitHandle(false, EventResetMode.AutoReset, OpenManagerEvent);
        var app = new App(args, openManager);
        return app.Run();
    }
}

internal sealed class App : Application
{
    readonly string[] _args;
    readonly EventWaitHandle _openManagerSignal;
    AppController? _controller;
    TrayIcon? _tray;
    ManagerWindow? _manager;
    bool _exiting;

    public App(string[] args, EventWaitHandle openManagerSignal)
    {
        _args = args;
        _openManagerSignal = openManagerSignal;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    string? Arg(string name)
    {
        int i = Array.IndexOf(_args, name);
        return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Init(AppPaths.Logs);
        DispatcherUnhandledException += (_, ev) => { Log.Error("처리되지 않은 예외", ev.Exception); ev.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("치명적 예외", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("관찰되지 않은 작업 예외", ev.Exception); ev.SetObserved(); };
        _ = StartAsync();
    }

    async Task StartAsync()
    {
#if DEBUG
        bool dev = true;
#else
        bool dev = _args.Contains("--dev");
#endif
        // WebView2 런타임(Windows 11에는 기본 포함)이 없으면 설치 안내 후 종료
        try { Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            Log.Error("WebView2 런타임 없음");
            var answer = MessageBox.Show(
                "Darkroom을 실행하려면 Microsoft Edge WebView2 런타임이 필요합니다.\n\n설치 페이지를 열까요? 설치한 뒤 Darkroom을 다시 실행하세요.",
                "Darkroom", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
            Shutdown(1);
            return;
        }

        try
        {
            _controller = new AppController(AppPaths.Resolve(Arg("--root")), dev, Dispatcher);
            await _controller.InitializeAsync();
            AutoStart.RepairIfMoved();
        }
        catch (Exception ex)
        {
            Log.Error("시작 실패", ex);
            MessageBox.Show($"Darkroom을 시작하지 못했습니다.\n\n{ex.Message}\n\n로그: {AppPaths.Logs}", "Darkroom", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        _tray = new TrayIcon(_controller, OpenManager, () => _ = ExitAsync());

        var listener = new Thread(() =>
        {
            while (_openManagerSignal.WaitOne())
            {
                if (_exiting) return;
                Dispatcher.BeginInvoke(OpenManager);
            }
        }) { IsBackground = true, Name = "OpenManagerSignal" };
        listener.Start();

        var quitListener = new Thread(() =>
        {
            using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEvent);
            quit.WaitOne();
            Log.Info("종료 요청(--quit)");
            Dispatcher.BeginInvoke(() => _ = ExitAsync());
        }) { IsBackground = true, Name = "QuitSignal" };
        quitListener.Start();

        // Windows 시작 시 자동 실행(--autostart)일 때는 첫 실행이라도 Manager를 띄우지 않는다
        if (_args.Contains("--manager") || (_controller.FirstRun && !_args.Contains("--autostart"))) OpenManager();
    }

    void OpenManager()
    {
        if (_controller == null || _exiting) return;
        if (_manager == null)
        {
            _manager = new ManagerWindow(_controller);
            _manager.Closed += (_, _) => _manager = null;
            _manager.Show();
        }
        if (_manager.WindowState == WindowState.Minimized) _manager.WindowState = WindowState.Normal;
        _manager.Activate();
    }

    async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _manager?.Close();
        _controller?.Shutdown();
        _tray?.Dispose();
        await Task.Yield();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting) { _exiting = true; _controller?.Shutdown(); _tray?.Dispose(); }
        base.OnExit(e);
    }
}
