using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Juggler.Core.Rules;
using Juggler.Ui.Services;
using Juggler.Ui.ViewModels;

namespace Juggler.Ui.Views;

public sealed partial class RuleEditorWindow : Window
{
    private readonly RuleEditorViewModel _vm;

    // Resolved from the visual tree on open.
    private TextBlock _headerTitle = null!;
    private Button _saveButton = null!;
    private TextBox _nameBox = null!;
    private ItemsControl _monitorPaths = null!;
    private TextBlock _noPathsHint = null!;
    private CheckBox _subfoldersCheck = null!;
    private TextBox _namePatternBox = null!;
    private ComboBox _patternKindBox = null!;
    private TextBox _extensionsBox = null!;
    private TextBox _minSizeBox = null!;
    private TextBox _maxSizeBox = null!;
    private ComboBox _actionBox = null!;
    private TextBox _destinationBox = null!;
    private ComboBox _sortKeyBox = null!;
    private TextBox _renameBox = null!;
    private TextBox _commandBox = null!;
    private TextBlock _tokensText = null!;
    private Grid _destinationRow = null!;
    private Grid _sortKeyRow = null!;
    private Grid _renameRow = null!;
    private Grid _commandRow = null!;
    private Border _errorPanel = null!;
    private TextBlock _errorText = null!;

    /// <summary>
    /// Suppresses handling while controls are filled from the rule. Without it, assigning Text
    /// fires TextChanged, which would overwrite the model with the previous rule's values.
    /// </summary>
    /// <summary>Set once the named controls are resolved, so a re-open is a no-op.</summary>
    private bool _initialized;

    private bool _binding;

