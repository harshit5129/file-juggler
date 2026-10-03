using Juggler.Core.Rules;
using Juggler.Ui.Services;

namespace Juggler.Ui.ViewModels;

/// <summary>
/// Display projection of a <see cref="Rule"/>.
/// <para>
/// Kept separate from the model so that formatting decisions ("2 folders watched") live in one
/// place and the rule itself stays free of presentation concerns.
/// </para>
/// </summary>
public sealed class RuleRow
{
    private const string Sep = "  \u00B7  ";      // middle dot separator
    private const string Quote = "\u201C";        // left double quote
    private const string EnDash = "\u2013";        // en dash, for ranges

    public RuleRow(Rule rule) => Rule = rule;

    /// <summary>
    /// Mutable by design: the row is a view onto the rule, so a toggle updates in place rather
    /// than replacing the instance the list is bound to.
    /// </summary>
    public Rule Rule { get; set; }

    public string Id => Rule.Id;

    public string Name => string.IsNullOrWhiteSpace(Rule.Name) ? "Untitled rule" : Rule.Name;

    public bool IsEnabled => Rule.Enabled;

    public bool HasError => Rule.HasErrors;

    public string ErrorText => string.Join(Sep, Rule.Errors);

    public string StatusText => Rule.Enabled
        ? Rule.HasErrors ? "Needs attention" : "Monitoring"
        : "Paused";

    /// <summary>Flips the enabled flag in place. The host persists it separately.</summary>
    public void ToggleEnabled() => Rule = Rule with { Enabled = !Rule.Enabled };

    /// <summary>Primary description: what it does, to what.</summary>
    public string Summary => $"{DescribeAction(Rule.Then.Action, Rule.Then.SortBy)}{Sep}{DescribeConditions()}";

    /// <summary>Secondary description: where it watches.</summary>
    public string PathSummary
    {
        get
        {
            int count = Rule.Monitor.Paths.Count;

            if (count == 0)
            {
                return "No folders selected yet";
            }

            if (count == 1)
            {
                return Formatting.Ellipsize(Rule.Monitor.Paths[0].Path);
            }

            return count + " folders watched"
                + (Rule.Monitor.IncludeSubfolders ? Sep + "including subfolders" : string.Empty);
        }
    }

    private static string DescribeAction(ActionKind kind, SortKey sortBy) => kind switch
    {
        ActionKind.Move => "Move",
        ActionKind.Copy => "Copy",
        ActionKind.Rename => "Rename",
        ActionKind.SortIntoFolders => $"Sort by {DescribeSort(sortBy)}",
        ActionKind.DeleteToRecycleBin => "Recycle",
        ActionKind.RunCommand => "Run command",
        _ => "Move",
    };

    private static string DescribeSort(SortKey key) => key switch
    {
        SortKey.Extension => "extension",
        SortKey.Name => "name",
        SortKey.DateCreated => "date created",
        SortKey.DateModified => "date modified",
        SortKey.Size => "size",
        _ => key.ToString().ToLowerInvariant(),
    };

    private string DescribeConditions()
    {
        if (Rule.If.IsMatchAll)
        {
            return "all files";
        }

        List<string> parts = [];

        if (Rule.If.Extensions.Count > 0)
        {
            parts.Add(string.Join(", ", Rule.If.Extensions.Select(e => "." + e)));
        }

        if (!string.IsNullOrWhiteSpace(Rule.If.NamePattern))
        {
            string kind = Rule.If.NamePatternKind == PatternKind.Regex ? "regex" : "name";
            parts.Add($"{kind} {Quote}{Rule.If.NamePattern}{Quote}");
        }

        if (Rule.If.MinSizeBytes is not null || Rule.If.MaxSizeBytes is not null)
        {
            parts.Add(Formatting.SizeOrAny(Rule.If.MinSizeBytes) + EnDash + Formatting.SizeOrAny(Rule.If.MaxSizeBytes));
        }

        if (Rule.If.CreatedAfter is not null || Rule.If.ModifiedAfter is not null)
        {
            parts.Add("recent");
        }

        return parts.Count == 0 ? "all files" : string.Join(Sep, parts);
    }
}
