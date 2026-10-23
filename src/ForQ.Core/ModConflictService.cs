using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForQ.Core;

public sealed class ModConflictReport
{
    public int ModCount { get; init; }
    public IReadOnlyList<ModConflict> Conflicts { get; init; } = Array.Empty<ModConflict>();
    public bool HasConflicts => Conflicts.Count > 0;
}

public sealed class ModConflict
{
    public string Title { get; init; } = "";
    public string Details { get; init; } = "";
}

public sealed class ModDependent
{
    public string Title { get; init; } = "";
    public string FileName { get; init; } = "";
    public string DependencyId { get; init; } = "";
    public string RequiredRange { get; init; } = "";
    public bool Enabled { get; init; }
    public string Status => Enabled ? "включён" : "отключён";
    public string Dependency => string.IsNullOrWhiteSpace(RequiredRange)
        ? $"требует {DependencyId}"
        : $"требует {DependencyId} {RequiredRange}";
}

public sealed class ModConflictService
{
    private static readonly Regex ModsBlock = new(
        @"(?ms)^[ \t]*\[\[mods\]\][ \t]*\r?\n(?<body>.*?)(?=^[ \t]*\[\[|\z)",
        RegexOptions.Compiled);

    private static readonly Regex DependencyBlock = new(
        @"(?ms)^[ \t]*\[\[dependencies\.(?<owner>[^\]\r\n]+)\]\][ \t]*\r?\n(?<body>.*?)(?=^[ \t]*\[\[|\z)",
        RegexOptions.Compiled);

    private readonly ContentService _content;

    public ModConflictService(ContentService content) => _content = content;

