using System.Text.Json;

namespace ForQ.Core;

public static class GameOptions
{
    public static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>();
        if (!File.Exists(path))
            return result;

        foreach (var line in File.ReadAllLines(path))
        {
            var index = line.IndexOf(':');
            if (index <= 0)
                continue;
            result[line[..index]] = line[(index + 1)..];
        }

        return result;
    }

    public static void Write(string path, Dictionary<string, string> values)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var lines = values.Select(pair => pair.Key + ":" + pair.Value).ToList();
        File.WriteAllLines(path, lines);
    }

    public static List<string> GetEnabledPacks(string path)
    {
        var values = Read(path);
        if (!values.TryGetValue("packs", out var raw) || string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    public static void SetPackEnabled(string path, string fileName, bool enabled)
    {
        var values = Read(path);
        var packs = GetEnabledPacks(path);
        var entry = "file/" + fileName;

        packs.RemoveAll(p => string.Equals(p, entry, StringComparison.OrdinalIgnoreCase));
        if (enabled)
            packs.Add(entry);

        values["packs"] = JsonSerializer.Serialize(packs);
        Write(path, values);
    }

    public static void SetShader(string path, string? fileName)
    {
        var values = Read(path);
        values["shaderPack"] = fileName is null ? "" : JsonSerializer.Serialize(fileName);
        Write(path, values);
    }

    public static string? GetShader(string path)
    {
        var values = Read(path);
        if (!values.TryGetValue("shaderPack", out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;
        try
        {
            return JsonSerializer.Deserialize<string>(raw);
        }
        catch (JsonException)
        {
            return raw.Trim('"');
        }
    }
}
