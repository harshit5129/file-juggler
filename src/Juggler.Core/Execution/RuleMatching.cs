using System.Globalization;
using System.Text;
using Juggler.Core.Rules;

// System.Globalization also defines a SortKey, so the unqualified name is ambiguous here.
using SortKey = Juggler.Core.Rules.SortKey;

namespace Juggler.Core.Execution;

/// <summary>A file, and everything a rule's conditions can ask about it.</summary>
/// <param name="FullPath">Absolute path. Never trusted from outside; always resolved by the caller.</param>
/// <param name="Length">Size in bytes.</param>
/// <param name="Created">Creation time.</param>
/// <param name="LastModified">Last write time.</param>
public sealed record FileCandidate(
    string FullPath,
    long Length,
    DateTimeOffset Created,
    DateTimeOffset LastModified)
{
    public string Name => Path.GetFileName(FullPath);

    public string Directory => Path.GetDirectoryName(FullPath) ?? string.Empty;

    /// <summary>Extension without the leading dot, lowercased. Empty when there is none.</summary>
    public string Extension =>
        Path.GetExtension(FullPath).TrimStart('.').ToLowerInvariant();

    /// <summary>Filename without its extension.</summary>
    public string Stem => Path.GetFileNameWithoutExtension(FullPath);
}

/// <summary>
/// Decides whether a file satisfies a rule's <c>If</c> block.
/// <para>
/// Every populated condition must hold (logical AND). This is the same reading the docs
/// describe and the one the editor labels, so it is the only one implemented.
/// </para>
/// </summary>
public static class RuleMatcher
{
    /// <summary>Upper bound on pattern length, matching what the validator accepts.</summary>
    private const int MaxPatternLength = 512;

    public static bool Matches(Rule rule, FileCandidate file) => Matches(rule.If, file);

