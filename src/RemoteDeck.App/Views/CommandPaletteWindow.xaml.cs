using System.Windows;
using System.Windows.Input;
using RemoteDeck.App.ViewModels;
using RemoteDeck.Core.Search;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

/// <summary>
/// The Ctrl+K command palette: a captionless overlay centred on the shell, listing the saved
/// connections, the open tabs and the shell's own commands, filtered as the user types.
///
/// The window decides nothing: the caller hands it the entries and reads <see cref="ChosenId"/>
/// after <see cref="Window.ShowDialog"/>. <c>null</c> means the palette was dismissed.
/// </summary>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms puts System.Windows.Forms in scope
// through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button, TextBox and
// friends ambiguous here.
public partial class CommandPaletteWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>Filled in when the DWM backdrop is refused; see the constructor.</summary>
    private const string OpaqueBackgroundKey = "ApplicationBackgroundBrush";

    private readonly CommandPaletteViewModel _viewModel;

    /// <summary>Guards the single close: <see cref="Window.Close"/> deactivates the window, which
    /// would otherwise re-enter <see cref="OnDeactivated"/> and clear a choice already made.</summary>
    private bool _closing;

    /// <summary>True once the window has actually held the keyboard; see <see cref="OnDeactivated"/>.</summary>
    private bool _activated;

    public CommandPaletteWindow(IReadOnlyList<PaletteItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        // Before the handle exists, so FluentWindow applies the decision once instead of applying
        // acrylic and then having it removed. A captionless window with no backdrop and no painted
        // root would be see-through, hence the opaque fallback rather than simply doing nothing.
        if (!Wpf.Ui.Controls.WindowBackdrop.IsSupported(Wpf.Ui.Controls.WindowBackdropType.Acrylic))
        {
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
            Root.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, OpaqueBackgroundKey);
        }

        _viewModel = new CommandPaletteViewModel(items);
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Activated += OnActivated;
    }

    /// <summary>What the user picked — <c>conn:&lt;id&gt;</c>, <c>cmd:&lt;name&gt;</c> or
    /// <c>tab:&lt;index&gt;</c> — or <c>null</c> when the palette was dismissed.</summary>
    public string? ChosenId { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Activate before focusing: the palette is useless without the keyboard, and asking for the
        // foreground explicitly is also what lets OnDeactivated tell "never got focus" from "lost it".
        Activate();
        SearchBox.Focus();
    }

    private void OnActivated(object? sender, EventArgs e) => _activated = true;

    /// <summary>
    /// The four palette keys, taken before the search box sees them: ↑ and ↓ would otherwise move
    /// the caret instead of the selection, and Enter would do nothing at all.
    /// </summary>
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Dismiss();
                e.Handled = true;
                break;

            case Key.Enter:
                Choose();
                e.Handled = true;
                break;

            case Key.Down:
                _viewModel.MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                _viewModel.MoveSelection(-1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Clicking outside dismisses the palette, the way every overlay of this shape does — but only
    /// once it has held the focus at least once. Windows sends WM_ACTIVATE/WA_INACTIVE to a window
    /// it shows while another process owns the foreground, and WPF turns that into a
    /// <see cref="Window.Deactivated"/> before the window is ever usable; acting on it would close
    /// the palette in the same frame it opened.
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_activated) return;

        Dismiss();
    }

    /// <summary>Keeps the keyboard selection visible: ↓ past the last drawn row must scroll.</summary>
    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is { } selected) List.ScrollIntoView(selected);
    }

    /// <summary>
    /// A single click on a row runs it — a palette is a chooser, and a first click that only
    /// highlights would leave the user hunting for the Enter key. That makes an accidental click
    /// expensive, so nothing but a real, currently listed row is allowed through.
    /// </summary>
    /// <remarks>
    /// The four tests below are the whole guard. A click on the scrollbar, on the list's own
    /// padding, or anywhere outside a row reaches this handler but sits under no item container, so
    /// <see cref="System.Windows.Controls.ItemsControl.ContainerFromElement"/> answers <c>null</c>
    /// and the click is dropped. The remaining three cover a container that is not one of ours, a
    /// container holding something the current query no longer lists, and a release that happened
    /// away from the row the press was reported against.
    /// </remarks>
    private void OnListMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (List.ContainerFromElement(source) is not System.Windows.Controls.ListViewItem container) return;
        if (System.Windows.Controls.ItemsControl.ItemsControlFromItemContainer(container) != List) return;
        if (container.Content is not PaletteMatch match || !_viewModel.Results.Contains(match)) return;
        if (!container.IsMouseOver) return;

        ChosenId = match.Item.Id;
        CloseOnce();
        e.Handled = true;
    }

    /// <summary>Enter: run whatever is selected. An empty result list has nothing to run, and
    /// deliberately does not close the palette — the user is mid-query.</summary>
    private void Choose()
    {
        if (_viewModel.SelectedId is not { } id) return;

        ChosenId = id;
        CloseOnce();
    }

    private void Dismiss() => CloseOnce();

    private void CloseOnce()
    {
        if (_closing) return;

        _closing = true;
        Close();
    }
}
