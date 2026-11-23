using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkroom.Core;

public static class DrJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>한 줄 JSON (위젯·Manager로 보내는 메시지용).</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"{Path.GetFileName(path)}: 비어 있음");

    /// <summary>임시 파일에 쓰고 교체 → 쓰다가 꺼져도 원본이 깨지지 않는다.</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static JsonElement ToElement(object? value) => JsonSerializer.SerializeToElement(value, Compact);
}

public static class Log
{
    static readonly object s_lock = new();
    static string? s_path;

    public static void Init(string directory)
    {
        Directory.CreateDirectory(directory);
        s_path = Path.Combine(directory, "darkroom.log");
        try
        {
            if (File.Exists(s_path) && new FileInfo(s_path).Length > 1_000_000)
                File.Move(s_path, s_path + ".old", overwrite: true);
        }
        catch (IOException) { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        System.Diagnostics.Debug.Write(line);
        if (s_path == null) return;
        lock (s_lock)
        {
            try { File.AppendAllText(s_path, line); } catch (IOException) { }
        }
    }
}
