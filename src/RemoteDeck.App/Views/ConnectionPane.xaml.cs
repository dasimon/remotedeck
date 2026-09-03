using System.Windows.Input;
using RemoteDeck.App.ViewModels;

namespace RemoteDeck.App.Views;

/// <summary>
/// The left pane: search box, "New" button, and the grouped, keyboard-driven connection list.
///
/// The control is deliberately self-contained — it owns no repository and opens no window. Every
/// gesture ends up on <see cref="ConnectionListViewModel"/>, which raises the events the shell acts on.
/// The shell hands it its view-model through <see cref="ViewModel"/> after construction; the
/// parameterless constructor is what lets the pane appear as a plain element in the shell's XAML.
/// </summary>
public partial class ConnectionPane : System.Windows.Controls.UserControl
{
    private ConnectionListViewModel? _viewModel;

    public ConnectionPane()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The pane's view-model. Setting it also sets the <see cref="System.Windows.FrameworkElement.DataContext"/>,
    /// so the bindings resolve. Reading it before the shell has assigned one is a programming error, hence
    /// the throw rather than a nullable getter that would spread <c>!</c> through the shell.
    /// </summary>
    public ConnectionListViewModel ViewModel
    {
        get => _viewModel ?? throw new InvalidOperationException($"{nameof(ConnectionPane)}.{nameof(ViewModel)} has not been set yet.");
        set
        {
            _viewModel = value;
            DataContext = value;
        }
    }

    /// <summary>Gives the search box the keyboard focus and selects what is already typed, so the next
    /// keystroke replaces the previous query. Called by the shell on Ctrl+F.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>
    /// Pane-wide keys. Enter and F2 act wherever the focus is inside the pane (typically the search box
    /// right after Ctrl+F); Delete deliberately does not — see <see cref="OnListKeyDown"/>.
    /// </summary>
    private void OnPanePreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_viewModel is null) return;

        switch (e.Key)
        {
            case Key.N when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.NewCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Enter:
                // Flush the search debounce first: Enter must act on the list the query describes,
                // not on the one from 120 ms ago. Refresh keeps the selection when it survives.
                _viewModel.Refresh();
                _viewModel.ConnectSelectedCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.F2:
                _viewModel.EditSelectedCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Delete is bound to the list alone: in the search box it must keep deleting characters.</summary>
    private void OnListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_viewModel is null || e.Key != Key.Delete) return;

        _viewModel.DeleteSelectedCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>A click on a workspace row opens it. On button-up, like the connection menu's entries:
    /// the press says the row is live, and sliding off before releasing changes nothing.</summary>
    private void OnWorkspaceClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null
            || sender is not System.Windows.FrameworkElement { DataContext: WorkspaceListItem item })
        {
            return;
        }

        e.Handled = true;
        _viewModel.OpenWorkspaceCommand.Execute(item);
    }

    /// <summary>
    /// Right-click, before the context menu opens: selects the row under the pointer and picks the
    /// menu that fits what was aimed at.
    /// </summary>
    /// <remarks>
    /// Selecting first is the whole point. A context menu whose entries act on
    /// <see cref="ConnectionListViewModel.Selected"/> would otherwise act on the row selected
    /// <em>before</em> the right-click — the classic way this feature deletes the wrong connection.
    /// Handled in the preview pass because WPF opens the menu on the button-up that follows.
    /// </remarks>
    private void OnListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var row = e.OriginalSource is System.Windows.DependencyObject source
            ? List.ContainerFromElement(source) as System.Windows.Controls.ListViewItem
            : null;

        if (row is null)
        {
            // Empty space below the last row: nothing to select, and the row menu would act on
            // whatever happened to be selected already.
            List.ContextMenu = (System.Windows.Controls.ContextMenu)Resources["EmptyMenu"];
            return;
        }

        row.IsSelected = true;
        List.ContextMenu = (System.Windows.Controls.ContextMenu)Resources["RowMenu"];
    }

    /// <summary>Double-click connects — but only on a row, not on the empty space below the last one,
    /// which would otherwise open a session the user never aimed at.</summary>
    private void OnListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is not System.Windows.DependencyObject source) return;
        if (List.ContainerFromElement(source) is not System.Windows.Controls.ListViewItem) return;

        _viewModel.ConnectSelectedCommand.Execute(null);
        e.Handled = true;
    }
}