    public RuleEditorWindow(RuleEditorViewModel vm, bool isNew)
    {
        InitializeComponent();

        _vm = vm;
        Title = isNew ? "New rule" : "Edit rule";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Resolves the named controls and fills the form.
    /// <para>
    /// This must run from <see cref="OnOpened"/>, not <c>OnInitialized</c>. For a Window,
    /// Avalonia raises <c>OnInitialized</c> from inside the *base* constructor
    /// (<c>Window..ctor</c> -> <c>OnAttachedToVisualTreeCore</c> -> <c>InitializeIfNeeded</c>),
    /// which in C# runs before the derived constructor body. InitializeComponent() had
    /// therefore not run yet, the logical tree was empty, and every
    /// <c>Tree.Require&lt;T&gt;</c> threw "No TextBlock named 'HeaderTitle' found".
    /// Opening the new-rule editor crashed the whole process.
    /// <para>
    /// <c>OnOpened</c> fires once the window is shown, which is the first moment the XAML is
    /// guaranteed loaded. The <c>_initialized</c> guard makes a re-open a no-op rather than a
    /// repopulate that would silently discard the user's edits.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_initialized)
        {
            return;
        }

        _initialized = true;

        _headerTitle = this.Require<TextBlock>("HeaderTitle");
        _saveButton = this.Require<Button>("SaveButton");
        _nameBox = this.Require<TextBox>("NameBox");
        _monitorPaths = this.Require<ItemsControl>("MonitorPaths");
        _noPathsHint = this.Require<TextBlock>("NoPathsHint");
        _subfoldersCheck = this.Require<CheckBox>("SubfoldersCheck");
        _namePatternBox = this.Require<TextBox>("NamePatternBox");
        _patternKindBox = this.Require<ComboBox>("PatternKindBox");
        _extensionsBox = this.Require<TextBox>("ExtensionsBox");
        _minSizeBox = this.Require<TextBox>("MinSizeBox");
        _maxSizeBox = this.Require<TextBox>("MaxSizeBox");
        _actionBox = this.Require<ComboBox>("ActionBox");
        _destinationBox = this.Require<TextBox>("DestinationBox");
        _sortKeyBox = this.Require<ComboBox>("SortKeyBox");
        _renameBox = this.Require<TextBox>("RenameBox");
        _commandBox = this.Require<TextBox>("CommandBox");
        _tokensText = this.Require<TextBlock>("TokensText");
        _destinationRow = this.Require<Grid>("DestinationRow");
        _sortKeyRow = this.Require<Grid>("SortKeyRow");
        _renameRow = this.Require<Grid>("RenameRow");
        _commandRow = this.Require<Grid>("CommandRow");
        _errorPanel = this.Require<Border>("ErrorPanel");
        _errorText = this.Require<TextBlock>("ErrorText");

        // Guarded so a re-open cannot repopulate and discard edits.
        _binding = true;

        try
        {
            _headerTitle.Text = string.IsNullOrWhiteSpace(_vm.Rule.Name) ? "New rule" : _vm.Rule.Name;

            _patternKindBox.ItemsSource = _vm.PatternKinds;
            _patternKindBox.SelectedItem = _vm.PatternKinds.First(c => c.Value == _vm.Rule.If.NamePatternKind);

            _actionBox.ItemsSource = _vm.Actions;
            _actionBox.SelectedItem = _vm.Actions.First(c => c.Value == _vm.Rule.Then.Action);

            _sortKeyBox.ItemsSource = _vm.SortKeys;
            _sortKeyBox.SelectedItem = _vm.SortKeys.First(c => c.Value == _vm.Rule.Then.SortBy);

            _nameBox.Text = _vm.Rule.Name;
            _namePatternBox.Text = _vm.Rule.If.NamePattern ?? string.Empty;
            _extensionsBox.Text = _vm.ExtensionsText;
            _minSizeBox.Text = _vm.Rule.If.MinSizeBytes?.ToString() ?? string.Empty;
            _maxSizeBox.Text = _vm.Rule.If.MaxSizeBytes?.ToString() ?? string.Empty;
            _destinationBox.Text = _vm.Rule.Then.Into ?? string.Empty;
            _renameBox.Text = _vm.Rule.Then.RenamePattern ?? string.Empty;
            _commandBox.Text = _vm.Rule.Then.CommandKey ?? string.Empty;
            _subfoldersCheck.IsChecked = _vm.Rule.Monitor.IncludeSubfolders;

            // These carry literal braces. XAML would read "{...}" as a markup extension,
            // so they are assigned here where the string is unambiguous.
            _renameBox.PlaceholderText = "{date:yyyy-MM-dd}-{name}{ext}";
            _destinationBox.PlaceholderText = @"C:\Users\me\Pictures\Sorted\{extension}";
            _tokensText.Text =
                "{extension}   {ext}   {name}   {date:yyyy-MM-dd}   {size}"
                + "   \u2014 usable in both the destination and the rename pattern."
                + " End the destination with a backslash to create one folder per value.";

            RenderPaths();
            ApplyActionVisibility(_vm.Rule.Then.Action);
        }
        finally
        {
            _binding = false;
        }

        _vm.Changed += OnVmChanged;
        ShowValidation();
    }

    private void OnVmChanged(object? sender, EventArgs e) => ShowValidation();

    private void ShowValidation()
    {
        _errorPanel.IsVisible = _vm.HasErrors;
        _errorText.Text = _vm.ErrorSummary;

        // Save stays enabled on purpose. Disabling it would explain nothing; the user needs to
        // be able to click and be told precisely what is wrong.
        _saveButton.Classes.Set("accent", true);
    }

    // ------------------------------------------------------------------ monitor paths

    private void RenderPaths()
    {
        _monitorPaths.ItemsSource = _vm.Rule.Monitor.Paths;
        _noPathsHint.IsVisible = _vm.Rule.Monitor.Paths.Count == 0;
    }

    private async void OnAddPath(object? sender, RoutedEventArgs e)
    {
        foreach (string path in await PickFoldersAsync("Choose a folder to watch"))
        {
            _vm.AddMonitorPath(path);
        }

        RenderPaths();
    }

