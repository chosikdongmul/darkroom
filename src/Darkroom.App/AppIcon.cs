using System.Drawing;

namespace Darkroom.App;

/// <summary>앱에 넣어 둔 마크 아이콘(build\darkroom.ico: 헌팅캡·콧수염·턱수염 아저씨).</summary>
internal static class AppIcon
{
    static Stream Open() => typeof(AppIcon).Assembly.GetManifestResourceStream("darkroom.ico")
        ?? throw new InvalidOperationException("darkroom.ico 리소스 없음");

    /// <summary>원하는 크기에 가장 가까운 그림으로(트레이는 작은 아이콘 크기).</summary>
    public static Icon Load(Size size)
    {
        using var s = Open();
        return new Icon(s, size);
    }

    /// <summary>WPF 창 아이콘(제목 표시줄·작업 표시줄).</summary>
    public static System.Windows.Media.ImageSource LoadWpf()
    {
        using var s = Open();
        var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        return System.Windows.Media.Imaging.BitmapFrame.Create(ms, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
    }
}
