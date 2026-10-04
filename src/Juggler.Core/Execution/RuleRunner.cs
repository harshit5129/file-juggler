using Juggler.Core.Configuration;
using Juggler.Core.Rules;

namespace Juggler.Core.Execution;

/// <summary>What a single file's outcome was.</summary>
public enum ActionOutcome
{
    /// <summary>Nothing matched, or the action was a no-op for this file.</summary>
    Skipped,

    /// <summary>Would have acted, but dry-run was on.</summary>
    WouldAct,

    /// <summary>The file was moved or renamed.</summary>
    Moved,

    /// <summary>A copy was made; the original is untouched.</summary>
    Copied,

    /// <summary>Sent to the Recycle Bin.</summary>
    Recycled,
}

/// <summary>One file's result. Returned for every candidate, matched or not.</summary>
public sealed record RuleRunEntry(
    string Source,
    string? Target,
    ActionOutcome Outcome,
    string? Note = null)
{
    public bool Failed => Note is not null && Outcome == ActionOutcome.Skipped && Target is null;
}

/// <summary>The result of running one rule over its monitored folders.</summary>
public sealed record RuleRunResult(
    string RuleId,
    IReadOnlyList<RuleRunEntry> Entries,
    int Scanned,
    bool DryRun)
{
    public int Matched => Entries.Count(e => e.Outcome != ActionOutcome.Skipped);

    public int Failed => Entries.Count(e => e.Failed);

    public int Changed =>
        Entries.Count(e => e.Outcome is ActionOutcome.Moved or ActionOutcome.Copied or ActionOutcome.Recycled);

    public int WouldHaveChanged =>
        Entries.Count(e => e.Outcome is ActionOutcome.WouldAct);
}

/// <summary>
/// Runs one rule over the files in its monitored folders.
/// <para>
/// This is the manual counterpart to the daemon's sweep: same matching, same actions, same
/// dry-run gate. It exists so the user can act on a rule without waiting for the resident
/// process, which does not exist yet.
/// </para>
/// <para>
/// Nothing here is clever about safety. Every guard is a place where the conservative answer is
/// to skip the file and say why, because the alternative is moving someone's data somewhere
/// unexpected with no record of it.
/// </para>
/// </summary>
public sealed class RuleRunner
{
    /// <summary>How deep a recursive scan is allowed to go. See docs/performance.md.</summary>
    private const int MaxDepth = 8;

    private readonly bool _dryRun;

    public RuleRunner(bool dryRun) => _dryRun = dryRun;

    /// <summary>
    /// Scans <paramref name="root"/> and applies <paramref name="rule"/> to what matches.
    /// </summary>
    public RuleRunResult Run(Rule rule, string root)
    {
        List<RuleRunEntry> entries = [];
        int scanned = 0;

        foreach (FileCandidate file in Enumerate(rule, root, out _))
        {
            scanned++;

            entries.Add(Act(rule, file));
        }

        return new RuleRunResult(rule.Id, entries, scanned, _dryRun);
    }

    /// <summary>
    /// Lists what a rule would do, without doing any of it. Always safe: no file is opened for
    /// writing, nothing is created, and a dry-run gate is irrelevant because nothing acts.
    /// </summary>
    public RuleRunResult Preview(Rule rule, string root)
    {
        List<RuleRunEntry> entries = [];
        int scanned = 0;

        foreach (FileCandidate file in Enumerate(rule, root, out _))
        {
            scanned++;

            if (RuleMatcher.Matches(rule, file))
            {
                entries.Add(new RuleRunEntry(
                    file.FullPath,
                    ResolveTarget(rule, file),
                    ActionOutcome.WouldAct));
            }
        }

        return new RuleRunResult(rule.Id, entries, scanned, DryRun: true);
    }

