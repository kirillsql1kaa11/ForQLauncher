using System.Text.Json.Serialization;

namespace ForQ.Core;

public enum LoaderKind
{
    Vanilla,
    Fabric,
    Quilt,
    NeoForge
}

public enum ContentKind
{
    Mod,
    ResourcePack,
    Shader
}

public sealed class Instance
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("minecraftVersion")] public string MinecraftVersion { get; set; } = "";
    [JsonPropertyName("loader")] public LoaderKind Loader { get; set; } = LoaderKind.Vanilla;
    [JsonPropertyName("loaderVersion")] public string LoaderVersion { get; set; } = "";
    [JsonPropertyName("profileId")] public string ProfileId { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "Player";
    [JsonPropertyName("memoryMb")] public int MemoryMb { get; set; } = 4096;
    [JsonPropertyName("width")] public int Width { get; set; } = 1280;
    [JsonPropertyName("height")] public int Height { get; set; } = 720;
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string RootPath { get; set; } = "";
    [JsonIgnore] public string Folder => Path.Combine(RootPath, "instances", Id);
    [JsonIgnore] public string OptionsPath => Path.Combine(Folder, "options.txt");
    [JsonIgnore] public string LoaderName => Loader switch
    {
        LoaderKind.Vanilla => "Ванильный",
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        _ => "NeoForge"
    };
}

public sealed class AppSettings
{
    [JsonPropertyName("username")] public string Username { get; set; } = "Player";
    [JsonPropertyName("memoryMb")] public int MemoryMb { get; set; } = 4096;
    [JsonPropertyName("blockTelemetry")] public bool BlockTelemetry { get; set; } = true;
    [JsonPropertyName("javaPath")] public string? JavaPath { get; set; }
    [JsonPropertyName("downloadThreads")] public int DownloadThreads { get; set; } = 8;
    [JsonPropertyName("lastInstanceId")] public string LastInstanceId { get; set; } = "";
    [JsonPropertyName("rootPath")] public string? RootPath { get; set; }
}

public sealed class LoaderBuild
{
    public string Version { get; init; } = "";
    public bool Stable { get; init; }
    public string Label => Stable ? Version : Version;
}

public sealed class InstalledContent
{
    public string FileName { get; init; } = "";
    public string Path { get; init; } = "";
    public ContentKind Kind { get; init; }
    public bool Enabled { get; init; } = true;
    public long Size { get; init; }

    [JsonIgnore] public string KindName => Kind switch
    {
        ContentKind.Mod => "Мод",
        ContentKind.ResourcePack => "Ресурспак",
        _ => "Шейдер"
    };
}
