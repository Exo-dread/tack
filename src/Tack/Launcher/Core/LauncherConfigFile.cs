using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tack.Launcher.Core;

[JsonSourceGenerationOptions(WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LauncherConfig))]
internal partial class LauncherJsonContext : JsonSerializerContext;

/// <summary>
/// launcher.json, next to tack.json. A separate file on purpose: tack.json (and sync) never see
/// the launcher, and someone who never turns it on never gets the file at all.
/// </summary>
internal sealed class LauncherConfigFile
{
    public const string FileName = "launcher.json";
    private const long MaxBytes = 256 * 1024; // a full launcher is a few KB

    public LauncherConfigFile(string folder) => Path = System.IO.Path.Combine(folder, FileName);

    public string Path { get; }

    /// <summary>The saved config, or the default (off) when there is none or it can't be read.</summary>
    public LauncherConfig Load(Action<string, Exception?>? onError = null)
    {
        foreach (string candidate in new[] { Path, Path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                if (new FileInfo(candidate).Length > MaxBytes) throw new InvalidDataException("launcher.json is too large");
                return Parse(File.ReadAllBytes(candidate)) ?? throw new InvalidDataException("empty launcher.json");
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Could not read {System.IO.Path.GetFileName(candidate)}", ex);
            }
        }
        return LauncherConfig.CreateDefault();
    }

    public static LauncherConfig? Parse(ReadOnlySpan<byte> json)
    {
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) json = json[3..];
        return JsonSerializer.Deserialize(json, LauncherJsonContext.Default.LauncherConfig)?.Normalize();
    }

    public static byte[] Serialize(LauncherConfig config) =>
        JsonSerializer.SerializeToUtf8Bytes(config, LauncherJsonContext.Default.LauncherConfig);

    /// <summary>Atomic write (temp file, then replace, keeping launcher.json.bak).</summary>
    public void Save(LauncherConfig config)
    {
        byte[] bytes = Serialize(config);
        string tmp = Path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(Path)) File.Replace(tmp, Path, Path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, Path);
    }
}