    public ModConflictReport Check(Instance instance, CancellationToken ct = default)
    {
        var installed = _content.ListInstalled(instance)
            .Where(item => item.Kind == ContentKind.Mod && item.Enabled)
            .Where(item => item.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = new List<ModFile>();
        foreach (var content in installed)
        {
            ct.ThrowIfCancellationRequested();
            files.Add(new ModFile
            {
                FileName = content.FileName,
                ProjectId = content.ProjectId,
                FallbackName = string.IsNullOrWhiteSpace(content.Title) ? content.FileName : content.Title,
                Identities = ReadIdentities(content.Path)
            });
        }

        var conflicts = new List<ModConflict>();
        var reportedFileSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var byId = files
            .SelectMany(file => file.Identities.Select(identity => new ModEntry(file, identity)))
            .GroupBy(entry => entry.Identity.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in byId)
        {
            var entries = group.Value;
            var duplicateFiles = entries
                .Select(entry => entry.File)
                .DistinctBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (duplicateFiles.Count < 2 || !reportedFileSets.Add(FileSetKey(duplicateFiles)))
                continue;

            var name = entries
                .Select(entry => string.IsNullOrWhiteSpace(entry.Identity.Name) ? entry.File.FallbackName : entry.Identity.Name)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? group.Key;

            conflicts.Add(new ModConflict
            {
                Title = $"Дубликат мода «{name}»",
                Details = $"ID {group.Key} заявлен в нескольких включённых файлах: "
                          + string.Join(", ", duplicateFiles.Select(file => file.FileName)) + "."
            });
        }

        foreach (var group in files
                     .Where(file => !string.IsNullOrWhiteSpace(file.ProjectId))
                     .GroupBy(file => file.ProjectId, StringComparer.OrdinalIgnoreCase))
        {
            var duplicateFiles = group
                .DistinctBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (duplicateFiles.Count < 2 || !reportedFileSets.Add(FileSetKey(duplicateFiles)))
                continue;

            conflicts.Add(new ModConflict
            {
                Title = $"Дубликат проекта «{duplicateFiles[0].FallbackName}»",
                Details = "Один проект установлен несколько раз: "
                          + string.Join(", ", duplicateFiles.Select(file => file.FileName)) + "."
            });
        }

        var seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ownerGroup in byId)
        {
            ct.ThrowIfCancellationRequested();

            var ownerEntry = ownerGroup.Value[0];
            var ownerName = ownerGroup.Value
                .Select(entry => string.IsNullOrWhiteSpace(entry.Identity.Name) ? entry.File.FallbackName : entry.Identity.Name)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? ownerGroup.Key;

            foreach (var declaredGroup in ownerGroup.Value
                         .SelectMany(entry => entry.Identity.Breaks)
                         .GroupBy(declared => declared.ModId, StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(declaredGroup.Key, ownerGroup.Key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!byId.TryGetValue(declaredGroup.Key, out var targets))
                    continue;

                var ranges = declaredGroup
                    .Select(declared => declared.Range)
                    .Where(range => !string.IsNullOrWhiteSpace(range) && range != "*")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var matchingTargets = targets
                    .Where(entry => ranges.Count == 0
                                    || ranges.Any(range => VersionRange.Matches(entry.Identity.Version, range)))
                    .ToList();

                if (matchingTargets.Count == 0)
                    continue;

                var targetFiles = matchingTargets
                    .Select(entry => entry.File)
                    .DistinctBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (targetFiles.Count == 0 || !seenPairs.Add(ownerGroup.Key + "|" + declaredGroup.Key))
                    continue;

                var targetName = matchingTargets
                    .Select(entry => string.IsNullOrWhiteSpace(entry.Identity.Name) ? entry.File.FallbackName : entry.Identity.Name)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? declaredGroup.Key;
                var rangeText = ranges.Count == 0
                    ? ""
                    : $" (заявленный диапазон: {string.Join(", ", ranges)})";

                conflicts.Add(new ModConflict
                {
                    Title = "Несовместимые моды",
                    Details = $"«{ownerName}» ({ownerEntry.File.FileName}) несовместим с "
                              + $"«{targetName}» ({string.Join(", ", targetFiles.Select(file => file.FileName))}){rangeText}."
                });
            }
        }

        return new ModConflictReport { ModCount = files.Count, Conflicts = conflicts };
    }

    public IReadOnlyList<ModDependent> FindDependents(Instance instance, InstalledContent target)
    {
        var targetIdentities = ReadIdentities(target.Path);
        if (targetIdentities.Count == 0)
            return Array.Empty<ModDependent>();

        var targetVersions = targetIdentities
            .Where(identity => !string.IsNullOrWhiteSpace(identity.Id))
            .GroupBy(identity => identity.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Version, StringComparer.OrdinalIgnoreCase);

        var result = new List<ModDependent>();
        var installed = _content.ListInstalled(instance)
            .Where(item => item.Kind == ContentKind.Mod)
            .Where(item => !string.Equals(item.FileName, target.FileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var candidate in installed)
        {
            foreach (var identity in ReadIdentities(candidate.Path))
            {
                foreach (var dependency in identity.Depends)
                {
                    if (IsRuntimeDependency(dependency.ModId))
                        continue;

                    if (!targetVersions.TryGetValue(dependency.ModId, out var targetVersion))
                        continue;

                    if (!string.IsNullOrWhiteSpace(dependency.Range)
                        && dependency.Range != "*"
                        && !string.IsNullOrWhiteSpace(targetVersion)
                        && !VersionRange.Matches(targetVersion, dependency.Range))
                        continue;

                    if (result.Any(item => string.Equals(item.FileName, candidate.FileName, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(item.DependencyId, dependency.ModId, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    result.Add(new ModDependent
                    {
                        Title = string.IsNullOrWhiteSpace(identity.Name) ? candidate.DisplayName : identity.Name,
                        FileName = candidate.FileName,
                        DependencyId = dependency.ModId,
                        RequiredRange = dependency.Range,
                        Enabled = candidate.Enabled
                    });
                }
            }
        }

        return result
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FileSetKey(IEnumerable<ModFile> files)
        => string.Join("|", files
            .Select(file => file.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

    private static List<ModIdentity> ReadIdentities(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);

            var quilt = ReadQuilt(archive);
            if (quilt is not null)
                return new List<ModIdentity> { quilt };

            var fabric = ReadFabric(archive);
            if (fabric is not null)
                return new List<ModIdentity> { fabric };

            var neoForge = ReadForge(archive, "META-INF/neoforge.mods.toml");
            if (neoForge.Count > 0)
                return neoForge;

            var forge = ReadForge(archive, "META-INF/mods.toml");
            if (forge.Count > 0)
                return forge;

            return ReadMcModInfo(archive);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or JsonException or NotSupportedException)
        {
            return new List<ModIdentity>();
        }
    }

    private static ModIdentity? ReadFabric(ZipArchive archive)
    {
        var entry = FindEntry(archive, "fabric.mod.json");
        if (entry is null)
            return null;

        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var id = GetString(root, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var identity = new ModIdentity
        {
            Id = id,
            Name = GetString(root, "name"),
            Version = GetString(root, "version")
        };

        identity.Breaks.AddRange(ReadObjectBreaks(root, "breaks"));
        identity.Depends.AddRange(ReadObjectDependencies(root, "depends"));
        return identity;
    }

    private static ModIdentity? ReadQuilt(ZipArchive archive)
    {
        var entry = FindEntry(archive, "quilt.mod.json");
        if (entry is null)
            return null;

        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("quilt_loader", out var loader) || loader.ValueKind != JsonValueKind.Object)
            return null;

        var id = GetString(loader, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var name = loader.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            ? GetString(metadata, "name")
            : "";

        var identity = new ModIdentity
        {
            Id = id,
            Name = name,
            Version = GetString(loader, "version")
        };

        if (loader.TryGetProperty("breaks", out var breaks) && breaks.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in breaks.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var target = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(target))
                    continue;

                identity.Breaks.Add(new DeclaredBreak(target, ReadQuiltRange(item)));
            }
        }

        if (loader.TryGetProperty("depends", out var depends))
            identity.Depends.AddRange(ReadQuiltDependencies(depends));

        return identity;
    }

    private static IEnumerable<DeclaredDependency> ReadObjectDependencies(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var item in dependencies.EnumerateObject())
        {
            if (!string.IsNullOrWhiteSpace(item.Name))
                yield return new DeclaredDependency(item.Name, ReadQuiltRange(item.Value));
        }
    }

    private static IEnumerable<DeclaredDependency> ReadQuiltDependencies(JsonElement dependencies)
    {
        if (dependencies.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in dependencies.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(item.Name))
                    yield return new DeclaredDependency(item.Name, ReadQuiltRange(item.Value));
            }
            yield break;
        }

        if (dependencies.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in dependencies.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = GetString(item, "id");
            if (!string.IsNullOrWhiteSpace(id))
                yield return new DeclaredDependency(id, ReadQuiltRange(item));
        }
    }

    private static string ReadQuiltRange(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Array => string.Join(" || ", element
                .EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            JsonValueKind.Object => GetString(element, "versions") is { Length: > 0 } versions
                ? versions
                : GetString(element, "version"),
            _ => ""
        };
    }

    private static IEnumerable<DeclaredBreak> ReadObjectBreaks(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var breaks) || breaks.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var item in breaks.EnumerateObject())
        {
            var range = item.Value.ValueKind switch
            {
                JsonValueKind.String => item.Value.GetString() ?? "",
                JsonValueKind.Array => string.Join(" || ", item.Value
                    .EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
                _ => ""
            };

            if (!string.IsNullOrWhiteSpace(item.Name))
                yield return new DeclaredBreak(item.Name, range);
        }
    }

    private static List<ModIdentity> ReadForge(ZipArchive archive, string entryName)
    {
        var entry = FindEntry(archive, entryName);
        if (entry is null)
            return new List<ModIdentity>();

        string text;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true))
            text = reader.ReadToEnd();

        text = text.TrimStart('\uFEFF');
        var identities = new List<ModIdentity>();

        foreach (Match match in ModsBlock.Matches(text))
        {
            var body = match.Groups["body"].Value;
            var id = TomlValue(body, "modId");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            identities.Add(new ModIdentity
            {
                Id = id,
                Name = TomlValue(body, "displayName"),
                Version = TomlValue(body, "version")
            });
        }

        if (identities.Count == 0)
            return identities;

        var breaks = new Dictionary<string, List<DeclaredBreak>>(StringComparer.OrdinalIgnoreCase);
        var depends = new Dictionary<string, List<DeclaredDependency>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in DependencyBlock.Matches(text))
        {
            var owner = match.Groups["owner"].Value.Trim();
            var body = match.Groups["body"].Value;
            var target = TomlValue(body, "modId");
            var type = TomlValue(body, "type");

            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(target))
                continue;

