using Microsoft.Win32;

namespace Darkroom.App;

/// <summary>Windows 시작 시 실행: HKCU\...\Run 에 "Darkroom" 값을 쓴다(관리자 권한 불필요, 이 사용자만).</summary>
internal static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Darkroom";

    static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    /// <summary>등록돼 있고, 지금 실행 중인 exe를 가리키는지</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && v.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>exe를 옮긴 뒤 켜져 있던 등록이 옛 경로를 가리키면 새 경로로 고친다.</summary>
    public static void RepairIfMoved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is string v && !v.Equals(Command, StringComparison.OrdinalIgnoreCase)
            && v.Contains("Darkroom", StringComparison.OrdinalIgnoreCase))
        {
            var oldExe = v.Trim().Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (oldExe != null && !File.Exists(oldExe)) Set(true);
        }
    }
}
