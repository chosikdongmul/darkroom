using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Darkroom.Core;

namespace Darkroom.Providers;

/// <summary>
/// system 네임스페이스 (1초 주기)
///   system.cpu   = 전체 CPU 사용률(%)
///   system.ram   = { totalBytes, usedBytes, percent }
///   system.net   = { downBps, upBps }  초당 바이트
///   system.disks = [{ name, label, totalBytes, freeBytes, usedBytes, percent }]  (30초마다)
/// </summary>
public sealed class SystemProvider : IDataProvider
{
    public string Namespace => "system";
    public TimeSpan Interval => TimeSpan.FromSeconds(1);

    ulong _idle, _kernel, _user;
    bool _hasCpu;
    long _rx, _tx, _netAt;
    int _tick;

    public Task UpdateAsync(IDataBus bus, CancellationToken ct)
    {
        PublishCpu(bus);
        PublishRam(bus);
        PublishNet(bus);
        if (_tick++ % 30 == 0) PublishDisks(bus);
        return Task.CompletedTask;
    }

    void PublishCpu(IDataBus bus)
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return;
        ulong idle = idleFt.Value, kernel = kernelFt.Value, user = userFt.Value;
        if (_hasCpu)
        {
            // kernel 시간에는 idle이 포함돼 있다.
            ulong total = (kernel - _kernel) + (user - _user);
            ulong busy = total - (idle - _idle);
            if (total > 0) bus.Publish("system.cpu", Math.Round(100.0 * busy / total, 1));
        }
        (_idle, _kernel, _user, _hasCpu) = (idle, kernel, user, true);
    }

    static void PublishRam(IDataBus bus)
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref ms) || ms.ullTotalPhys == 0) return;
        ulong used = ms.ullTotalPhys - ms.ullAvailPhys;
        bus.Publish("system.ram", new
        {
            totalBytes = ms.ullTotalPhys,
            usedBytes = used,
            percent = Math.Round(100.0 * used / ms.ullTotalPhys, 1),
        });
    }

    void PublishNet(IDataBus bus)
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch (NetworkInformationException) { return; }

        long now = Environment.TickCount64;
        if (_netAt != 0 && now > _netAt)
        {
            double sec = (now - _netAt) / 1000.0;
            bus.Publish("system.net", new
            {
                downBps = Math.Max(0, (long)((rx - _rx) / sec)),
                upBps = Math.Max(0, (long)((tx - _tx) / sec)),
            });
        }
        (_rx, _tx, _netAt) = (rx, tx, now);
    }

    static void PublishDisks(IDataBus bus)
    {
        var disks = new List<object>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize == 0) continue;
                long used = d.TotalSize - d.TotalFreeSpace;
                disks.Add(new
                {
                    name = d.Name.TrimEnd('\\'),
                    label = d.VolumeLabel,
                    totalBytes = d.TotalSize,
                    freeBytes = d.TotalFreeSpace,
                    usedBytes = used,
                    percent = Math.Round(100.0 * used / d.TotalSize, 1),
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        bus.Publish("system.disks", disks);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32", SetLastError = true)] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}

/// <summary>
/// battery 네임스페이스 (10초 주기)
///   battery.status = { hasBattery, percent, charging, pluggedIn, remainingMinutes, saver }
/// </summary>
public sealed class BatteryProvider : IDataProvider
{
    public string Namespace => "battery";
    public TimeSpan Interval => TimeSpan.FromSeconds(10);

    public Task UpdateAsync(IDataBus bus, CancellationToken ct)
    {
        if (!GetSystemPowerStatus(out var s)) return Task.CompletedTask;
        bool hasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255;
        bus.Publish("battery.status", new
        {
            hasBattery,
            percent = hasBattery && s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : (int?)null,
            charging = hasBattery && (s.BatteryFlag & 8) != 0,
            pluggedIn = s.ACLineStatus == 1,
            remainingMinutes = hasBattery && s.BatteryLifeTime >= 0 && s.ACLineStatus != 1 ? s.BatteryLifeTime / 60 : (int?)null,
            saver = s.SystemStatusFlag == 1,
        });
        return Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
}