    public static bool Matches(ConditionSpec condition, FileCandidate file)
    {
        if (condition.Extensions.Count > 0)
        {
            bool hit = false;

            foreach (string ext in condition.Extensions)
            {
                if (string.Equals(ext.TrimStart('.'), file.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    break;
                }
            }

            if (!hit)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(condition.NamePattern)
            && !NameMatches(condition.NamePattern, condition.NamePatternKind, file.Name))
        {
            return false;
        }

        if (condition.MinSizeBytes is { } min && file.Length < min)
        {
            return false;
        }

        if (condition.MaxSizeBytes is { } max && file.Length > max)
        {
            return false;
        }

        if (condition.CreatedAfter is { } createdAfter && file.Created < createdAfter)
        {
            return false;
        }

        if (condition.CreatedBefore is { } createdBefore && file.Created > createdBefore)
        {
            return false;
        }

        if (condition.ModifiedAfter is { } modifiedAfter && file.LastModified < modifiedAfter)
        {
            return false;
        }

        if (condition.ModifiedBefore is { } modifiedBefore && file.LastModified > modifiedBefore)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Matches a filename against a wildcard or a regular expression.
    /// <para>
    /// Glob is a hand-written matcher rather than a translated regex: a pattern containing
    /// <c>(</c> or <c>[</c> is a legal filename, and translating it would make those characters
    /// mean something the user never asked for. Backtracking here is bounded by
    /// <see cref="MaxPatternLength"/> against a single filename, so it cannot blow up.
    /// </para>
    /// </summary>
    public static bool NameMatches(string pattern, PatternKind kind, string name)
    {
        if (kind == PatternKind.Regex)
        {
            return RegexMatches(pattern, name);
        }

        return GlobMatches(pattern, name);
    }

    private static bool RegexMatches(string pattern, string name)
    {
        try
        {
            // Compiled once and cached per pattern. Not Compiled: it is AOT-published, where
            // interpreted is the safer default, and this runs at most once per candidate file.
            return System.Text.RegularExpressions.Regex.IsMatch(
                name, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
            // The validator rejects an invalid pattern, so this is only reachable if a rule
            // reached the runner without being validated. Treat as no match rather than throw.
            return false;
        }
    }

    /// <summary>Wildcard match where <c>*</c> is any run and <c>?</c> is exactly one character.</summary>
    internal static bool GlobMatches(string pattern, string name)
    {
        if (pattern.Length > MaxPatternLength)
        {
            return false;
        }

        int p = 0;
        int n = 0;
        int starP = -1;
        int starN = 0;

        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' ||
                char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(name[n])))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p;
                starN = n;
                p++;
            }
            else if (starP >= 0)
            {
                // Backtrack: let the last '*' absorb one more character.
                p = starP + 1;
                starN++;
                n = starN;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}

/// <summary>
/// Expands <c>{...}</c> tokens in a destination or rename pattern.
/// <para>
/// A token the code does not recognise is left in place as literal text rather than being
/// silently dropped, so an unknown token is visible in the output instead of quietly becoming a
/// blank. The validator already rejects unbalanced braces, which is what stops a half-written
/// token reaching here as part of a filename.
/// </para>
/// </summary>
public static class TokenExpander
{
    /// <summary>
    /// Expands the argument-less tokens only. <c>{date:format}</c> needs the caller to supply a
    /// timestamp and is handled by <see cref="ExpandWithDates"/>, so it is deliberately left
    /// alone here rather than guessed at.
    /// </summary>
    public static string Expand(string template, FileCandidate file) =>
        template.Replace("{extension}", file.Extension)
                .Replace("{ext}", file.Extension.Length == 0 ? string.Empty : "." + file.Extension)
                .Replace("{name}", file.Stem)
                .Replace("{size}", file.Length.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Expands tokens, handling <c>{date:format}</c> which carries a format specifier and so
    /// cannot go through simple string replacement.
    /// </summary>
    public static string ExpandWithDates(string template, FileCandidate file, DateTimeOffset timestamp)
    {
        if (!template.Contains("{date", StringComparison.Ordinal))
        {
            return Expand(template, file);
        }

        StringBuilder sb = new(template.Length);
        int i = 0;

        while (i < template.Length)
        {
            if (i + 6 <= template.Length && template.AsSpan(i, 6).SequenceEqual("{date:"))
            {
                int close = template.IndexOf('}', i + 6);
                if (close > 0)
                {
                    string format = template[(i + 6)..close];
                    sb.Append(timestamp.ToString(Format(format), CultureInfo.InvariantCulture));
                    i = close + 1;
                    continue;
                }
            }

            // "{date}" with no format specifier is a half-written token. Rendering it as
            // year-month is friendlier than writing the literal "{date}" into a filename.
            if (i + 6 <= template.Length && template.AsSpan(i, 6).SequenceEqual("{date}"))
            {
                sb.Append(timestamp.ToString("yyyy-MM", CultureInfo.InvariantCulture));
                i += 6;
                continue;
            }

            sb.Append(template[i]);
            i++;
        }

        return Expand(sb.ToString(), file);
    }

    private static string Format(string format) => format.Length == 0 ? "yyyy-MM" : format;

    /// <summary>Subfolder name a SortIntoFolders rule derives for one file.</summary>
    public static string SortFolderName(ActionSpec action, FileCandidate file)
    {
        string stamp = file.LastModified.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        return action.SortBy switch
        {
            SortKey.Extension => file.Extension,
            SortKey.Name => file.Stem,
            SortKey.DateCreated => file.Created.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            SortKey.DateModified => stamp,
            SortKey.Size => SizeBucket(file.Length),
            _ => file.Extension,
        };
    }

    /// <summary>
    /// Coarse size buckets rather than exact byte counts: a folder named <c>4837291</c> is not
    /// something a person can navigate.
    /// </summary>
    private static string SizeBucket(long bytes) => bytes switch
    {
        < 100 * 1024 => "Under 100 KB",
        < 1024 * 1024 => "100 KB - 1 MB",
        < 10 * 1024 * 1024 => "1 - 10 MB",
        < 100 * 1024 * 1024 => "10 - 100 MB",
        < 1024L * 1024 * 1024 => "100 MB - 1 GB",
        _ => "Over 1 GB",
    };
}