    private void OnRemovePath(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string path)
        {
            _vm.RemoveMonitorPath(path);
            RenderPaths();
        }
    }

    private void OnSubfoldersChanged(object? sender, RoutedEventArgs e)
    {
        if (!_binding)
        {
            _vm.SetIncludeSubfolders(_subfoldersCheck.IsChecked == true);
        }
    }

    /// <summary>
    /// Folder picking goes through the shell provider rather than being typed, so the stored path
    /// is always a real local path. A virtual folder would be written to the config and then
    /// silently never watched by the daemon.
    /// </summary>
    private async Task<IReadOnlyList<string>> PickFoldersAsync(string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider)
        {
            return [];
        }

        IReadOnlyList<IStorageFolder> folders = await provider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = true });

        List<string> result = [];

        foreach (IStorageFolder folder in folders)
        {
            if (folder.TryGetLocalPath() is { } local)
            {
                result.Add(local);
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ fields

    private void OnNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_binding)
        {
            _vm.SetName(_nameBox.Text ?? string.Empty);
        }
    }

    private void OnPatternChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_binding)
        {
            _vm.SetNamePattern(_namePatternBox.Text);
        }
    }

    private void OnPatternKindChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_binding && _patternKindBox.SelectedItem is Choice<PatternKind> choice)
        {
            _vm.SetPatternKind(choice.Value);
        }
    }

    private void OnExtensionsChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_binding)
        {
            _vm.SetExtensionsText(_extensionsBox.Text ?? string.Empty);
        }
    }

    private void OnSizeChanged(object? sender, TextChangedEventArgs e)
    {
        if (_binding)
        {
            return;
        }

        // Parsed leniently: empty clears the bound, a valid non-negative number sets it,
        // and anything else is left alone so a half-typed or invalid value never silently
        // wipes an existing bound. Mid-keystroke input is not a mistake worth blocking on,
        // but silently dropping a bound the user thought they set would be data loss.
        string? minText = _minSizeBox.Text;
        string? maxText = _maxSizeBox.Text;

        long? min = ParseSize(minText);
        long? max = ParseSize(maxText);

        bool minInvalid = !string.IsNullOrWhiteSpace(minText) && min is null;
        bool maxInvalid = !string.IsNullOrWhiteSpace(maxText) && max is null;

        if (minInvalid || maxInvalid)
        {
            return;
        }

        _vm.UpdateCondition(c => c with
        {
            MinSizeBytes = min,
            MaxSizeBytes = max,
        });
    }

    private static long? ParseSize(string? text)
    {
        string trimmed = text?.Trim() ?? string.Empty;

        return trimmed.Length == 0
            ? null
            : long.TryParse(trimmed, out long value) && value >= 0 ? value : null;
    }

    private void OnDestinationChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_binding)
        {
            _vm.SetDestination(_destinationBox.Text);
        }
    }

    private void OnRenameChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_binding)
        {
            _vm.SetRenamePattern(_renameBox.Text);
        }
    }

    private void OnCommandChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_binding)
        {
            _vm.SetCommandKey(_commandBox.Text);
        }
    }

    private void OnActionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_binding && _actionBox.SelectedItem is Choice<ActionKind> choice)
        {
            _vm.SetAction(choice.Value);
            ApplyActionVisibility(choice.Value);
        }
    }

    private void OnSortKeyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_binding && _sortKeyBox.SelectedItem is Choice<SortKey> choice)
        {
            _vm.SetSortKey(choice.Value);
        }
    }

    /// <summary>Reveals only the fields the chosen action actually uses.</summary>
    private void ApplyActionVisibility(ActionKind action)
    {
        _destinationRow.IsVisible = action is ActionKind.Move or ActionKind.Copy or ActionKind.SortIntoFolders;
        _sortKeyRow.IsVisible = action == ActionKind.SortIntoFolders;
        _commandRow.IsVisible = action == ActionKind.RunCommand;
        _renameRow.IsVisible = action is not (ActionKind.DeleteToRecycleBin or ActionKind.RunCommand);
    }

    private async void OnBrowseDestination(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> picked = await PickFoldersAsync("Choose a destination folder");

        if (picked.Count == 0)
        {
            return;
        }

        _binding = true;
        _destinationBox.Text = picked[0];
        _binding = false;

        _vm.SetDestination(_destinationBox.Text);
    }

    // ------------------------------------------------------------------ commands

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        _vm.SaveRequested = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _vm.Changed -= OnVmChanged;
        base.OnClosed(e);
    }
}
