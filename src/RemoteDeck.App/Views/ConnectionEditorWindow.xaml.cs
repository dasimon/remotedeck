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
    /// <param name="template">When creating, what the form starts from instead of blank — a duplicate.
    /// Ignored when <paramref name="existing"/> is given: saving then updates that row.</param>
    public ConnectionEditorWindow(Connection? existing, Connection? template = null)
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
        _viewModel = ConnectionEditorViewModel.From(existing ?? template, credentials, KnownGroups(), Services.WindowsVpn.KnownProfiles());
        DataContext = _viewModel;

        // Says which of the two it is: a form that looks the same for "new" and "edit" leaves the
        // user to guess whether Save will add a row or change one.
        Title = existing is null ? Strings.Editor_TitleNew : Text.Of(Strings.Editor_TitleEdit, existing.Name);
        EditorTitleBar.Title = Title;

        _initial = Snapshot();
        Closing += OnClosing;
        Loaded += (_, _) => NameInput.Focus();
    }

    /// <summary>The form as it stood when the window opened, to tell whether leaving loses anything.</summary>
    private readonly string _initial;

    /// <summary>Everything the form would write, as one comparable string.</summary>
    private string Snapshot()
    {
        var probe = new Connection { Name = "", Host = "" };
        _viewModel.ApplyTo(probe);
        return System.Text.Json.JsonSerializer.Serialize(probe);
    }

    /// <summary>
    /// Escape, Cancel and the title bar's cross all end here. The form is long, and one keystroke
    /// used to throw it away in silence; now a changed form asks first, with No as the default.
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Saved || Snapshot() == _initial)
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show(this, Strings.Editor_DiscardMessage, Strings.Editor_DiscardTitle,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        e.Cancel = answer != MessageBoxResult.Yes;
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

        // A copy of the existing instance, so an Update carries the columns the form does not expose
        // (LastConnectedUtc, CreatedUtc) through unchanged — and a failed write leaves the instance
        // the pane shows, and connects with, as it was.
        var connection = _existing?.Copy() ?? new Connection { Name = "", Host = "" };
        _viewModel.ApplyTo(connection);

        try
        {
            if (_existing is null) _repository.Insert(connection); else _repository.Update(connection);

            // Written: now the shared instance may change. An open session holds that same
            // instance, and its tab and next reconnect follow the edit, as they always have.
            if (_existing is not null) _viewModel.ApplyTo(_existing);
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
