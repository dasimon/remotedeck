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
        Loaded += (_, _) => FocusList();
    }

    /// <summary>The list takes the keyboard on opening, on its first row, so Enter and Delete work
    /// without a click.</summary>
    private void FocusList()
    {
        if (List.SelectedIndex < 0 && List.Items.Count > 0) List.SelectedIndex = 0;
        List.Focus();
        if (List.ItemContainerGenerator.ContainerFromIndex(Math.Max(List.SelectedIndex, 0)) is System.Windows.Controls.ListViewItem row) row.Focus();
    }

    /// <summary>No Cancel button here to carry IsCancel, so Escape closes the window by hand.</summary>
    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>The keys the buttons stand for: Enter edits the selected row, Delete arms then confirms.</summary>
    private void OnListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Enter when Selected is not null:
                e.Handled = true;
                OnEdit(sender, e);
                break;
            case System.Windows.Input.Key.Delete when Selected is not null:
                e.Handled = true;
                OnDelete(sender, e);
                break;
        }
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
