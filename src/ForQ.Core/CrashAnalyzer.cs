using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForQ.Core;

public sealed class CrashAnalysis
{
    public bool Found { get; init; }
    public string Source { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Cause { get; init; } = "";
    public string? Suspect { get; init; }
    public string? SuspectFile { get; init; }
    public IReadOnlyList<string> Recommendations { get; init; } = Array.Empty<string>();
    public string StackTrace { get; init; } = "";
    public string Details { get; init; } = "";
}

public sealed class CrashAnalyzer
{
    private static readonly Regex FramePattern = new(
        @"^\s*at\s+(?<class>[\w.$]+)\.(?<method>[\w$<>]+)\((?<source>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex ExceptionPattern = new(
        @"(?m)^(?:Caused by:\s*)?(?<line>(?:[a-zA-Z_$][\w$]*\.)+[A-Za-z_$][\w$]*(?:Exception|Error)(?::.*)?)$",
        RegexOptions.Compiled);

    private static readonly string[] VanillaPrefixes =
    {
        "net.minecraft.",
        "com.mojang.",
        "java.",
        "javax.",
        "jdk.",
        "sun.",
        "org.lwjgl.",
        "org.slf4j.",
        "org.apache.",
        "io.netty.",
        "com.google.",
        "org.objectweb.asm.",
        "org.spongepowered.asm.",
        "net.fabricmc.loader.",
        "org.quiltmc.loader.",
        "cpw.mods.",
        "net.neoforged.",
        "net.minecraftforge."
    };

    private readonly ContentService _content;

    public CrashAnalyzer(ContentService content) => _content = content;

    public CrashAnalysis Analyze(Instance instance, CancellationToken ct)
    {
        var latestLog = Path.Combine(instance.Folder, "logs", "latest.log");
        var crashReport = FindLatestCrashReport(instance);
        var latestText = File.Exists(latestLog) ? ReadTail(latestLog) : "";

        string? source = null;
        var text = "";
        if (latestText.Length > 0 && LooksLikeCrash(latestText))
        {
            source = latestLog;
            text = latestText;
        }
        else if (crashReport is not null)
        {
            source = crashReport;
            text = ReadTail(crashReport);
        }
        else if (latestText.Length > 0)
        {
            source = latestLog;
            text = latestText;
        }

        if (source is null)
        {
            return new CrashAnalysis
            {
                Found = false,
                Summary = "Логи не найдены",
                Cause = "В профиле нет папки logs или файлов crash-reports.",
                Recommendations = new[] { "Запустите игру хотя бы один раз, чтобы лаунчер создал логи." }
            };
        }

        ct.ThrowIfCancellationRequested();
        var found = LooksLikeCrash(text) || crashReport is not null || HasError(text);
        var frames = FramePattern.Matches(text)
            .Cast<Match>()
            .Select(match => new Frame(
                match.Groups["class"].Value,
                match.Groups["method"].Value,
                match.Groups["source"].Value))
            .ToList();

        var mods = BuildModIndex(instance, ct);
        var suspect = FindSuspect(text, frames, mods);
        var exceptionLine = ExtractException(text);
        var summary = ExtractSummary(text, exceptionLine);
        var cause = Explain(text, suspect?.Name);
        var recommendations = BuildRecommendations(text, suspect?.Name);

        return new CrashAnalysis
        {
            Found = found,
            Source = Path.GetFileName(source),
            Summary = summary,
            Cause = cause,
            Suspect = suspect?.Name,
            SuspectFile = suspect?.FileName,
            Recommendations = recommendations,
            StackTrace = ExtractStackTrace(text),
            Details = ExtractDetails(text)
        };
    }

