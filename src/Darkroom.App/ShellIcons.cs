using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Darkroom.App;

/// <summary>파일·폴더·앱·shell: 경로의 Windows 아이콘(48px)을 PNG로. 바로가기 위젯과 Manager가 쓴다.</summary>
internal static class ShellIcons
{
    static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>아이콘 PNG. 웹 주소이거나 찾지 못하면 null.</summary>
    public static byte[]? Png(string target)
    {
        target = Environment.ExpandEnvironmentVariables(target.Trim());
        if (target.Length == 0 || target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(target, out var hit)) return hit;
            var png = Load(target);
            if (Cache.Count > 200) Cache.Clear();
            Cache[target] = png;
            return png;
        }
    }

    static byte[]? Load(string target)
    {
        if (SHParseDisplayName(target, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return null;
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            var info = new SHFILEINFO();
            if (SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_PIDL | SHGFI_SYSICONINDEX) == IntPtr.Zero) return null;
            var iid = typeof(IImageList).GUID;
            if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out var list) != 0) return null;
            if (list.GetIcon(info.iIcon, ILD_TRANSPARENT, out hIcon) != 0 || hIcon == IntPtr.Zero) return null;
            using var icon = Icon.FromHandle(hIcon);
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is COMException or ExternalException or ArgumentException) { return null; }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
            CoTaskMemFree(pidl);
        }
    }

    const uint SHGFI_PIDL = 0x8, SHGFI_SYSICONINDEX = 0x4000;
    const int SHIL_EXTRALARGE = 2, ILD_TRANSPARENT = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attr, ref SHFILEINFO info, uint cb, uint flags);
    [DllImport("shell32")] static extern int SHGetImageList(int list, ref Guid riid, out IImageList ppv);
    [DllImport("user32")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("ole32")] static extern void CoTaskMemFree(IntPtr p);
}
