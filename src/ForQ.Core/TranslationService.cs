using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForQ.Core;

public sealed class TranslationService
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";
    private const int ChunkSize = 900;

    private readonly HttpApi _http;
    private readonly string _cachePath;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;

    public TranslationService(HttpApi http, string cachePath)
    {
        _http = http;
        _cachePath = cachePath;
    }

    public async Task<string> ToRussianAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        if (IsRussian(text))
            return text;

        await EnsureLoadedAsync();

        var chunks = Split(text);
        var result = new StringBuilder();

        foreach (var chunk in chunks)
        {
            var translated = await TranslateChunkAsync(chunk, ct);
            if (result.Length > 0)
                result.Append(chunks.Count > 1 ? "\n" : " ");
            result.Append(translated);
        }

        return result.ToString();
    }

    private async Task<string> TranslateChunkAsync(string chunk, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(chunk, out var cached))
                return cached;

            var url = $"{Endpoint}?client=gtx&sl=auto&tl=ru&dt=t&q={Uri.EscapeDataString(chunk)}";
            var json = await _http.GetStringAsync(url, ct);
            var translated = Parse(json);
            if (string.IsNullOrWhiteSpace(translated))
                return chunk;

            _cache[chunk] = translated;
            await SaveCacheAsync();
            return translated;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return chunk;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return "";

        var builder = new StringBuilder();
        foreach (var part in root[0].EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Array || part.GetArrayLength() == 0)
                continue;
            if (part[0].ValueKind == JsonValueKind.String)
                builder.Append(part[0].GetString());
        }

        return builder.ToString();
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded)
            return;

        _loaded = true;
        if (!File.Exists(_cachePath))
            return;

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_cachePath), Json.Options);
            if (stored is null)
                return;

            foreach (var pair in stored)
                _cache[pair.Key] = pair.Value;
        }
        catch (JsonException)
        {
        }
    }

    private async Task SaveCacheAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (_cache.Count > 4000)
            {
                var keep = _cache.Keys.TakeLast(3000).ToList();
                foreach (var key in _cache.Keys.Except(keep).ToList())
                    _cache.Remove(key);
            }

            await File.WriteAllTextAsync(_cachePath, JsonSerializer.Serialize(_cache, Json.Options));
        }
        catch (IOException)
        {
        }
    }

    private static bool IsRussian(string text)
    {
        var cyrillic = text.Count(ch => ch is >= 'А' and <= 'я' or 'Ё' or 'ё');
        var letters = text.Count(char.IsLetter);
        return letters > 0 && cyrillic * 10 >= letters * 6;
    }

    private static List<string> Split(string text)
    {
        var result = new List<string>();
        var buffer = new StringBuilder();

        foreach (var paragraph in text.Split('\n'))
        {
            if (paragraph.Length > ChunkSize)
            {
                foreach (var sentence in Regex.Split(paragraph, @"(?<=[.!?])\s+"))
                {
                    if (buffer.Length > 0 && buffer.Length + sentence.Length + 1 > ChunkSize)
                    {
                        result.Add(buffer.ToString());
                        buffer.Clear();
                    }

                    if (sentence.Length > ChunkSize)
                    {
                        for (var offset = 0; offset < sentence.Length; offset += ChunkSize)
                        {
                            result.Add(sentence.Substring(offset, Math.Min(ChunkSize, sentence.Length - offset)));
                        }
                        continue;
                    }

                    if (buffer.Length > 0)
                        buffer.Append(' ');
                    buffer.Append(sentence);
                }
            }
            else
            {
                if (buffer.Length > 0 && buffer.Length + paragraph.Length + 1 > ChunkSize)
                {
                    result.Add(buffer.ToString());
                    buffer.Clear();
                }

                if (buffer.Length > 0)
                    buffer.Append('\n');
                buffer.Append(paragraph);
            }
        }

        if (buffer.Length > 0)
            result.Add(buffer.ToString());

        return result.Where(chunk => !string.IsNullOrWhiteSpace(chunk)).ToList();
    }
}

public static class MarkdownText
{
    public static string ToPlain(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        var text = Regex.Replace(markdown, @"<[^>]+>", " ");
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", " ");
        text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"```.*?```", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"`([^`]*)`", "$1");
        text = Regex.Replace(text, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*[-*+]\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*>\s?", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\*{1,3}([^*]+)\*{1,3}", "$1");
        text = Regex.Replace(text, @"_{1,3}([^_]+)_{1,3}", "$1");
        text = Regex.Replace(text, @"\r\n?", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        text = Regex.Replace(text, @"[ \t]{2,}", " ");

        return text.Trim();
    }
}
