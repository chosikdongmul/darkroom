using System.Buffers.Binary;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Darkroom.Providers;

/// <summary>사진 폴더 훑기·이미지 크기 읽기. 배경화면 바꾸기에서 쓴다.</summary>
public static class PhotoLibrary
{
    /// <summary>Windows 배경화면으로 확실히 쓸 수 있는 형식</summary>
    public static readonly HashSet<string> WallpaperExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };

    /// <summary>폴더(하위 포함)의 파일 중 확장자가 맞는 것. 숨김·시스템 파일과 접근 불가 폴더는 건너뛴다.</summary>
    public static List<string> Enumerate(string folder, ISet<string> extensions, int max = 20_000)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System,
        };
        var list = new List<string>();
        if (!Directory.Exists(folder)) return list;
        foreach (var f in Directory.EnumerateFiles(folder, "*", opts))
        {
            if (!extensions.Contains(Path.GetExtension(f))) continue;
            list.Add(f);
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>이미지를 긴 변 256px로 디코딩해 히스토그램(Histogram.Compute 형태)을 만든다. 형식은 내용으로 판별(확장자 없어도 됨).</summary>
    public static async Task<object> HistogramAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        double scale = Math.Min(1.0, 256.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Linear,
        };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return Histogram.Compute(data.DetachPixelData());
    }

    /// <summary>JPEG·PNG·BMP의 가로·세로(픽셀). 파일 앞부분만 읽는다. 모르면 null.</summary>
    public static (int width, int height)? TryReadSize(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg")
            {
                var e = ExifReader.ReadFile(path);
                if (e?.Width is int w && e.Height is int h && w > 0 && h > 0)
                {
                    // EXIF 회전값 5~8이면 가로세로가 바뀐다
                    return e.Orientation is >= 5 and <= 8 ? (h, w) : (w, h);
                }
                return null;
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[32];
            if (fs.Read(head) < 26) return null;
            if (ext == ".png" && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
                return ((int)BinaryPrimitives.ReadUInt32BigEndian(head[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(head[20..]));
            if (ext == ".bmp" && head[0] == 'B' && head[1] == 'M')
                return (BinaryPrimitives.ReadInt32LittleEndian(head[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head[22..])));
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