            var range = TomlValue(body, "versionRange");
            if (string.Equals(type, "incompatible", StringComparison.OrdinalIgnoreCase))
            {
                if (!breaks.TryGetValue(owner, out var list))
                {
                    list = new List<DeclaredBreak>();
                    breaks[owner] = list;
                }

                list.Add(new DeclaredBreak(target, range));
            }
            else if (string.Equals(type, "required", StringComparison.OrdinalIgnoreCase)
                     && !IsRuntimeDependency(target))
            {
                if (!depends.TryGetValue(owner, out var list))
                {
                    list = new List<DeclaredDependency>();
                    depends[owner] = list;
                }

                list.Add(new DeclaredDependency(target, range));
            }
        }

        foreach (var identity in identities)
        {
            if (breaks.TryGetValue(identity.Id, out var breakList))
                identity.Breaks.AddRange(breakList);
            if (depends.TryGetValue(identity.Id, out var dependencyList))
                identity.Depends.AddRange(dependencyList);
        }

        return identities;
    }

    private static bool IsRuntimeDependency(string id)
        => id.Equals("minecraft", StringComparison.OrdinalIgnoreCase)
           || id.Equals("forge", StringComparison.OrdinalIgnoreCase)
           || id.Equals("neoforge", StringComparison.OrdinalIgnoreCase)
           || id.Equals("fabricloader", StringComparison.OrdinalIgnoreCase)
           || id.Equals("quilt_loader", StringComparison.OrdinalIgnoreCase)
           || id.Equals("quilted_loader", StringComparison.OrdinalIgnoreCase)
           || id.Equals("java", StringComparison.OrdinalIgnoreCase);

    private static List<ModIdentity> ReadMcModInfo(ZipArchive archive)
    {
        var entry = FindEntry(archive, "mcmod.info");
        if (entry is null)
            return new List<ModIdentity>();

        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var items = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object
              && root.TryGetProperty("modList", out var list)
              && list.ValueKind == JsonValueKind.Array
                ? list
                : default;

        if (items.ValueKind != JsonValueKind.Array)
            return new List<ModIdentity>();

        var identities = new List<ModIdentity>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = GetString(item, "modid");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            identities.Add(new ModIdentity
            {
                Id = id,
                Name = GetString(item, "name"),
                Version = GetString(item, "version")
            });
        }

        return identities;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
    {
        var direct = archive.GetEntry(path);
        if (direct is not null)
            return direct;

        return archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));
    }

    private static string TomlValue(string body, string key)
    {
        var match = Regex.Match(
            body,
            $@"(?m)^[ \t]*{Regex.Escape(key)}[ \t]*=[ \t]*(?<quote>[""'])(?<value>.*?)\k<quote>[ \t]*(?:#.*)?\r?$");
        return match.Success ? match.Groups["value"].Value : "";
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private sealed record DeclaredBreak(string ModId, string Range);

    private sealed record DeclaredDependency(string ModId, string Range);

    private sealed record ModEntry(ModFile File, ModIdentity Identity);

    private sealed class ModFile
    {
        public string FileName { get; init; } = "";
        public string ProjectId { get; init; } = "";
        public string FallbackName { get; init; } = "";
        public List<ModIdentity> Identities { get; init; } = new();
    }

    private sealed class ModIdentity
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public List<DeclaredBreak> Breaks { get; } = new();
        public List<DeclaredDependency> Depends { get; } = new();
    }
}