    private static string? FindLatestCrashReport(Instance instance)
    {
        var directory = Path.Combine(instance.Folder, "crash-reports");
        if (!Directory.Exists(directory))
            return null;

        return Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string ReadTail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int limit = 2_000_000;
            if (stream.Length > limit)
                stream.Seek(-limit, SeekOrigin.End);

            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static bool LooksLikeCrash(string text)
        => text.Contains("Minecraft Crash Report", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Crash report", StringComparison.OrdinalIgnoreCase)
           || text.Contains("A fatal error has been detected", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Exception in thread", StringComparison.OrdinalIgnoreCase);

    private static bool HasError(string text)
        => ExceptionPattern.IsMatch(text)
           || text.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
           || text.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

    private static string ExtractException(string text)
    {
        var match = ExceptionPattern.Match(text);
        return match.Success ? match.Groups["line"].Value.Trim() : "";
    }

    private static string ExtractSummary(string text, string exceptionLine)
    {
        var lines = text.Replace("\r", "").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
            {
                var next = index + 1 < lines.Length ? lines[index + 1].Trim() : "";
                return string.IsNullOrWhiteSpace(next) ? line["Description:".Length..].Trim() : next;
            }

            if (line.StartsWith("Failure message:", StringComparison.OrdinalIgnoreCase))
                return line["Failure message:".Length..].Trim();
        }

        if (!string.IsNullOrWhiteSpace(exceptionLine))
            return exceptionLine;

        return "В логе есть ошибка, но явный стек вызовов не найден.";
    }

    private static string Explain(string text, string? suspect)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("outofmemoryerror") || lower.Contains("java heap space"))
            return "Игре не хватило памяти. Скорее всего, выделено слишком мало ОЗУ для текущего количества модов.";

        if (lower.Contains("unsupportedclassversionerror"))
            return "Версия Java слишком старая для этой сборки или мода.";

        if (lower.Contains("modresolutionexception")
            || lower.Contains("missing or unsupported mandatory dependencies")
            || lower.Contains("missing dependency"))
            return "Не установлена обязательная зависимость мода или её версия не подходит.";

        if (lower.Contains("mixinincompatibilityerror")
            || lower.Contains("mixinapplyerror")
            || lower.Contains("mixin apply failed"))
            return "Конфликт миксинов: один мод вмешивается в код другого или в базовую игру.";

        if (lower.Contains("duplicate mods") || lower.Contains("duplicatemod"))
            return "В профиле несколько копий одного и того же мода.";

        if (lower.Contains("unsupportedclassversion")
            || lower.Contains("nosuchmethoderror")
            || lower.Contains("nosuchfielderror")
            || lower.Contains("incompatibleclasschangeerror"))
            return "Мод собран под другую версию Minecraft, загрузчика или Java.";

        if (lower.Contains("classnotfoundexception") || lower.Contains("noclassdeffounderror"))
            return "Не хватает библиотеки или зависимости: мод ссылается на отсутствующий класс.";

        if (lower.Contains("glfw error") || lower.Contains("opengl") || lower.Contains("pixel format"))
            return "Похоже на проблему с графикой или драйвером видеокарты.";

        if (lower.Contains("accessdeniedexception") || lower.Contains("filesystemexception"))
            return "Лаунчер или игра не смогли получить доступ к файлу. Возможно, файл занят другой программой.";

        if (!string.IsNullOrWhiteSpace(suspect))
            return $"Ошибка произошла в моде «{suspect}». Он первым появился в стеке вызовов среди установленных модов.";

        return "Точную причину определить не удалось. В стеке вызовов нет однозначной ссылки на установленный мод.";
    }

    private static IReadOnlyList<string> BuildRecommendations(string text, string? suspect)
    {
        var lower = text.ToLowerInvariant();
        var result = new List<string>();

        if (!string.IsNullOrWhiteSpace(suspect))
        {
            result.Add($"Отключите мод «{suspect}» и попробуйте запустить игру снова.");
            result.Add("Проверьте, что версия мода совпадает с версией Minecraft и загрузчиком профиля.");
        }

        if (lower.Contains("outofmemoryerror") || lower.Contains("java heap space"))
            result.Add("Увеличьте память профиля в настройках лаунчера или уменьшите количество модов.");

        if (lower.Contains("modresolutionexception")
            || lower.Contains("missing or unsupported mandatory dependencies")
            || lower.Contains("missing dependency"))
            result.Add("Установите обязательные зависимости и проверьте их версии.");

        if (lower.Contains("unsupportedclassversionerror"))
            result.Add("Выберите подходящую версию Java для этой версии Minecraft.");

        if (lower.Contains("mixinapplyerror") || lower.Contains("mixin apply failed"))
            result.Add("Проверьте конфликты модов и уберите один из модов, которые меняют одно и то же.");

        if (lower.Contains("glfw error") || lower.Contains("opengl"))
            result.Add("Обновите драйвер видеокарты и попробуйте запустить игру без шейдеров.");

        if (result.Count == 0)
        {
            result.Add("Отключите последние добавленные моды и запустите игру.");
            result.Add("Проверьте папку crash-reports и полный latest.log.");
        }

        result.Add("После изменения профиля снова нажмите «Разбор краша».");
        return result;
    }

    private static string ExtractStackTrace(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var start = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.TrimStart().StartsWith("at ", StringComparison.Ordinal)
                || ExceptionPattern.IsMatch(line))
            {
                start = index;
                break;
            }
        }

        if (start < 0)
            return "Стек вызовов не найден.";

