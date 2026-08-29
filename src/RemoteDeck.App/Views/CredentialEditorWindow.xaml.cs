using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeck.App.Services;
using RemoteDeck.App.ViewModels;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Security;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

/// <summary>
/// Modal editor for one credential. The secret never becomes a managed string: it goes
/// straight from the native <c>PasswordBox</c> to a BSTR and into the vault.
/// </summary>
public partial class CredentialEditorWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly CredentialRepository _repository;
    private readonly ICredentialVault _vault;
    private readonly Credential? _existing;
    private readonly CredentialEditorViewModel _vm;

    /// <summary>True once the credential has been written to the database; the caller reloads on it.</summary>
    public bool Saved { get; private set; }

    public CredentialEditorWindow(Credential? existing)
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        _repository = App.Current.Services.GetRequiredService<CredentialRepository>();
        _vault = App.Current.Services.GetRequiredService<ICredentialVault>();
        _existing = existing;
        _vm = new CredentialEditorViewModel
        {
            IsNew = existing is null,
            Label = existing?.Label ?? "",
            UserName = existing?.UserName ?? "",
            Domain = existing?.Domain ?? "",
        };
        DataContext = _vm;
        PasswordHint.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => LabelInput.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var others = _repository.GetAll().Where(c => c.Id != (_existing?.Id ?? 0)).Select(c => c.Label);
        // SecurePassword hands out a fresh copy on every read: read it exactly once, own it for the
        // whole handler, and let the using dispose it on every exit path.
        using var secure = PasswordInput.SecurePassword;
        bool hasPassword = secure.Length > 0;
        if (!_vm.Validate(others) || (_vm.IsNew && !hasPassword))
        {
            if (_vm.IsNew && !hasPassword) _vm.Errors = string.Join("\n", new[] { _vm.Errors, "Password is required." }.Where(s => s.Length > 0));
            ErrorBar.IsOpen = true;
            return;
        }

        var credential = _existing ?? new Credential { Label = "", UserName = "", SecretBlob = [], Entropy = [] };
        credential.Label = _vm.Label.Trim();
        credential.UserName = _vm.UserName.Trim();
        credential.Domain = string.IsNullOrWhiteSpace(_vm.Domain) ? null : _vm.Domain.Trim();

        if (hasPassword)
        {
            // SecureString -> native BSTR -> vault (UTF-8 bytes -> DPAPI) -> zero+free. No managed string.
            nint bstr = Marshal.SecureStringToBSTR(secure);
            try { _vault.Seal(credential, bstr); }
            finally { Marshal.ZeroFreeBSTR(bstr); }
            PasswordInput.Clear();
        }

        try
        {
            if (_existing is null) _repository.Insert(credential); else _repository.Update(credential);
            ProbeLog.Write("vault", $"Credential '{credential.Label}' {(_existing is null ? "created" : "updated")} (secret {(hasPassword ? "sealed" : "unchanged")})");
            Saved = true;
            Close();
        }
        catch (Exception ex)
        {
            ProbeLog.Write("vault", $"Save failed: {ex.GetType().Name}: {ex.Message}");
            _vm.Errors = $"Could not save: {ex.Message}";
            ErrorBar.IsOpen = true;
        }
    }
}
