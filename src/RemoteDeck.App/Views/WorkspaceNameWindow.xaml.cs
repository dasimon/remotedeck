using System.Windows;

namespace RemoteDeck.App.Views;

/// <summary>
/// A workspace's name, and whether it connects its sessions when opened. The only window workspaces
/// add: there is no workspace editor, a workspace is captured.
/// </summary>
/// <remarks>
/// It only rejects an empty name. A duplicate name is not an error here — it is the normal way to
/// evolve a workspace — and it is confirmed by the caller, the only one with the repository at
/// hand.
/// </remarks>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms brings System.Windows.Forms into scope
// through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button and TextBox ambiguous.
internal sealed partial class WorkspaceNameWindow : Wpf.Ui.Controls.FluentWindow
{
    public WorkspaceNameWindow(string? proposedName, bool autoConnect)
    {
        InitializeComponent();
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);

        NameBox.Text = proposedName ?? string.Empty;
        AutoConnectBox.IsChecked = autoConnect;
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

        // The field takes focus with its text preselected: the window exists to type a name, and
        // proposing a name means offering to replace it in one keystroke.
        Loaded += (_, _) =>
        {
            _ = NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>The name typed, trimmed. Valid only after a <c>ShowDialog()</c> that returned
    /// <c>true</c>.</summary>
    public string WorkspaceName { get; private set; } = string.Empty;

    public bool AutoConnect { get; private set; }

    /// <summary>The button follows the field: a workspace without a name cannot be picked in the
    /// palette, which is the only way to open it.</summary>
    private void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        WorkspaceName = name;
        AutoConnect = AutoConnectBox.IsChecked == true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
