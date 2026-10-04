using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Juggler.Ui.ViewModels;

namespace Juggler.Ui.Views;

/// <summary>
/// First-run setup.
/// <para>
/// Modal on purpose. A rules list the user can interact with before finishing setup is a list they
/// can half-configure and leave behind, which is the failure mode this window exists to prevent.
/// </para>
/// <para>
/// Both outcomes (finish and skip) set <see cref="SetupWizardViewModel.Completed"/>, so the host
/// marks setup done either way. Skipping is a legitimate choice, not a partial state.
/// </para>
/// </summary>
public sealed partial class SetupWizard : Window
{
    private readonly SetupWizardViewModel _vm;

    private bool _ready;

    public SetupWizard(SetupWizardViewModel vm)
    {
        InitializeComponent();

        _vm = vm;
        DataContext = vm;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Subscribes after the tree exists, from OnOpened rather than OnInitialized: for a Window,
    /// Avalonia raises OnInitialized from inside the base constructor, which runs before this
    /// type's constructor body, so InitializeComponent() has not yet run. See RuleEditorWindow.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_ready)
        {
            return;
        }

        _ready = true;

        foreach (FolderChoice folder in _vm.Folders)
        {
            folder.SelectionChanged += OnFolderSelectionChanged;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (FolderChoice folder in _vm.Folders)
        {
            folder.SelectionChanged -= OnFolderSelectionChanged;
        }

        base.OnClosed(e);
    }

    private void OnFolderSelectionChanged(object? sender, EventArgs e) => _vm.Recalculate();

    private void OnNext(object? sender, RoutedEventArgs e) => _vm.Next();

    private void OnBack(object? sender, RoutedEventArgs e) => _vm.Back();

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        _vm.Accept();
        Close();
    }

    private void OnSkip(object? sender, RoutedEventArgs e)
    {
        _vm.Skip();
        Close();
    }
}