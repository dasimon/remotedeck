using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeck.App.Controls;
using RemoteDeck.App.Resources;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

/// <summary>Modal credential manager: list, add, edit and a two-step delete. Status goes through the InfoBar, never a MessageBox.</summary>
public partial class CredentialsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly CredentialRepository _repository;
    private Credential? _pendingDelete;

    public CredentialsWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        _repository = App.Current.Services.GetRequiredService<CredentialRepository>();
        Reload();
    }

    private Credential? Selected => List.SelectedItem as Credential;

    private void Reload()
    {
        List.ItemsSource = _repository.GetAll();
        _pendingDelete = null;
        DeleteButton.Content = Strings.Credentials_Delete;
        OnSelectionChanged(this, null!);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool has = Selected is not null;
        EditButton.IsEnabled = has;
        DeleteButton.IsEnabled = has;
        if (_pendingDelete is not null && !ReferenceEquals(_pendingDelete, Selected))
        {
            _pendingDelete = null;
            DeleteButton.Content = Strings.Credentials_Delete;
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Selected is { } c) OpenEditor(c);
    }

    private void OpenEditor(Credential? existing)
    {
        var editor = new CredentialEditorWindow(existing) { Owner = this };
        editor.ShowDialog();
        if (editor.Saved) Reload();
    }

    /// <summary>Two-step delete: the first click arms the button, the second one deletes. No MessageBox.</summary>
    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } c) return;
        if (!ReferenceEquals(_pendingDelete, c))
        {
            _pendingDelete = c;
            DeleteButton.Content = Strings.Credentials_ConfirmDelete;
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, Strings.Credentials_DeleteConfirmTitle,
                Text.Of(Strings.Credentials_DeleteConfirmMessage, c.Label));
            return;
        }
        try
        {
            _repository.Delete(c.Id);
            ProbeLog.Write("vault", $"Credential '{c.Label}' deleted");
            StatusBar.Hide();
            Reload();
        }
        catch (Exception ex)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, Strings.Common_DeleteFailedTitle, ex.Message);
        }
    }
}
