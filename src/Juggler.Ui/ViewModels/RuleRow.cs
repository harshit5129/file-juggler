using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Juggler.Core.Rules;
using Juggler.Ui.Services;

namespace Juggler.Ui.ViewModels;

/// <summary>
/// Display projection of a <see cref="Rule"/>.
/// <para>
/// Kept separate from the model so that formatting decisions ("2 folders watched") live in one
/// place and the rule itself stays free of presentation concerns.
/// </para>
/// <para>
/// Rows are long-lived and mutate in place. The list binds its <c>ItemsSource</c> once and
/// never replaces it. Two reasons:
/// <list type="bullet">
/// <item>Replacing <c>ItemsSource</c> wholesale tore down every item container mid-frame, and
/// Avalonia's <c>ContentPresenter</c> null-referenced while clearing them. Reproduced as a hard
/// crash the moment a rule was added, because that is exactly when the list is reassigned.</item>
/// <item>Recreating rows on every reload also contradicted the in-place toggle this class
/// documents, and made the enabled toggle lose its visual state across a refresh.</item>
/// </list>
/// </para>
/// </summary>
public sealed class RuleRow : INotifyPropertyChanged
{
    private const string Sep = "  \u00B7  ";      // middle dot separator
    private const string Quote = "\u201C";        // left double quote
    private const string EnDash = "\u2013";        // en dash, for ranges

    private Rule _rule;
    private bool _isVisible = true;

    public RuleRow(Rule rule) => _rule = rule;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The rule this row projects. Replacing it raises change notifications for everything
    /// derived from it, so a reload updates the visible text in place.
    /// </summary>
    public Rule Rule
    {
        get => _rule;
        set
        {
            if (ReferenceEquals(_rule, value))
            {
                return;
            }

            _rule = value;

            Raise(nameof(Rule), nameof(Name), nameof(IsEnabled), nameof(HasError),
                  nameof(ErrorText), nameof(StatusText), nameof(Summary), nameof(PathSummary));
        }
    }

    /// <summary>
    /// Drives list filtering. An <c>ItemsControl</c> cannot be filtered by reassigning its
    /// source, so rows hide themselves instead.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            Raise(nameof(IsVisible));
        }
    }

    public string Id => Rule.Id;

    public string Name => string.IsNullOrWhiteSpace(Rule.Name) ? "Untitled rule" : Rule.Name;

    public bool IsEnabled => Rule.Enabled;

    public bool HasError => Rule.HasErrors;

    public string ErrorText => string.Join(Sep, Rule.Errors);

    public string StatusText => Rule.Enabled
        ? Rule.HasErrors ? "Needs attention" : "Monitoring"
        : "Paused";

    /// <summary>Flips the enabled flag in place. The host persists it separately.</summary>
    public void ToggleEnabled()
    {
        Rule = Rule with { Enabled = !Rule.Enabled };
    }

    /// <summary>True when this row matches the given search text.</summary>
    public bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Summary.Contains(query, StringComparison.OrdinalIgnoreCase)
        || PathSummary.Contains(query, StringComparison.OrdinalIgnoreCase);

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
                // With a single folder, whether recursion is on changes nothing visible,
                // so name the folder instead of saying "1 folder watched".
                return Rule.Monitor.IncludeSubfolders
                    ? Formatting.Ellipsize(Rule.Monitor.Paths[0].Path) + Sep + "including subfolders"
                    : Formatting.Ellipsize(Rule.Monitor.Paths[0].Path);
            }

            return count + " folders watched"
                + (Rule.Monitor.IncludeSubfolders ? Sep + "including subfolders" : string.Empty);
        }
    }

    private void Raise([CallerMemberName] string? property = null, params string[] also)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

        foreach (string name in also)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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