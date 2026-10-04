using System.Text.Json;
using System.Text.Json.Serialization;
using Juggler.Core.Rules;

namespace Juggler.Core.Configuration;

/// <summary>
/// Reads and writes the on-disk configuration.
/// <para>
/// Writes are atomic: a temp file in the same directory, flushed, then renamed over the target.
/// A watcher or a second process therefore never observes a half-written config.
/// See docs/architecture.md.
/// </para>
/// </summary>
public sealed class ConfigStore
{
    /// <summary>
    /// Serialize and deserialize against the source-generated <see cref="AppJsonContext"/>.
    /// <para>
    /// The reflection-based overloads are unusable here: they are
    /// <c>RequiresUnreferencedCode</c> and <c>RequiresDynamicCode</c>, and the resident daemon
    /// is published with NativeAOT. Handing the generated context to the serializer is what lets
    /// the trimmer and the AOT compiler see a closed set of types.
    /// </para>
    /// <para>
    /// <c>UseStringEnumConverter</c> on that context is also what keeps enums written as names.
    /// The reflection-based <c>JsonStringEnumConverter</c> did the same job but is itself
    /// <c>RequiresDynamicCode</c>, so it could not be used in an AOT build - which is exactly how
    /// the original bug arose: enums were written as integers because the AOT-safe way to write
    /// them as names was not in use yet.
    /// </para>
    /// </summary>
    private static string Write(AppConfig config) =>
        JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);

    private static AppConfig? ReadFrom(string json) =>
        JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);

    /// <summary>
    /// Per-user config location. Chosen over a roaming/Program Files path so no elevation is needed.
    /// </summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileJuggler",
        "rules.json");

    /// <summary>Where the undo journal lives. Never uploaded; see SECURITY.md.</summary>
    public static string JournalPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileJuggler",
        "journal.jsonl");

    public string Path { get; }

    public ConfigStore(string? path = null) => Path = path ?? DefaultPath;

    /// <summary>
    /// Set when the most recent <see cref="Load"/> failed outright. While this is non-null the
    /// in-memory config is stale (it is still whatever was last successfully read), so writing it
    /// back would destroy rules this process never managed to read.
    /// </summary>
    private string? _loadError;

    /// <summary>True when the last load succeeded, i.e. writing the current config is safe.</summary>
    public bool CanSave() => _loadError is null;

    /// <summary>The last load error, or null.</summary>
    public string? LoadError => _loadError;

    /// <summary>Where the read actually came from. Distinguishing these is a correctness requirement, not diagnostics.</summary>
    public enum LoadSource
    {
        /// <summary>A real file was read and parsed. The only case where the config should be trusted.</summary>
        Parsed,

        /// <summary>No file at the path. Correct on first run; wrong as a transient state during an atomic replace.</summary>
        Missing,

        /// <summary>The file existed but was empty or whitespace. Treat as unreadable, not as "delete everything".</summary>
        Blank,
    }

    public sealed record LoadResult(AppConfig Config, IReadOnlyList<ConfigIssue> Issues, string? Error, LoadSource Source)
    {
        public bool Failed => Error is not null;

        public bool CanApply => Error is null && Issues.All(i => i.Severity != IssueSeverity.Error);

        /// <summary>
        /// True only when a real file was parsed. Callers must not adopt the returned config otherwise.
        /// </summary>
        public bool IsAuthoritative => Source == LoadSource.Parsed && Error is null;
    }

    /// <summary>Loads and validates. A malformed file yields defaults plus an error, never an exception.</summary>
    public LoadResult Load()
    {
        LoadResult result = LoadCore();
        _loadError = result.Error;
        return result;
    }

    private LoadResult LoadCore()
    {
        if (!File.Exists(Path))
        {
            return new LoadResult(new AppConfig(), [], null, LoadSource.Missing);
        }

        string json;
        try
        {
            json = File.ReadAllText(Path);
        }
        catch (Exception ex)
        {
            return new LoadResult(new AppConfig(), [], $"Could not read '{Path}': {ex.Message}", LoadSource.Missing);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return new LoadResult(new AppConfig(), [], null, LoadSource.Blank);
        }

        AppConfig? parsed;
        try
        {
            parsed = ReadFrom(json);
        }
        catch (JsonException ex)
        {
            // Line/byte position is what makes this actionable rather than just annoying.
            return new LoadResult(new AppConfig(), [],
                $"'{Path}' is not valid JSON at line {ex.LineNumber}, byte {ex.BytePositionInLine}: {ex.Message}", LoadSource.Missing);
        }

        if (parsed is null)
        {
            return new LoadResult(new AppConfig(), [], $"'{Path}' deserialised to null.", LoadSource.Missing);
        }

        var config = Normalize(parsed);
        return new LoadResult(config, ConfigValidator.Validate(config), null, LoadSource.Parsed);
    }

    /// <summary>
    /// Validates then writes atomically. Refuses to persist a config with errors, so a bad rule
    /// cannot be saved into a state where the daemon would pick it up.
    /// <para>
    /// Also refuses when the last <see cref="Load"/> failed. In that case the caller's config is
    /// not what is on disk, and writing it would replace rules this process never read with an
    /// empty set. Failing loudly costs the user one message; succeeding silently deletes their work.
    /// </para>
    /// </summary>
    public IReadOnlyList<ConfigIssue> Save(AppConfig config)
    {
        if (_loadError is not null)
        {
            return
            [
                new ConfigIssue(
                    null,
                    "",
                    $"Refusing to save: the config could not be read, so writing now would discard it. {_loadError}"),
            ];
        }

        List<ConfigIssue> issues = [.. ConfigValidator.Validate(config)];

        if (issues.Any(i => i.Severity == IssueSeverity.Error))
        {
            return issues;
        }

        string? dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = Write(config);
        string temp = Path + ".tmp";

        // Same directory so the rename stays on one volume and is therefore atomic.
        File.WriteAllText(temp, json);

        try
        {
            File.Move(temp, Path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return issues;
    }

    /// <summary>Repairs nulls that a hand-edited file can introduce, and re-attaches validation errors to their rules.</summary>
    private static AppConfig Normalize(AppConfig config)
    {
        List<Rule> normalized = [];

        foreach (Rule rule in config.Rules)
        {
            normalized.Add(rule with
            {
                Name = rule.Name ?? "New Rule",
                Errors = [],
                Monitor = rule.Monitor with { Paths = rule.Monitor.Paths ?? [] },
                If = rule.If with { Extensions = rule.If.Extensions ?? [] },
            });
        }

        AppConfig clean = config with { Rules = normalized };

        // Attach per-rule errors so the editor can show them inline.
        foreach (ConfigIssue issue in ConfigValidator.Validate(clean))
        {
            if (issue.Rule is null)
            {
                continue;
            }

            int index = normalized.FindIndex(r => string.Equals(r.Id, issue.RuleId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                normalized[index] = normalized[index] with
                {
                    Errors = [.. normalized[index].Errors, issue.Message],
                };
            }
        }

        return clean with { Rules = normalized };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort. The temp file is harmless.
        }
    }
}

/// <summary>
/// AOT-friendly serialisation contract. See docs/architecture.md.
/// <para>
/// This context existed but was never used: ConfigStore still went through the reflection-based
/// overloads, which are RequiresUnreferencedCode and RequiresDynamicCode and cannot survive
/// NativeAOT. That dead code is why the AOT analyzer warnings appeared, and it is very likely
/// how enums ended up written as integers: UseStringEnumConverter was never enabled here, so
/// nothing wrote enums as names.
/// </para>
/// <para>
/// GenerationMode must stay Default, not Metadata: Metadata emits type metadata only, which is
/// enough to validate but not to serialize or deserialize.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(GeneralSettings))]
[JsonSerializable(typeof(RunStats))]
[JsonSerializable(typeof(Rule))]
[JsonSerializable(typeof(MonitorSpec))]
[JsonSerializable(typeof(MonitorEntry))]
[JsonSerializable(typeof(ConditionSpec))]
[JsonSerializable(typeof(ActionSpec))]
[JsonSerializable(typeof(ActionKind))]
[JsonSerializable(typeof(SortKey))]
[JsonSerializable(typeof(PatternKind))]
internal sealed partial class AppConfigJsonContext : JsonSerializerContext;
