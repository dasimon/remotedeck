using CommunityToolkit.Mvvm.ComponentModel;
using RemoteDeck.App.Resources;
using RemoteDeck.Core.Security;

namespace RemoteDeck.App.ViewModels;

/// <summary>Form state for the credential editor. Never holds the secret: the view seals it directly through the vault.</summary>
public sealed partial class CredentialEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _label = "";

    /// <summary>Set by <see cref="Validate"/>; paints the field's edge through <c>Mark.Invalid</c>.</summary>
    [ObservableProperty] private bool _labelInvalid;
    [ObservableProperty] private bool _userNameInvalid;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _domain = "";
    [ObservableProperty] private string _errors = "";

    public bool IsNew { get; init; } = true;

    /// <summary>Field label. The asterisk marks the password as required only when creating:
    /// on edit an empty box means "keep the stored secret".</summary>
    public string PasswordLabel => IsNew ? Strings.CredEditor_PasswordRequired : Strings.CredEditor_Password;

    /// <summary>Placeholder for the password box. The stored secret is encrypted and never decrypted
    /// for display, so on edit the placeholder states the intent instead of faking masked characters.</summary>
    public string PasswordPlaceholder => IsNew
        ? Strings.CredEditor_PasswordPlaceholderNew
        : Strings.CredEditor_PasswordPlaceholderExisting;

    public bool Validate(IEnumerable<string> otherLabels)
    {
        var errors = CredentialRules.Validate(Label, UserName, otherLabels);
        Errors = string.Join("\n", errors.Select(ValidationMessages.Of));
        LabelInvalid = errors.Any(e => e is not CredentialError.UserNameRequired);
        UserNameInvalid = errors.Contains(CredentialError.UserNameRequired);
        return errors.Count == 0;
    }
}
