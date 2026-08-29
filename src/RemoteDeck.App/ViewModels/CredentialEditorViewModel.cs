using CommunityToolkit.Mvvm.ComponentModel;
using RemoteDeck.Core.Security;

namespace RemoteDeck.App.ViewModels;

/// <summary>Form state for the credential editor. Never holds the secret: the view seals it directly through the vault.</summary>
public sealed partial class CredentialEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _domain = "";
    [ObservableProperty] private string _errors = "";

    public bool IsNew { get; init; } = true;

    public bool Validate(IEnumerable<string> otherLabels)
    {
        var errors = CredentialRules.Validate(Label, UserName, otherLabels);
        Errors = string.Join("\n", errors);
        return errors.Count == 0;
    }
}
