namespace Juggler.Core.Rules;

/// <summary>How a rule's name pattern is interpreted.</summary>
public enum PatternKind
{
    /// <summary>Wildcard match: <c>*.tmp</c>, <c>report-?.docx</c>. Case-insensitive.</summary>
    Glob,

    /// <summary>.NET regular expression. Case-insensitive by default. Compiled once, cached per rule.</summary>
    Regex,
}

/// <summary>What a matching rule does to the file.</summary>
public enum ActionKind
{
    /// <summary>Move to a destination path, creating directories as needed.</summary>
    Move,

    /// <summary>Copy to a destination path, leaving the original in place.</summary>
    Copy,

    /// <summary>Rename in place, optionally into a subfolder.</summary>
    Rename,

    /// <summary>Move into subfolders derived from a key (extension, date, size...).</summary>
    SortIntoFolders,

    /// <summary>Send to the Recycle Bin. Never an unlink. See docs/safety-and-recovery.</summary>
    DeleteToRecycleBin,

    /// <summary>Run an external command. Arguments come from a named allowlist, not raw config.</summary>
    RunCommand,
}

/// <summary>Key used to derive subfolder names for <see cref="ActionKind.SortIntoFolders"/>.</summary>
public enum SortKey
{
    Extension,
    Name,
    DateCreated,
    DateModified,
    Size,
}

/// <summary>A folder to watch.</summary>
public sealed record MonitorEntry
{
    /// <summary>Absolute path. Never contains <c>..</c> segments. Validated on load.</summary>
    public required string Path { get; init; }
}

/// <summary>The <c>Monitor</c> block: which folders this rule applies to.</summary>
public sealed record MonitorSpec
{
    public List<MonitorEntry> Paths { get; init; } = [];

    /// <summary>Descend into subdirectories. Depth-capped at runtime (see docs/performance).</summary>
    public bool IncludeSubfolders { get; init; } = true;
}

/// <summary>The <c>If</c> block. Every populated condition must match (logical AND).</summary>
public sealed record ConditionSpec
{
    /// <summary>Extensions without a leading dot, e.g. <c>png</c>, <c>jpg</c>. Empty means "any".</summary>
    public List<string> Extensions { get; init; } = [];

    public string? NamePattern { get; init; }
    public PatternKind NamePatternKind { get; init; } = PatternKind.Glob;

    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }

    public DateTimeOffset? CreatedAfter { get; init; }
    public DateTimeOffset? CreatedBefore { get; init; }
    public DateTimeOffset? ModifiedAfter { get; init; }
    public DateTimeOffset? ModifiedBefore { get; init; }

    /// <summary>True when no condition at all is set, i.e. "all files".</summary>
    public bool IsMatchAll =>
        Extensions.Count == 0
        && string.IsNullOrWhiteSpace(NamePattern)
        && MinSizeBytes is null
        && MaxSizeBytes is null
        && CreatedAfter is null
        && CreatedBefore is null
        && ModifiedAfter is null
        && ModifiedBefore is null;
}

/// <summary>The <c>Then</c> block.</summary>
public sealed record ActionSpec
{
    public ActionKind Action { get; init; } = ActionKind.Move;

    /// <summary>
    /// Destination directory. Supports tokens: <c>{extension}</c>, <c>{name}</c>, <c>{ext}</c>,
    /// <c>{date:yyyy-MM}</c>, <c>{size}</c>. A trailing separator creates per-key subfolders.
    /// </summary>
    public string? Into { get; init; }

    /// <summary>New filename. Same token syntax. Null leaves the name unchanged.</summary>
    public string? RenamePattern { get; init; }

    /// <summary>Only meaningful for <see cref="ActionKind.SortIntoFolders"/>.</summary>
    public SortKey SortBy { get; init; } = SortKey.Extension;

    /// <summary>Named entry in the command allowlist. Never a raw command line.</summary>
    public string? CommandKey { get; init; }
}

/// <summary>A single rule: monitor, conditions, action.</summary>
public sealed record Rule
{
    /// <summary>Stable identifier, used by the journal and by rule reordering.</summary>
    public required string Id { get; init; }

    public string Name { get; init; } = "New Rule";

    public bool Enabled { get; init; } = true;

    public MonitorSpec Monitor { get; init; } = new();

    public ConditionSpec If { get; init; } = new();

    public ActionSpec Then { get; init; } = new();

    /// <summary>Set when the last validation pass failed. Displayed in the UI; never persisted.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;

    /// <summary>Creates a rule with a fresh unique-enough identifier.</summary>
    public static Rule New() => new()
    {
        Id = Guid.NewGuid().ToString("n")[..12],
        Name = "New Rule",
    };
}
