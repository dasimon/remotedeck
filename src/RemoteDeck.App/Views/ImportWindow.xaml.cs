using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeck.App.Controls;
using RemoteDeck.App.Services;
using RemoteDeck.App.ViewModels;
using RemoteDeck.Core.Data;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

/// <summary>
/// Modal import preview: pick a source, look at what it found, untick what should not be written, then
/// import. Nothing reaches the database before the Import button is pressed.
///
/// The window owns the disk and the dialogs; <see cref="ImportViewModel"/> owns the reading, the
/// deduplication and the writing. No password is imported and no credential is created — the window
/// says so, in the window.
/// </summary>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms puts System.Windows.Forms in scope
// through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button, TextBox and
// friends ambiguous across the app.
public partial class ImportWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ImportViewModel _viewModel;
    private bool _loading;

    /// <summary>How many connections this window has written, over all its imports. The shell reloads on it.</summary>
    public int ImportedCount { get; private set; }

    public ImportWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        _viewModel = new ImportViewModel(App.Current.Services.GetRequiredService<ConnectionRepository>());
        DataContext = _viewModel;
    }

    /// <summary>
    /// Folder picker through <c>Microsoft.Win32.OpenFolderDialog</c> — the WPF one, so nothing here
    /// reaches for <c>System.Windows.Forms.FolderBrowserDialog</c>.
    /// </summary>
    /// <remarks><c>async void</c> is the only shape an event handler that awaits can take; the whole
    /// body is guarded, so nothing escapes onto the dispatcher.</remarks>
    private async void OnFromFolder(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder holding the .rdp files",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        SetLoading(true);
        try
        {
            await _viewModel.LoadFromFolderAsync(dialog.FolderName);
            StatusBar.Hide();
        }
        catch (Exception ex)
        {
            ProbeLog.Write("import", $"Folder read failed: {ex.GetType().Name}: {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, "Could not read that folder", ex.Message);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void OnFromRegistry(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        SetLoading(true);
        try
        {
            _viewModel.LoadFromRegistry();
            StatusBar.Hide();
        }
        catch (Exception ex)
        {
            ProbeLog.Write("import", $"Registry read failed: {ex.GetType().Name}: {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, "Could not read the Remote Desktop history", ex.Message);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void OnSelectAllNew(object sender, RoutedEventArgs e) => _viewModel.SelectAllNew();

    private void OnClear(object sender, RoutedEventArgs e) => _viewModel.ClearSelection();

    private void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var imported = _viewModel.Import();
            ImportedCount += imported;
            ProbeLog.Write("import", $"{imported} connection(s) imported");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success,
                imported == 1 ? "1 connection imported" : $"{imported} connections imported",
                "They were saved without a credential; add one from Manage credentials when you need it.");
        }
        catch (Exception ex)
        {
            ProbeLog.Write("import", $"Import failed: {ex.GetType().Name}: {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, "Import failed", ex.Message);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Keeps a slow folder from being browsed twice at once.</summary>
    private void SetLoading(bool loading)
    {
        _loading = loading;
        FolderButton.IsEnabled = !loading;
        RegistryButton.IsEnabled = !loading;
    }
}
