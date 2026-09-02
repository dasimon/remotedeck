using System.Windows;

namespace RemoteDeck.App.Views;

/// <summary>
/// Le nom d'un espace, et s'il connecte ses sessions à l'ouverture. La seule fenêtre que les espaces
/// ajoutent : il n'y a pas d'éditeur d'espace, un espace se capture (spec espaces §5).
/// </summary>
/// <remarks>
/// Elle ne valide que le vide. Le doublon de nom n'est pas une erreur ici — c'est la façon normale
/// de faire évoluer un espace — et il est confirmé par l'appelant, qui est le seul à avoir le
/// dépôt sous la main.
/// </remarks>
// Wpf.Ui.Controls.* est qualifié à dessein : UseWindowsForms met System.Windows.Forms dans la portée
// via les usings implicites, et un `using Wpf.Ui.Controls;` nu rendrait Button et TextBox ambigus.
internal sealed partial class WorkspaceNameWindow : Wpf.Ui.Controls.FluentWindow
{
    public WorkspaceNameWindow(string? proposedName, bool autoConnect)
    {
        InitializeComponent();

        NameBox.Text = proposedName ?? string.Empty;
        AutoConnectBox.IsChecked = autoConnect;
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

        // Le champ prend le focus et le texte est présélectionné : la fenêtre sert à taper un nom, et
        // proposer un nom c'est proposer de le remplacer d'une frappe.
        Loaded += (_, _) =>
        {
            _ = NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>Le nom saisi, débarrassé de ses espaces de bord. Valide seulement après un
    /// <c>ShowDialog()</c> qui a rendu <c>true</c>.</summary>
    public string WorkspaceName { get; private set; } = string.Empty;

    public bool AutoConnect { get; private set; }

    /// <summary>Le bouton suit le champ : un espace sans nom ne peut pas être désigné dans la
    /// palette, qui est la seule façon de l'ouvrir.</summary>
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
