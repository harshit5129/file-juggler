using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Juggler.Core.Rules;
using Juggler.Core.Setup;

namespace Juggler.Ui.ViewModels;

/// <summary>
/// First-run setup state.
/// <para>
/// The point of this view model is that the user has very little to decide. Folders are detected
/// and preselected; rules are generated from them and preselected. Every control is a
/// deselection, not a construction. Nothing here needs a keyboard to reach a useful state.
/// </para>
/// </summary>
public sealed class SetupWizardViewModel : INotifyPropertyChanged
{
    private bool _foldersVisible = true;
    private bool _rulesVisible;

    public SetupWizardViewModel()
    {
        foreach (DetectedFolder folder in SetupSuggestions.Detect())
        {
            Folders.Add(new FolderChoice(folder) { IsSelected = true });
        }

        Recalculate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Every standard folder found on this machine, preselected.</summary>
    public ObservableCollection<FolderChoice> Folders { get; } = [];

    /// <summary>Generated rules for the selected folders, preselected.</summary>
    public ObservableCollection<RuleChoice> Rules { get; } = [];

    /// <summary>True on page one.</summary>
    public bool FoldersVisible
    {
        get => _foldersVisible;
        private set => Set(ref _foldersVisible, value);
    }

    /// <summary>True on page two.</summary>
    public bool RulesVisible
    {
        get => _rulesVisible;
        private set => Set(ref _rulesVisible, value);
    }

    public string Title => "Let's set this up";

    /// <summary>Headline. Changes per page so the window says what it is asking for.</summary>
    public string Headline => FoldersVisible
        ? "Which folders should I watch?"
        : "Here is what I would do. Remove anything you do not want.";

    public string Subtitle => FoldersVisible
        ? "I found these on your machine. Everything is already ticked - just untick what you do not want."
        : "Nothing is switched on yet. You can turn rules on one at a time from the list afterwards.";

    /// <summary>Shown when no standard folder could be found, so the page is not just empty.</summary>
    public bool NoFoldersFound => Folders.Count == 0;

    public string NoFoldersFoundText =>
        "I could not find a standard Downloads or Documents folder on this machine. You can still "
        + "continue and add folders by hand once setup is done.";

    public bool CanGoBack => RulesVisible;

    /// <summary>Rules that will actually be written.</summary>
    public IEnumerable<Rule> SelectedRules =>
        Rules.Where(r => r.IsSelected).Select(r => r.Rule);

    public int SelectedRuleCount => Rules.Count(r => r.IsSelected);

    public string Summary => SelectedRuleCount == 0
        ? "No rules will be created."
        : $"{SelectedRuleCount} rule{(SelectedRuleCount == 1 ? "" : "s")} created, all switched off.";

    /// <summary>Set by the window. The host reads it on close.</summary>
    public bool Completed { get; set; }

    /// <summary>Set when the user finishes. False means skip.</summary>
    public bool Accepted { get; set; }

    public void Next()
    {
        FoldersVisible = false;
        RulesVisible = true;
        Recalculate();
        Raise();
    }

    public void Back()
    {
        FoldersVisible = true;
        RulesVisible = false;
        Raise();
    }

    public void Accept()
    {
        Accepted = true;
        Completed = true;
    }

    public void Skip() => Completed = true;

    /// <summary>
    /// Regenerates proposals from the current folder selection. Called after every folder
    /// toggle: unticking Pictures must remove the screenshot rule, not leave it behind.
    /// </summary>
    public void Recalculate()
    {
        List<DetectedFolder> selected = [.. Folders.Where(f => f.IsSelected).Select(f => f.Folder)];
        List<RuleSuggestion> suggestions = [.. SetupSuggestions.Build(selected)];

        // Preserve the user's unchecks across a recalculation, so toggling one folder off and
        // on again does not silently re-enable a rule they deliberately removed.
        HashSet<string> rejected = Rules.Where(r => !r.IsSelected).Select(r => r.Rule.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Rules.Clear();
        foreach (RuleSuggestion suggestion in suggestions)
        {
            Rules.Add(new RuleChoice(suggestion)
            {
                IsSelected = !rejected.Contains(suggestion.Rule.Id),
            });
        }

        Raise();
    }

    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A detected folder plus the user's checkbox state for it.</summary>
public sealed class FolderChoice(DetectedFolder folder) : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DetectedFolder Folder { get; } = folder;

    public string Label => Folder.Label;

    public string Path => Folder.Path;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Set by the view model so a toggle regenerates the rules.</summary>
    public event EventHandler? SelectionChanged;
}

/// <summary>A proposed rule plus the user's checkbox state for it.</summary>
public sealed class RuleChoice(RuleSuggestion suggestion) : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RuleSuggestion Suggestion { get; } = suggestion;

    public Rule Rule => Suggestion.Rule;

    public string Name => Rule.Name;

    public string Rationale => Suggestion.Rationale;

    public string Destination => Rule.Then.Into ?? string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}