    /// <summary>Enumerate candidates, calling <paramref name="errors"/> for anything unreadable.</summary>
    private IEnumerable<FileCandidate> Enumerate(Rule rule, string root, out List<string> errors)
    {
        errors = [];

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            errors.Add($"{root} does not exist.");
            return [];
        }

        var results = new List<FileCandidate>();
        Walk(root, rule.Monitor.IncludeSubfolders ? MaxDepth : 0, results, errors);
        return results;
    }

    private void Walk(string directory, int remainingDepth, List<FileCandidate> into, List<string> errors)
    {
        string[] files;

        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{directory}: {ex.Message}");
            return;
        }

        foreach (string path in files)
        {
            FileCandidate? candidate = TryDescribe(path, errors);
            if (candidate is not null)
            {
                into.Add(candidate);
            }
        }

        if (remainingDepth <= 0)
        {
            return;
        }

        string[] directories;

        try
        {
            directories = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{directory}: {ex.Message}");
            return;
        }

        foreach (string child in directories)
        {
            // A reparse point is a link to somewhere else. Following one would reach outside the
            // folder the user named, which is the thing recursion is capped to prevent.
            if (IsReparsePoint(child))
            {
                continue;
            }

            Walk(child, remainingDepth - 1, into, errors);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;   // Cannot tell, so treat it as unsafe.
        }
    }

    private static FileCandidate? TryDescribe(string path, List<string> errors)
    {
        try
        {
            FileInfo info = new(path);

            // A shortcut is not a file the user wants sorted; it is a pointer to one.
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            return new FileCandidate(
                info.FullName,
                info.Length,
                new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{path}: {ex.Message}");
            return null;
        }
    }

    private RuleRunEntry Act(Rule rule, FileCandidate file)
    {
        if (!RuleMatcher.Matches(rule, file))
        {
            return new RuleRunEntry(file.FullPath, null, ActionOutcome.Skipped, "No match.");
        }

        string? target = ResolveTarget(rule, file);

        if (target is null)
        {
            return new RuleRunEntry(file.FullPath, null, ActionOutcome.Skipped, "No destination for this action.");
        }

        if (_dryRun)
        {
            return new RuleRunEntry(file.FullPath, target, ActionOutcome.WouldAct, "Dry run: nothing changed.");
        }

        try
        {
            return rule.Then.Action switch
            {
                ActionKind.Copy => Copy(file, target),
                ActionKind.DeleteToRecycleBin => Recycle(file),
                ActionKind.Rename when string.IsNullOrWhiteSpace(rule.Then.Into) => RenameInPlace(file, target),
                ActionKind.RunCommand => new RuleRunEntry(
                    file.FullPath, null, ActionOutcome.Skipped,
                    "RunCommand needs the daemon; it has no command allowlist yet."),
                _ => Move(file, target),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new RuleRunEntry(file.FullPath, target, ActionOutcome.Skipped, ex.Message);
        }
    }

    /// <summary>Where this file should end up, or null when the action has no destination.</summary>
    private static string? ResolveTarget(Rule rule, FileCandidate file)
    {
        ActionSpec action = rule.Then;

        if (action.Action == ActionKind.DeleteToRecycleBin || action.Action == ActionKind.RunCommand)
        {
            return null;
        }

        // A destination ending in a separator means "one folder per key", which is what the
        // editor's token hint tells the user. SortIntoFolders with a plain destination also gets
        // one, because otherwise SortBy is dead: the rule would say "sort by date" and quietly
        // file everything into a single directory. A destination that already names its own key
        // wins, so a rule written as "...\Papers\{date:yyyy}" does not become "Papers\2026\2026".
        bool perKey = action.Action == ActionKind.SortIntoFolders
            ? !string.IsNullOrEmpty(action.Into) && !action.Into.Contains('{', StringComparison.Ordinal)
            : action.Into is { Length: > 0 } into
                && (into.EndsWith(Path.DirectorySeparatorChar)
                    || into.EndsWith(Path.AltDirectorySeparatorChar));

        if (perKey)
        {
            string key = action.Action == ActionKind.SortIntoFolders
                ? TokenExpander.SortFolderName(action, file)
                : file.Extension;

            action = action with { Into = Path.Combine(action.Into!, key) };
        }

        if (string.IsNullOrWhiteSpace(action.Into))
        {
            // Pure rename: same directory, new name. ExpandWithDates, not Expand: a rename
            // pattern is allowed to carry {date:...} exactly as a destination can.
            return action.RenamePattern is null
                ? null
                : Path.Combine(
                    file.Directory,
                    SafeName(TokenExpander.ExpandWithDates(action.RenamePattern, file, file.LastModified)));
        }

        string directory = TokenExpander.ExpandWithDates(action.Into!, file, file.LastModified);

        string filename = action.RenamePattern is { } pattern
            ? TokenExpander.Expand(pattern, file)
            : file.Name;

        return Path.Combine(directory, SafeName(filename));
    }

    /// <summary>
    /// Strips characters that cannot appear in a filename and refuses names Windows rejects.
    /// Without this a token expanding to a path separator would place the file somewhere the
    /// rule never named.
    /// </summary>
    internal static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed";
        }

        Span<char> buffer = stackalloc char[name.Length];
        int length = 0;

        foreach (char c in name)
        {
            bool invalid = Path.GetInvalidFileNameChars().Contains(c)
                           || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?';

            buffer[length++] = invalid ? '-' : c;
        }

        string cleaned = new string(buffer[..length]).Trim().TrimEnd('.');

        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }

    private static RuleRunEntry Move(FileCandidate file, string target)
    {
        string? guard = Guard(file, target);
        if (guard is not null)
        {
            return new RuleRunEntry(file.FullPath, target, ActionOutcome.Skipped, guard);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(file.FullPath, target);
        return new RuleRunEntry(file.FullPath, target, ActionOutcome.Moved);
    }

    private static RuleRunEntry Copy(FileCandidate file, string target)
    {
        string? guard = Guard(file, target);
        if (guard is not null)
        {
            return new RuleRunEntry(file.FullPath, target, ActionOutcome.Skipped, guard);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Never overwrite. Silently replacing a file that happens to share a name destroys data
        // that no rule asked to touch.
        File.Copy(file.FullPath, target, overwrite: false);
        return new RuleRunEntry(file.FullPath, target, ActionOutcome.Copied);
    }

    private static RuleRunEntry RenameInPlace(FileCandidate file, string target)
    {
        if (string.Equals(Path.GetFullPath(file.FullPath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return new RuleRunEntry(file.FullPath, target, ActionOutcome.Skipped, "Name would not change.");
        }

        return Move(file, target);
    }

    /// <summary>
    /// Refuses the moves that would destroy something: overwriting an existing file, moving a
    /// file onto itself, or moving it into a directory it came from.
    /// </summary>
    private static string? Guard(FileCandidate file, string target)
    {
        string source = Path.GetFullPath(file.FullPath);
        string destination = Path.GetFullPath(target);

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            return "Source and destination are the same.";
        }

        if (File.Exists(destination))
        {
            return $"'{Path.GetFileName(destination)}' already exists; left alone.";
        }

        return null;
    }

    private static RuleRunEntry Recycle(FileCandidate file)
    {
        throw new NotSupportedException(
            "Recycle Bin support needs the resident daemon; it has no shell integration yet.");
    }

    /// <summary>Convenience: run every enabled rule whose monitor covers <paramref name="root"/>.</summary>
    public IReadOnlyList<RuleRunResult> RunAll(AppConfig config, string root)
    {
        List<RuleRunResult> results = [];

        foreach (Rule rule in config.Rules)
        {
            if (!rule.Enabled || !rule.Monitor.Paths.Any(p =>
                    string.Equals(p.Path, root, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(Run(rule, root));
        }

        return results;
    }
}