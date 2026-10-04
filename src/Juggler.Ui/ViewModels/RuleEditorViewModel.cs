using System.Collections.ObjectModel;
using Juggler.Core.Configuration;
using Juggler.Core.Rules;
using Juggler.Ui.Services;

namespace Juggler.Ui.ViewModels;

/// <summary>
/// Editable state for the rule editor window.
/// <para>
/// Holds a working copy: the rule is only written back to disk when the user saves, so closing
/// the window without saving discards cleanly.
/// </para>
/// </summary>
public sealed class RuleEditorViewModel
{
    private readonly ConfigService _config;

    public RuleEditorViewModel(Rule rule, ConfigService config)
    {
        _config = config;
        Rule = rule with
        {
            Monitor = rule.Monitor with { Paths = [.. rule.Monitor.Paths] },
            If = rule.If with { Extensions = [.. rule.If.Extensions] },
        };

        foreach (ActionKind kind in Enum.GetValues<ActionKind>())
        {
            Actions.Add(new Choice<ActionKind>(kind, DescribeAction(kind)));
        }

        foreach (SortKey key in Enum.GetValues<SortKey>())
        {
            SortKeys.Add(new Choice<SortKey>(key, DescribeSort(key)));
        }

        foreach (PatternKind kind in Enum.GetValues<PatternKind>())
        {
            PatternKinds.Add(new Choice<PatternKind>(kind,
                kind == PatternKind.Glob ? "Wildcard" : "Regular expression"));
        }

        Validate();
    }

    /// <summary>The working copy. Assigned to config on save.</summary>
    public Rule Rule { get; private set; }

    /// <summary>Set by the window's Save button. The host reads it on close.</summary>
    public bool SaveRequested { get; set; }

    public ObservableCollection<Choice<ActionKind>> Actions { get; } = [];

    public ObservableCollection<Choice<SortKey>> SortKeys { get; } = [];

    public ObservableCollection<Choice<PatternKind>> PatternKinds { get; } = [];

    public IReadOnlyList<ConfigIssue> Issues { get; private set; } = [];

    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);

    /// <summary>Raised after every mutation, so the window can refresh its error panel.</summary>
    public event EventHandler? Changed;

    public string ErrorSummary => HasErrors
        ? string.Join(Environment.NewLine, Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => "• " + i.Message))
        : string.Empty;

    /// <summary>
    /// Validates the working copy on its own, so errors appear while editing rather than only
    /// on save. The rule is wrapped in a throwaway config purely to reuse the validator.
    /// </summary>
    public void Validate()
    {
        AppConfig probe = _config.Current with { Rules = [Rule] };
        Issues = [.. ConfigValidator.Validate(probe).Where(i => i.Rule is not null)];
        Rule = Rule with { Errors = [.. Issues.Select(i => i.Message)] };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Direct assignment for fields the editor parses itself, such as the size bounds.</summary>
    public void UpdateCondition(Func<ConditionSpec, ConditionSpec> change)
    {
        Rule = Rule with { If = change(Rule.If) };
        Validate();
    }

    // ------------------------------------------------------------------ mutations

    public void SetName(string name)
    {
        Rule = Rule with { Name = name };
        Validate();
    }

    public void SetAction(ActionKind action)
    {
        Rule = Rule with { Then = Rule.Then with { Action = action } };
        Validate();
    }

    public void SetSortKey(SortKey key)
    {
        Rule = Rule with { Then = Rule.Then with { SortBy = key } };
        Validate();
    }

    public void SetPatternKind(PatternKind kind)
    {
        Rule = Rule with { If = Rule.If with { NamePatternKind = kind } };
        Validate();
    }

    public void SetIncludeSubfolders(bool include)
    {
        Rule = Rule with { Monitor = Rule.Monitor with { IncludeSubfolders = include } };
        Validate();
    }

    public void SetNamePattern(string? pattern)
    {
        Rule = Rule with { If = Rule.If with { NamePattern = string.IsNullOrWhiteSpace(pattern) ? null : pattern } };
        Validate();
    }

    public void SetExtensionsText(string text)
    {
        List<string> exts =
        [
            .. text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(e => e.TrimStart('.').ToLowerInvariant())
                  .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        Rule = Rule with { If = Rule.If with { Extensions = exts } };
        Validate();
    }

    public void SetDestination(string? path)
    {
        Rule = Rule with { Then = Rule.Then with { Into = string.IsNullOrWhiteSpace(path) ? null : path } };
        Validate();
    }

    public void SetRenamePattern(string? pattern)
    {
        Rule = Rule with { Then = Rule.Then with { RenamePattern = string.IsNullOrWhiteSpace(pattern) ? null : pattern } };
        Validate();
    }

    public void SetCommandKey(string? key)
    {
        Rule = Rule with { Then = Rule.Then with { CommandKey = string.IsNullOrWhiteSpace(key) ? null : key } };
        Validate();
    }

    public void AddMonitorPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Rule.Monitor.Paths.Any(p => p.Path == path))
        {
            return;
        }

        Rule = Rule with { Monitor = Rule.Monitor with { Paths = [.. Rule.Monitor.Paths, new MonitorEntry { Path = path }] } };
        Validate();
    }

    public void RemoveMonitorPath(string path)
    {
        Rule = Rule with
        {
            Monitor = Rule.Monitor with { Paths = [.. Rule.Monitor.Paths.Where(p => p.Path != path)] },
        };
        Validate();
    }

    public string ExtensionsText => string.Join(" ", Rule.If.Extensions);

    public static string DescribeAction(ActionKind kind) => kind switch
    {
        ActionKind.Move => "Move to folder",
        ActionKind.Copy => "Copy to folder",
        ActionKind.Rename => "Rename",
        ActionKind.SortIntoFolders => "Sort into folders",
        ActionKind.DeleteToRecycleBin => "Send to Recycle Bin",
        ActionKind.RunCommand => "Run command",
        _ => kind.ToString(),
    };

    public static string DescribeSort(SortKey key) => key switch
    {
        SortKey.Extension => "File type",
        SortKey.Name => "File name",
        SortKey.DateCreated => "Date created",
        SortKey.DateModified => "Date modified",
        SortKey.Size => "File size",
        _ => key.ToString(),
    };
}

/// <summary>A displayable option. Avalonia's ComboBox needs a name, not just a value.</summary>
public sealed record Choice<T>(T Value, string Label) where T : struct, Enum
{
    public override string ToString() => Label;
}
