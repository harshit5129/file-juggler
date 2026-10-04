using System.Text.RegularExpressions;
using Juggler.Core.Rules;

namespace Juggler.Core.Configuration;

/// <summary>Severity of a configuration problem.</summary>
public enum IssueSeverity
{
    Warning,
    Error,
}

public sealed record ConfigIssue(Rule? Rule, string RuleId, string Message, IssueSeverity Severity = IssueSeverity.Error)
{
    public string Format() => RuleId.Length == 0 ? Message : $"[{RuleId}] {Message}";
}

/// <summary>
/// Validates an <see cref="AppConfig"/> before it is applied.
/// <para>
/// This runs <em>before</em> the running configuration is swapped in. An invalid config leaves the
/// active rules untouched: a stray comma must never silently disable someone's file rules.
/// See docs/architecture.md.
/// </para>
/// </summary>
public static class ConfigValidator
{
    private static readonly HashSet<string> ForbiddenCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "cmd.exe",
        "powershell", "powershell.exe",
        "pwsh", "pwsh.exe",
        "wscript", "wscript.exe",
        "cscript", "cscript.exe",
        "mshta", "mshta.exe",
        "reg", "regedit", "regedit.exe",
    };

    public static IReadOnlyList<ConfigIssue> Validate(AppConfig config)
    {
        List<ConfigIssue> issues = [];

        if (config.SchemaVersion != 1)
        {
            issues.Add(new ConfigIssue(null, string.Empty,
                $"Unsupported schemaVersion {config.SchemaVersion}. This build understands version 1.",
                IssueSeverity.Error));
        }

        ValidateGeneral(config.General, issues);

        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (Rule rule in config.Rules)
        {
            ValidateRule(rule, seenIds, issues);
        }

        return issues;
    }

    /// <summary>True when the config is safe to apply.</summary>
    public static bool CanApply(AppConfig config, out IReadOnlyList<ConfigIssue> issues)
    {
        issues = Validate(config);
        return issues.All(i => i.Severity != IssueSeverity.Error);
    }

    private static void ValidateGeneral(GeneralSettings g, List<ConfigIssue> issues)
    {
        if (g.DebounceSeconds is < 0 or > 3600)
            issues.Add(new ConfigIssue(null, string.Empty, "general.debounceSeconds must be between 0 and 3600."));

        if (g.SweepIntervalMinutes is < 1 or > 1440)
            issues.Add(new ConfigIssue(null, string.Empty, "general.sweepIntervalMinutes must be between 1 and 1440."));

        if (g.MaxConcurrent is < 1 or > 8)
            issues.Add(new ConfigIssue(null, string.Empty, "general.maxConcurrent must be between 1 and 8. Values above 1 usually hurt throughput on mechanical disks."));

        if (g.LogMaxMegabytes is < 1 or > 256)
            issues.Add(new ConfigIssue(null, string.Empty, "general.logMaxMegabytes must be between 1 and 256."));

        if (g.LogRotations is < 1 or > 20)
            issues.Add(new ConfigIssue(null, string.Empty, "general.logRotations must be between 1 and 20."));

        if (g.Notify && !g.NotifyOnError && !g.NotifyOnWatchedFolderMissing && !g.NotifyOnLowMemoryMode)
            issues.Add(new ConfigIssue(null, string.Empty,
                "general.notify is on but no event is enabled, so nothing can ever be raised.",
                IssueSeverity.Warning));
    }

    private static void ValidateRule(Rule rule, HashSet<string> seenIds, List<ConfigIssue> issues)
    {
        void Error(string message) => issues.Add(new ConfigIssue(rule, rule.Id, message));
        void Warn(string message) => issues.Add(new ConfigIssue(rule, rule.Id, message, IssueSeverity.Warning));

        // Identity problems are errors even for a draft: they are not something the user fixes
        // by filling in the form.
        ValidateIdentity(rule, seenIds, Error, Warn);

        if (IsDraft(rule))
        {
            // A rule watching nothing can never match a file, so it is inert rather than wrong.
            // Reporting errors on it would make the editor flag half-typed rules as broken and
            // train the user to ignore the error panel. See docs/safety-and-recovery.
            Warn("Draft rule: add a folder to monitor before this can do anything.");
            return;
        }

        ValidateMonitor(rule, Error);
        ValidateConditions(rule, Error, Warn);
        ValidateAction(rule, Error);
    }

    /// <summary>
    /// A rule with no monitored folders. Such a rule is inert: no file can ever match it, so
    /// validating its conditions and action produces noise rather than a real defect.
    /// </summary>
    private static bool IsDraft(Rule rule) => rule.Monitor?.Paths is null || rule.Monitor.Paths.Count == 0;

    private static void ValidateIdentity(Rule rule, HashSet<string> seenIds, Action<string> error, Action<string> warn)
    {
        if (string.IsNullOrWhiteSpace(rule.Id))
        {
            error("Rule has no id.");
        }
        else if (!seenIds.Add(rule.Id))
        {
            error($"Duplicate rule id '{rule.Id}'. Rule ids must be unique.");
        }

        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            warn("Rule has no description.");
        }
    }

    private static void ValidateMonitor(Rule rule, Action<string> error)
    {
        if (rule.Monitor?.Paths is null)
        {
            return;
        }

        foreach (MonitorEntry entry in rule.Monitor.Paths)
        {
            string? p = entry?.Path;

            if (string.IsNullOrWhiteSpace(p))
            {
                error("Monitor path is empty.");
                continue;
            }

            if (!Path.IsPathRooted(p))
                error($"Monitor path '{p}' must be absolute. Relative paths make behaviour depend on the working directory.");

            if (ContainsParentSegment(p))
                error($"Monitor path '{p}' contains '..'. Traversal is not permitted.");

            if (p.IndexOfAny(['*', '?']) >= 0)
                error($"Monitor path '{p}' contains a wildcard. Watch one folder per entry.");
        }
    }

    private static bool ContainsParentSegment(string path)
    {
        foreach (string part in path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
        {
            if (part == "..")
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateConditions(Rule rule, Action<string> error, Action<string> warn)
    {
        ConditionSpec? c = rule.If;

        if (c is null)
        {
            return;
        }

        foreach (string ext in c.Extensions)
        {
            if (string.IsNullOrWhiteSpace(ext))
                error("Condition contains an empty extension.");
            else if (ext.StartsWith('.'))
                error($"Extension '{ext}' must not include the leading dot.");
            else if (ext.Any(char.IsWhiteSpace))
                error($"Extension '{ext}' must not contain whitespace.");
        }

        if (c.MinSizeBytes is not null && c.MinSizeBytes < 0)
            error("Condition minSizeBytes must not be negative.");

        if (c.MinSizeBytes is not null && c.MaxSizeBytes is not null && c.MinSizeBytes > c.MaxSizeBytes)
            error("Condition minSizeBytes is greater than maxSizeBytes, so nothing can ever match.");

        if (c.CreatedAfter is not null && c.CreatedBefore is not null && c.CreatedAfter > c.CreatedBefore)
            error("Condition createdAfter is later than createdBefore, so nothing can ever match.");

        if (c.ModifiedAfter is not null && c.ModifiedBefore is not null && c.ModifiedAfter > c.ModifiedBefore)
            error("Condition modifiedAfter is later than modifiedBefore, so nothing can ever match.");

        if (string.IsNullOrWhiteSpace(c.NamePattern))
            return;

        if (c.NamePatternKind == PatternKind.Regex)
        {
            try
            {
                _ = new Regex(c.NamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                error($"Name pattern is not a valid regular expression: {ex.Message}");
            }
        }

        if (c.NamePattern.Length > 512)
            warn("Name pattern is very long; matching cost scales with pattern length.");
    }

    private static void ValidateAction(Rule rule, Action<string> error)
    {
        ActionSpec? a = rule.Then;

        if (a is null)
        {
            error("Action is missing.");
            return;
        }

        if (a.Action is ActionKind.Move or ActionKind.Copy)
        {
            if (string.IsNullOrWhiteSpace(a.Into))
                error($"Action '{a.Action}' requires a destination path.");
            else
                ValidateDestination(a.Into, error);
        }
        else if (a.Action == ActionKind.Rename)
        {
            if (string.IsNullOrWhiteSpace(a.RenamePattern) && string.IsNullOrWhiteSpace(a.Into))
            {
                error("Action 'Rename' requires a rename pattern or a destination folder.");
            }
            else if (!string.IsNullOrWhiteSpace(a.Into))
            {
                ValidateDestination(a.Into, error);
            }
        }
        else if (a.Action == ActionKind.SortIntoFolders)
        {
            if (string.IsNullOrWhiteSpace(a.Into))
                error("Action 'SortIntoFolders' requires a destination root.");
            else
                ValidateDestination(a.Into, error);
        }
        else if (a.Action == ActionKind.RunCommand)
        {
            if (string.IsNullOrWhiteSpace(a.CommandKey))
            {
                error("Action 'RunCommand' requires a command key.");
            }
            else if (ForbiddenCommands.Contains(a.CommandKey))
            {
                // Not a hard block, but it should never have gotten this far.
                error($"Command key '{a.CommandKey}' is an interpreter and is not permitted. Reference an allowlisted command instead.");
            }
        }

        if (!string.IsNullOrWhiteSpace(a.RenamePattern))
            ValidateTokens(a.RenamePattern, "renamePattern", error);
    }

    private static void ValidateDestination(string into, Action<string> error)
    {
        if (!Path.IsPathRooted(into))
        {
            error($"Destination '{into}' must be an absolute path.");
            return;
        }

        if (ContainsParentSegment(into))
            error($"Destination '{into}' contains '..'. Traversal is not permitted.");

        ValidateTokens(into, "destination", error);
    }

    /// <summary>
    /// Checks that <c>{...}</c> tokens are balanced. A half-written token would otherwise be
    /// written literally into a filename.
    /// </summary>
    private static void ValidateTokens(string value, string field, Action<string> error)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '}')
            {
                error($"{field} contains an unmatched '}}'. Every token must be opened, e.g. {{extension}}.");
                return;
            }

            if (value[i] != '{')
            {
                continue;
            }

            int close = value.IndexOf('}', i + 1);
            if (close < 0)
            {
                error($"{field} contains an unclosed '{{'. Every token must be closed, e.g. {{extension}}.");
                return;
            }

            if (close == i + 1)
            {
                error($"{field} contains an empty '{{}}' token.");
                return;
            }

            string inner = value.Substring(i + 1, close - i - 1);
            if (inner.Contains('{'))
            {
                error($"{field} contains nested '{{', which is not supported.");
                return;
            }

            i = close;
        }
    }
}