        var result = lines.Skip(start).Take(80).ToList();
        return string.Join("\n", result).Trim();
    }

    private static string ExtractDetails(string text)
    {
        var normalized = text.Replace("\r", "");
        var lines = normalized.Split('\n');
        var start = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Contains("Description:", StringComparison.OrdinalIgnoreCase)
                || lines[index].Contains("Minecraft Crash Report", StringComparison.OrdinalIgnoreCase))
            {
                start = index;
                break;
            }
        }

        var result = lines.Skip(start).Take(120).ToList();
        return string.Join("\n", result).Trim();
    }

    private static ModInfo? FindSuspect(string text, IReadOnlyList<Frame> frames, IReadOnlyList<ModInfo> mods)
    {
        var classIndex = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            foreach (var className in mod.ClassNames)
                classIndex.TryAdd(className, mod);
        }

        foreach (var frame in frames)
        {
            if (IsVanilla(frame.ClassName))
                continue;

            if (classIndex.TryGetValue(frame.ClassName, out var byClass))
                return byClass;
        }

        foreach (var frame in frames)
        {
            if (IsVanilla(frame.ClassName))
                continue;

            var normalized = frame.ClassName.Replace('_', '-');
            foreach (var mod in mods)
            {
                if (string.IsNullOrWhiteSpace(mod.Id))
                    continue;
                var id = mod.Id.Replace('_', '-');
                if (normalized.Contains("." + id + ".", StringComparison.OrdinalIgnoreCase))
                    return mod;
            }
        }

        foreach (var mod in mods)
        {
            if (string.IsNullOrWhiteSpace(mod.Id))
                continue;

            var mixinPattern = $@"(?i)\b{Regex.Escape(mod.Id)}\b.{{0,80}}(?:mixin|failed|error|exception)";
            if (Regex.IsMatch(text, mixinPattern, RegexOptions.IgnoreCase))
                return mod;
        }

        return null;
    }

    private static bool IsVanilla(string className)
        => VanillaPrefixes.Any(prefix => className.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private List<ModInfo> BuildModIndex(Instance instance, CancellationToken ct)
    {
        var result = new List<ModInfo>();
        var installed = _content.ListInstalled(instance)
            .Where(item => item.Kind == ContentKind.Mod)
            .ToList();

        foreach (var content in installed)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(content.Path))
                continue;

            var info = new ModInfo
            {
                Name = content.DisplayName,
                Id = content.ProjectId,
                FileName = content.FileName
            };

            try
            {
                using var archive = ZipFile.OpenRead(content.Path);
                var metadata = ReadMetadata(archive);
                if (!string.IsNullOrWhiteSpace(metadata.Name))
                    info.Name = metadata.Name;
                if (!string.IsNullOrWhiteSpace(metadata.Id))
                    info.Id = metadata.Id;

                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.EndsWith(".class", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (entry.FullName.Contains("module-info", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var className = entry.FullName[..^".class".Length].Replace('/', '.');
                    if (!IsVanilla(className))
                        info.ClassNames.Add(className);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                continue;
            }

            result.Add(info);
        }

        return result;
    }

    private static (string Id, string Name) ReadMetadata(ZipArchive archive)
    {
        var fabric = FindEntry(archive, "fabric.mod.json");
        if (fabric is not null)
        {
            using var document = JsonDocument.Parse(ReadText(fabric));
            var root = document.RootElement;
            return (GetString(root, "id"), GetString(root, "name"));
        }

        var quilt = FindEntry(archive, "quilt.mod.json");
        if (quilt is not null)
        {
            using var document = JsonDocument.Parse(ReadText(quilt));
            var root = document.RootElement;
            if (root.TryGetProperty("quilt_loader", out var loader) && loader.ValueKind == JsonValueKind.Object)
            {
                var name = loader.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                    ? GetString(metadata, "name")
                    : "";
                return (GetString(loader, "id"), name);
            }
        }

        var forge = FindEntry(archive, "META-INF/neoforge.mods.toml") ?? FindEntry(archive, "META-INF/mods.toml");
        if (forge is not null)
        {
            var text = ReadText(forge);
            return (
                Regex.Match(text, @"(?m)^\s*modId\s*=\s*[""'](?<value>.*?)[""']").Groups["value"].Value,
                Regex.Match(text, @"(?m)^\s*displayName\s*=\s*[""'](?<value>.*?)[""']").Groups["value"].Value);
        }

        return ("", "");
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
        => archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
        return reader.ReadToEnd().TrimStart('\uFEFF');
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private sealed record Frame(string ClassName, string Method, string Source);

    private sealed class ModInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public HashSet<string> ClassNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
