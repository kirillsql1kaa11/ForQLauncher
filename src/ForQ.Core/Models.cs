using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForQ.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class VersionManifest
{
    [JsonPropertyName("latest")] public LatestVersions Latest { get; set; } = new();
    [JsonPropertyName("versions")] public List<ManifestVersion> Versions { get; set; } = new();
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")] public string Release { get; set; } = "";
    [JsonPropertyName("snapshot")] public string Snapshot { get; set; } = "";
}

public sealed class ManifestVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("releaseTime")] public DateTimeOffset ReleaseTime { get; set; }
}

public sealed class GameVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("mainClass")] public string MainClass { get; set; } = "";
    [JsonPropertyName("assetIndex")] public AssetIndexRef? AssetIndex { get; set; }
    [JsonPropertyName("assets")] public string? Assets { get; set; }
    [JsonPropertyName("javaVersion")] public JavaVersionRef? JavaVersion { get; set; }
    [JsonPropertyName("libraries")] public List<GameLibrary> Libraries { get; set; } = new();
    [JsonPropertyName("downloads")] public GameDownloads Downloads { get; set; } = new();
    [JsonPropertyName("arguments")] public GameArguments? Arguments { get; set; }
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
}

public sealed class JavaVersionRef
{
    [JsonPropertyName("component")] public string Component { get; set; } = "";
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
}

public sealed class GameDownloads
{
    [JsonPropertyName("client")] public DownloadInfo? Client { get; set; }
    [JsonPropertyName("client_mappings")] public DownloadInfo? ClientMappings { get; set; }
    [JsonPropertyName("server")] public DownloadInfo? Server { get; set; }
}

public sealed class DownloadInfo
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
}

public sealed class GameLibrary
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("rules")] public List<GameRule>? Rules { get; set; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }
    [JsonPropertyName("extract")] public ExtractInfo? Extract { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")] public DownloadInfo? Artifact { get; set; }
    [JsonPropertyName("classpath")] public DownloadInfo? Classpath { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, DownloadInfo>? Classifiers { get; set; }
}

public sealed class GameRule
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("os")] public RuleOs? Os { get; set; }
    [JsonPropertyName("features")] public RuleFeatures? Features { get; set; }
}

public sealed class RuleOs
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
}

public sealed class RuleFeatures
{
    [JsonPropertyName("is_demo_user")] public bool? IsDemoUser { get; set; }
    [JsonPropertyName("has_custom_resolution")] public bool? HasCustomResolution { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ExtractInfo
{
    [JsonPropertyName("exclude")] public List<string> Exclude { get; set; } = new();
}

public sealed class GameArguments
{
    [JsonPropertyName("game")] public List<JsonElement>? Game { get; set; }
    [JsonPropertyName("jvm")] public List<JsonElement>? Jvm { get; set; }
}

public sealed class RuntimeProducts
{
    [JsonExtensionData] public Dictionary<string, JsonElement> Platforms { get; set; } = new();
}

public sealed class RuntimeEntry
{
    [JsonPropertyName("manifest")] public RuntimeManifestRef Manifest { get; set; } = new();
    [JsonPropertyName("version")] public RuntimeVersionInfo? Version { get; set; }
}

public sealed class RuntimeVersionInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("released")] public DateTimeOffset Released { get; set; }
}

public sealed class RuntimeManifestRef
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class RuntimeManifest
{
    [JsonPropertyName("files")] public Dictionary<string, RuntimeFile> Files { get; set; } = new();
}

public sealed class RuntimeFile
{
    [JsonPropertyName("type")] public string Type { get; set; } = "file";
    [JsonPropertyName("downloads")] public RuntimeDownloads Downloads { get; set; } = new();
    [JsonPropertyName("executable")] public bool Executable { get; set; }
}

public sealed class RuntimeDownloads
{
    [JsonPropertyName("raw")] public DownloadInfo? Raw { get; set; }
    [JsonPropertyName("lzma")] public DownloadInfo? Lzma { get; set; }
}

public sealed class FabricLoaderEntry
{
    [JsonPropertyName("loader")] public LoaderInfo Loader { get; set; } = new();
    [JsonPropertyName("intermediary")] public LoaderMavenInfo? Intermediary { get; set; }
    [JsonPropertyName("launcherMeta")] public LoaderMeta? LauncherMeta { get; set; }
}

public sealed class QuiltLoaderEntry
{
    [JsonPropertyName("loader")] public LoaderInfo Loader { get; set; } = new();
    [JsonPropertyName("intermediary")] public LoaderMavenInfo? Intermediary { get; set; }
    [JsonPropertyName("launcherMeta")] public LoaderMeta? LauncherMeta { get; set; }
}

public sealed class LoaderInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("stable")] public bool Stable { get; set; }
    [JsonPropertyName("maven")] public string Maven { get; set; } = "";
    [JsonPropertyName("separator")] public string Separator { get; set; } = ".";
    [JsonPropertyName("build")] public int Build { get; set; }
}

public sealed class LoaderMavenInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("maven")] public string Maven { get; set; } = "";
}

public sealed class LoaderMeta
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("min_java_version")] public int MinJavaVersion { get; set; }
    [JsonPropertyName("libraries")] public LoaderMetaLibraries Libraries { get; set; } = new();
    [JsonPropertyName("mainClass")] public JsonElement? MainClass { get; set; }

    public string ClientMainClass(string fallback)
    {
        if (MainClass is not { } element)
            return fallback;

        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? fallback;

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("client", out var client)
            && client.ValueKind == JsonValueKind.String)
            return client.GetString() ?? fallback;

        return fallback;
    }
}

public sealed class LoaderMetaLibraries
{
    [JsonPropertyName("common")] public List<LoaderLibrary> Common { get; set; } = new();
    [JsonPropertyName("client")] public List<LoaderLibrary> Client { get; set; } = new();
    [JsonPropertyName("server")] public List<LoaderLibrary> Server { get; set; } = new();
}

public sealed class LoaderLibrary
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
}

public sealed class LoaderMainClass
{
    [JsonPropertyName("client")] public string Client { get; set; } = "";
    [JsonPropertyName("server")] public string Server { get; set; } = "";
}

public sealed class ModrinthSearchResponse
{
    [JsonPropertyName("hits")] public List<ModrinthProject> Hits { get; set; } = new();
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
}

public sealed class ModrinthProject
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("date_modified")] public DateTimeOffset DateModified { get; set; }
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("author")] public string? Author { get; set; }
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("version_type")] public string VersionType { get; set; } = "";
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = new();
    [JsonPropertyName("files")] public List<ModrinthFile> Files { get; set; } = new();
    [JsonPropertyName("date_published")] public DateTimeOffset DatePublished { get; set; }
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "";
}

public sealed class ModrinthFile
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = new();
}
