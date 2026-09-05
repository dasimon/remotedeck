using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeck.App.Controls;
using RemoteDeck.App.Resources;
using RemoteDeck.App.Services;
using RemoteDeck.App.ViewModels;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

/// <summary>
/// Modal editor for one connection. Everything the user types lives in the view-model; this window
/// only wires the repositories, runs the validation and writes the row.
/// </summary>
public partial class ConnectionEditorWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ConnectionRepository _repository;
    private readonly Connection? _existing;
    private readonly ConnectionEditorViewModel _viewModel;

    /// <summary>True once the connection has been written to the database; the caller reloads on it.</summary>
    public bool Saved { get; private set; }

    /// <param name="existing">The connection to edit, or <c>null</c> to create a new one.</param>
    public ConnectionEditorWindow(Connection? existing)
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        // Once there is a handle, the monitor is known. SizeToContent still sizes the window to the
        // form; this only caps it, and past the cap the form scrolls between the title bar and the
        // buttons (see the Grid in the markup). Measured: ~990 px at 100 %, taller than a 768 px
        // laptop screen, and Save was unreachable there.
        SourceInitialized += (_, _) => MaxHeight = WorkAreaHeight() - 48;
        _repository = App.Current.Services.GetRequiredService<ConnectionRepository>();
        var credentials = App.Current.Services.GetRequiredService<CredentialRepository>().GetAll();
        _existing = existing;
        _viewModel = ConnectionEditorViewModel.From(existing, credentials, KnownGroups(), Services.WindowsVpn.KnownProfiles());
        DataContext = _viewModel;
        Loaded += (_, _) => NameInput.Focus();
    }

    /// <summary>The groups already in use, so the group combo suggests them instead of inviting typos.</summary>
    private IReadOnlyList<string> KnownGroups() =>
        [.. _repository.GetAll()
            .Select(c => c.GroupName)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)];

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.Validate())
        {
            ErrorBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, Strings.Editor_CheckFormTitle, _viewModel.Errors);
            return;
        }

        // The existing instance is edited in place so an Update carries the columns the form does not
        // expose (AcceptedCertThumbprint, LastConnectedUtc, CreatedUtc) through unchanged.
        var connection = _existing ?? new Connection { Name = "", Host = "" };
        _viewModel.ApplyTo(connection);

        try
        {
            if (_existing is null) _repository.Insert(connection); else _repository.Update(connection);
            ProbeLog.Write("connections", $"'{connection.Name}' {(_existing is null ? "created" : "updated")}");
            Saved = true;
            Close();
        }
        catch (Exception ex)
        {
            ProbeLog.Write("connections", $"Save failed: {ex.GetType().Name}: {ex.Message}");
            ErrorBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, Strings.Editor_CouldNotSaveTitle, ex.Message);
        }
    }

    /// <summary>
    /// The usable height of the monitor this window is on, in device-independent pixels — the
    /// screen minus the taskbar. The monitor is the one the handle sits on, not the primary: the
    /// editor opens centred on its owner, and the owner may be anywhere.
    /// </summary>
    private double WorkAreaHeight()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        return area.Height / VisualTreeHelper.GetDpi(this).DpiScaleY;
    }
}
