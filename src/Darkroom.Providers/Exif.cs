using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Darkroom.Providers;

public sealed record ExifInfo
{
    public string? Make { get; init; }
    public string? Model { get; init; }
    public string? Lens { get; init; }
    public double? FocalLength { get; init; }
    public int? FocalLength35 { get; init; }
    public double? FNumber { get; init; }
    public double? ExposureTime { get; init; }
    public int? Iso { get; init; }
    /// <summary>"yyyy-MM-ddTHH:mm:ss" (카메라 현지 시각, 시간대 없음)</summary>
    public string? TakenAt { get; init; }
    public int? Orientation { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }

    /// <summary>"1/250", "0.5s", "2s"</summary>
    public string? Shutter => ExposureTime switch
    {
        null or <= 0 => null,
        < 0.3 => "1/" + Math.Round(1 / ExposureTime.Value).ToString(CultureInfo.InvariantCulture),
        _ => Math.Round(ExposureTime.Value, 1).ToString(CultureInfo.InvariantCulture) + "s",
    };

    /// <summary>"f/2.8"</summary>
    public string? Aperture => FNumber is > 0 ? "f/" + Math.Round(FNumber.Value, 1).ToString(CultureInfo.InvariantCulture) : null;
}

/// <summary>JPEG의 EXIF(카메라·렌즈·노출값)와 크기만 읽는 작은 파서. 외부 라이브러리 없이, 앞부분만 읽는다.</summary>
public static class ExifReader
{
    public const int MaxBytes = 512 * 1024;

    public static ExifInfo? ReadFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            var buf = new byte[Math.Min(fs.Length, MaxBytes)];
            int n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            return Read(buf.AsSpan(0, n));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static ExifInfo? Read(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
        var info = new ExifInfo();
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) return info;
            byte marker = jpeg[pos + 1];
            if (marker == 0xFF) { pos++; continue; }               // 채움 바이트
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { pos += 2; continue; }
            if (marker is 0xDA or 0xD9) break;                    // 영상 데이터 시작 → 끝
            int len = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(pos + 2)..]);
            if (len < 2 || pos + 2 + len > jpeg.Length) break;
            var seg = jpeg.Slice(pos + 4, len - 2);

            if (marker == 0xE1 && seg.Length > 6 && seg[..6].SequenceEqual("Exif\0\0"u8))
                info = ParseTiff(seg[6..], info);
            else if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && seg.Length >= 5)
                info = info with
                {
                    Height = info.Height ?? BinaryPrimitives.ReadUInt16BigEndian(seg[1..]),
                    Width = info.Width ?? BinaryPrimitives.ReadUInt16BigEndian(seg[3..]),
                };
            pos += 2 + len;
        }
        return info;
    }

    static ExifInfo ParseTiff(ReadOnlySpan<byte> t, ExifInfo info)
    {
        if (t.Length < 8) return info;
        bool le = t[0] == 'I' && t[1] == 'I';
        if (!le && !(t[0] == 'M' && t[1] == 'M')) return info;
        var r = new TiffReader(t.ToArray(), le);
        if (r.U16(2) != 42) return info;

        uint ifd0 = r.U32(4);
        uint exifIfd = 0;
        foreach (var e in r.Entries(ifd0))
        {
            switch (e.Tag)
            {
                case 0x010F: info = info with { Make = r.Ascii(e) }; break;
                case 0x0110: info = info with { Model = r.Ascii(e) }; break;
                case 0x0112: info = info with { Orientation = (int?)r.Int(e) }; break;
                case 0x8769: exifIfd = (uint)(r.Int(e) ?? 0); break;
            }
        }
        if (exifIfd == 0) return info;
        foreach (var e in r.Entries(exifIfd))
        {
            switch (e.Tag)
            {
                case 0x829A: info = info with { ExposureTime = r.Rational(e) }; break;
                case 0x829D: info = info with { FNumber = r.Rational(e) }; break;
                case 0x8827: info = info with { Iso = (int?)r.Int(e) }; break;
                case 0x9003:
                    var dt = r.Ascii(e);
                    if (dt != null && DateTime.TryParseExact(dt, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken))
                        info = info with { TakenAt = taken.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) };
                    break;
                case 0x920A: info = info with { FocalLength = r.Rational(e) }; break;
                case 0xA405: info = info with { FocalLength35 = (int?)r.Int(e) }; break;
                case 0xA434: info = info with { Lens = r.Ascii(e) }; break;
                case 0xA002: info = info with { Width = (int?)r.Int(e) ?? info.Width }; break;
                case 0xA003: info = info with { Height = (int?)r.Int(e) ?? info.Height }; break;
            }
        }
        return info;
    }

    readonly record struct Entry(ushort Tag, ushort Type, uint Count, int ValuePos);

    sealed class TiffReader(byte[] d, bool le)
    {
        public ushort U16(long o) => o + 2 <= d.Length ? (le ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)o)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)o))) : (ushort)0;
        public uint U32(long o) => o + 4 <= d.Length ? (le ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)o)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)o))) : 0;

        static int TypeSize(ushort type) => type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 => 8, _ => 0 };

        public IEnumerable<Entry> Entries(uint ifd)
        {
            if (ifd + 2 > d.Length) yield break;
            int count = U16(ifd);
            if (count > 512) yield break;
            for (int i = 0; i < count; i++)
            {
                long p = ifd + 2 + i * 12L;
                if (p + 12 > d.Length) yield break;
                ushort tag = U16(p), type = U16(p + 2);
                uint n = U32(p + 4);
                long size = (long)TypeSize(type) * n;
                if (size == 0 || n > 100_000) continue;
                long valuePos = size <= 4 ? p + 8 : U32(p + 8);
                if (valuePos + size > d.Length) continue;
                yield return new Entry(tag, type, n, (int)valuePos);
            }
        }

        public string? Ascii(Entry e)
        {
            if (e.Type != 2) return null;
            var s = Encoding.ASCII.GetString(d, e.ValuePos, (int)e.Count).TrimEnd('\0', ' ');
            return s.Length == 0 ? null : s;
        }

        public long? Int(Entry e) => e.Type switch
        {
            3 => U16(e.ValuePos),
            4 => U32(e.ValuePos),
            9 => (int)U32(e.ValuePos),
            _ => null,
        };

        public double? Rational(Entry e)
        {
            if (e.Type is not (5 or 10)) return null;
            double num = e.Type == 5 ? U32(e.ValuePos) : (int)U32(e.ValuePos);
            double den = e.Type == 5 ? U32(e.ValuePos + 4) : (int)U32(e.ValuePos + 4);
            return den == 0 ? null : num / den;
        }
    }
}
