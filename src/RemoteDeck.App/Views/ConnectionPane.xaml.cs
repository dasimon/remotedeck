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
