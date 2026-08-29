# RemoteDeck — Lot 2 : coffre DPAPI et éditeur d'identifiants

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stocker des identifiants chiffrés par DPAPI, les gérer dans une UI (CRUD), et ouvrir une session RDP avec un identifiant du coffre — sans que le mot de passe n'existe jamais comme `string` managée (spec §5).

**Architecture:** `RemoteDeck.Core/Security` porte le coffre (`ICredentialVault`, `DpapiCredentialVault`) et les conversions BSTR ↔ UTF-8 (`SecretBytes`) ; sa surface publique **n'a aucun paramètre ni retour `string`** pour un secret — vérifié par un test de réflexion. L'App introduit un conteneur DI minimal (`App.Services`), deux fenêtres (`CredentialsWindow` liste + `CredentialEditorWindow` formulaire) et branche le coffre sur `RdpSessionHost.PutPassword`. `SECURITY.md` publie le modèle de menace §5.4.

**Tech Stack:** .NET 10, `System.Security.Cryptography.ProtectedData` 10.0.11, `Microsoft.Extensions.DependencyInjection` 10.0.x, `CommunityToolkit.Mvvm` 8.4.2, WPF-UI 4.3.0, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-remotedeck-design.md` — §5 (coffre, chaîne du secret, modèle de menace), §3, §7.3 (fenêtres possédées, airspace), §10.

## Global Constraints

- **Jamais de `string` managée contenant un secret**, nulle part dans `src/`. Chaîne autorisée : `PasswordBox.SecurePassword` → `Marshal.SecureStringToBSTR` → (`Seal` : BSTR → `byte[]` UTF-8 → DPAPI → blob) ou (`UseSecret` : blob → DPAPI → `byte[]` UTF-8 → `SysAllocStringLen` → BSTR → callback) → `Marshal.ZeroFreeBSTR` + `CryptographicOperations.ZeroMemory`, tout dans des `finally`. Les tests peuvent matérialiser un `string` **côté test uniquement** (`Marshal.StringToBSTR` / `PtrToStringBSTR`) pour comparer.
- DPAPI `DataProtectionScope.CurrentUser` + entropie de **32 octets aléatoires par identifiant** (`RandomNumberGenerator.GetBytes(32)`), régénérée à chaque `Seal`.
- `RemoteDeck.Core` reste `net10.0`. `ProtectedData` est Windows-only : la classe du coffre porte `[SupportedOSPlatform("windows")]` (sinon CA1416 = erreur avec `TreatWarningsAsErrors`). Aucune référence WPF/COM dans Core.
- Aucun log d'un secret, d'un blob ou d'une entropie — seulement `Label`/`UserName`/longueurs.
- Jamais de `MessageBox` : erreurs et confirmations **dans la fenêtre** (InfoBar, bouton de confirmation en deux temps).
- Les fenêtres secondaires sont des `FluentWindow` avec `Owner` = fenêtre principale (§7.3, airspace) ; `WindowStartupLocation.CenterOwner`.
- UI et code en **anglais** ; commits en anglais ; `git add` par fichier, jamais `-A`/`.` ; jamais `.superpowers/`, `docs/PROJET.md`, `bin/`, `obj/`. Commits : `git -c user.name="David Simon" -c user.email="david.simon@financieredelacite.com" commit -m "..."` + trailer `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- TDD dans Core. Warning-free.
- Baseline : `main` @ b834a80, 33 tests verts.

---

## Carte des fichiers

| Fichier | Responsabilité |
|---|---|
| `src/RemoteDeck.Core/Security/SecretBytes.cs` | BSTR ↔ `byte[]` UTF-8 sans `string`, effacement |
| `src/RemoteDeck.Core/Security/ICredentialVault.cs` | Contrat : `Seal`, `UseSecret` — aucune surface `string` |
| `src/RemoteDeck.Core/Security/DpapiCredentialVault.cs` | Implémentation DPAPI CurrentUser + entropie |
| `src/RemoteDeck.Core/Security/CredentialRules.cs` | Validation Label/UserName (pur, testable) |
| `tests/RemoteDeck.Core.Tests/Security/DpapiCredentialVaultTests.cs` | Aller-retour, entropie, surface sans string |
| `tests/RemoteDeck.Core.Tests/Security/CredentialRulesTests.cs` | Règles de validation |
| `src/RemoteDeck.App/App.xaml.cs` | Conteneur DI (`Services`) |
| `src/RemoteDeck.App/ViewModels/CredentialEditorViewModel.cs` | État du formulaire (sans le secret) |
| `src/RemoteDeck.App/Views/CredentialEditorWindow.xaml(.cs)` | Formulaire ; `PasswordBox` natif ; scelle via le coffre |
| `src/RemoteDeck.App/Views/CredentialsWindow.xaml(.cs)` | Liste + Add/Edit/Delete (confirmation en deux temps) |
| `src/RemoteDeck.App/Views/ShellWindow.xaml(.cs)` | Sélecteur d'identifiant, bouton *Credentials…*, connexion via coffre |
| `SECURITY.md`, `README.md`, spec §3/§5.2 | Modèle de menace publié, doc utilisateur, contrat aligné |

---

### Task 1: `SecretBytes`, `ICredentialVault`, `DpapiCredentialVault`, `CredentialRules` (Core, TDD)

**Files:**
- Modify: `src/RemoteDeck.Core/RemoteDeck.Core.csproj`
- Create: `src/RemoteDeck.Core/Security/SecretBytes.cs`, `ICredentialVault.cs`, `DpapiCredentialVault.cs`, `CredentialRules.cs`
- Test: `tests/RemoteDeck.Core.Tests/Security/DpapiCredentialVaultTests.cs`, `CredentialRulesTests.cs`

**Interfaces:**
- Produces:
  - `public interface ICredentialVault { void Seal(Credential credential, nint secretBstr); void UseSecret(Credential credential, Action<nint> useBstr); }`
  - `public sealed class DpapiCredentialVault : ICredentialVault` (`[SupportedOSPlatform("windows")]`)
  - `internal static class SecretBytes { static byte[] Utf8FromBstr(nint bstr); static nint BstrFromUtf8(ReadOnlySpan<byte> utf8); }`
  - `public static class CredentialRules { const int MaxLabelLength = 64; static IReadOnlyList<string> Validate(string? label, string? userName, IEnumerable<string> otherLabels); }` — retourne les messages d'erreur (vide = valide) : label requis, ≤ 64, unique (ordinal insensible à la casse) parmi `otherLabels` ; userName requis.

- [ ] **Step 1: csproj Core**

Ajouter au `ItemGroup` des paquets :

```xml
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="10.0.11" />
```

- [ ] **Step 2: Tests**

`tests/RemoteDeck.Core.Tests/Security/DpapiCredentialVaultTests.cs` :

```csharp
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Security;

namespace RemoteDeck.Core.Tests.Security;

public sealed class DpapiCredentialVaultTests
{
    private readonly DpapiCredentialVault _vault = new();

    private static Credential Make() => new()
    {
        Label = "L", UserName = "u", SecretBlob = [], Entropy = [],
    };

    /// <summary>Test-only helper: builds a native BSTR from a literal. Production code never does this.</summary>
    private static void WithBstr(string literal, Action<nint> use)
    {
        nint bstr = Marshal.StringToBSTR(literal);
        try { use(bstr); } finally { Marshal.ZeroFreeBSTR(bstr); }
    }

    [Fact]
    public void Seal_then_UseSecret_round_trips_unicode()
    {
        var c = Make();
        WithBstr("p@ss wörd — 密码", b => _vault.Seal(c, b));

        string? seen = null;
        _vault.UseSecret(c, b => seen = Marshal.PtrToStringBSTR(b));

        Assert.Equal("p@ss wörd — 密码", seen);
    }

    [Fact]
    public void Seal_sets_32_byte_entropy_and_a_non_empty_blob()
    {
        var c = Make();

        WithBstr("x", b => _vault.Seal(c, b));

        Assert.Equal(32, c.Entropy.Length);
        Assert.NotEmpty(c.SecretBlob);
    }

    [Fact]
    public void Same_secret_sealed_twice_gives_different_blobs_and_entropy()
    {
        var a = Make();
        var b = Make();
        WithBstr("same", x => _vault.Seal(a, x));
        WithBstr("same", x => _vault.Seal(b, x));

        Assert.NotEqual(a.Entropy, b.Entropy);
        Assert.NotEqual(a.SecretBlob, b.SecretBlob);
    }

    [Fact]
    public void Wrong_entropy_fails_to_unprotect()
    {
        var c = Make();
        WithBstr("secret", b => _vault.Seal(c, b));
        c.Entropy = new byte[32];

        Assert.Throws<CryptographicException>(() => _vault.UseSecret(c, _ => { }));
    }

    [Fact]
    public void Empty_secret_round_trips()
    {
        var c = Make();
        WithBstr("", b => _vault.Seal(c, b));

        int? length = null;
        _vault.UseSecret(c, b => length = Marshal.ReadInt32(b, -4));

        Assert.Equal(0, length);
    }

    [Fact]
    public void Vault_surface_has_no_string_parameter_or_return()
    {
        var methods = typeof(ICredentialVault).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(methods);
        Assert.All(methods, m =>
        {
            Assert.NotEqual(typeof(string), m.ReturnType);
            Assert.All(m.GetParameters(), p => Assert.NotEqual(typeof(string), p.ParameterType));
        });
    }
}
```

`tests/RemoteDeck.Core.Tests/Security/CredentialRulesTests.cs` :

```csharp
using RemoteDeck.Core.Security;

namespace RemoteDeck.Core.Tests.Security;

public sealed class CredentialRulesTests
{
    [Fact]
    public void Valid_input_has_no_errors()
        => Assert.Empty(CredentialRules.Validate("Admin", "admin", ["Other"]));

    [Fact]
    public void Label_and_user_are_required()
    {
        var errors = CredentialRules.Validate("  ", "", []);

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Label_must_be_unique_case_insensitively()
        => Assert.Single(CredentialRules.Validate("admin", "u", ["ADMIN"]));

    [Fact]
    public void Label_is_limited_to_64_characters()
        => Assert.Single(CredentialRules.Validate(new string('a', 65), "u", []));
}
```

- [ ] **Step 3: RED** — `dotnet test tests/RemoteDeck.Core.Tests` → échec de compilation.

- [ ] **Step 4: Implémenter**

`src/RemoteDeck.Core/Security/SecretBytes.cs` :

```csharp
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDeck.Core.Security;

/// <summary>
/// Conversions between a native BSTR and UTF-8 bytes that never create a managed string.
/// Every intermediate buffer is zeroed in a finally block.
/// </summary>
internal static class SecretBytes
{
    /// <summary>Copies the BSTR's UTF-16 payload into UTF-8 bytes. Caller zeroes the result.</summary>
    public static byte[] Utf8FromBstr(nint bstr)
    {
        if (bstr == 0) throw new ArgumentException("BSTR must not be null.", nameof(bstr));
        int chars = Marshal.ReadInt32(bstr, -4) / 2;   // BSTR length prefix is in bytes
        var buffer = new char[chars];
        Marshal.Copy(bstr, buffer, 0, chars);
        try
        {
            return Encoding.UTF8.GetBytes(buffer);
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    /// <summary>Allocates a BSTR from UTF-8 bytes. Caller frees it with <see cref="Marshal.ZeroFreeBSTR"/>.</summary>
    public static nint BstrFromUtf8(ReadOnlySpan<byte> utf8)
    {
        var chars = new char[Encoding.UTF8.GetCharCount(utf8)];
        var handle = GCHandle.Alloc(chars, GCHandleType.Pinned);
        try
        {
            Encoding.UTF8.GetChars(utf8, chars);
            nint bstr = SysAllocStringLen(handle.AddrOfPinnedObject(), (uint)chars.Length);
            if (bstr == 0) throw new OutOfMemoryException("SysAllocStringLen failed.");
            return bstr;
        }
        finally
        {
            Array.Clear(chars);
            handle.Free();
        }
    }

    public static void Zero(byte[] bytes) => CryptographicOperations.ZeroMemory(bytes);

    [DllImport("oleaut32.dll")]
    private static extern nint SysAllocStringLen(nint source, uint length);
}
```

`src/RemoteDeck.Core/Security/ICredentialVault.cs` :

```csharp
using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Security;

/// <summary>
/// Encrypts and lends secrets. By design no member accepts or returns a <see cref="string"/>:
/// secrets travel as native BSTRs owned by the caller (spec §5.2).
/// </summary>
public interface ICredentialVault
{
    /// <summary>Encrypts the BSTR's content into <paramref name="credential"/> (new entropy + blob). The caller keeps ownership of the BSTR.</summary>
    void Seal(Credential credential, nint secretBstr);

    /// <summary>Decrypts the secret into a native BSTR lent to <paramref name="useBstr"/>, then zeroes and frees it.</summary>
    void UseSecret(Credential credential, Action<nint> useBstr);
}
```

`src/RemoteDeck.Core/Security/DpapiCredentialVault.cs` :

```csharp
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Security;

/// <summary>
/// Windows DPAPI, CurrentUser scope, plus 32 bytes of per-credential entropy (spec §5.1).
/// The database file alone is useless without the Windows profile; two identical secrets
/// produce different blobs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialVault : ICredentialVault
{
    private const int EntropyLength = 32;

    public void Seal(Credential credential, nint secretBstr)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var entropy = RandomNumberGenerator.GetBytes(EntropyLength);
        var utf8 = SecretBytes.Utf8FromBstr(secretBstr);
        try
        {
            credential.SecretBlob = ProtectedData.Protect(utf8, entropy, DataProtectionScope.CurrentUser);
            credential.Entropy = entropy;
        }
        finally
        {
            SecretBytes.Zero(utf8);
        }
    }

    public void UseSecret(Credential credential, Action<nint> useBstr)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(useBstr);
        var utf8 = ProtectedData.Unprotect(credential.SecretBlob, credential.Entropy, DataProtectionScope.CurrentUser);
        try
        {
            nint bstr = SecretBytes.BstrFromUtf8(utf8);
            try
            {
                useBstr(bstr);
            }
            finally
            {
                Marshal.ZeroFreeBSTR(bstr);
            }
        }
        finally
        {
            SecretBytes.Zero(utf8);
        }
    }
}
```

`src/RemoteDeck.Core/Security/CredentialRules.cs` :

```csharp
namespace RemoteDeck.Core.Security;

/// <summary>Validation rules for the credential editor. Pure; messages are UI-ready English.</summary>
public static class CredentialRules
{
    public const int MaxLabelLength = 64;

    public static IReadOnlyList<string> Validate(string? label, string? userName, IEnumerable<string> otherLabels)
    {
        var errors = new List<string>();
        var trimmed = label?.Trim() ?? "";
        if (trimmed.Length == 0) errors.Add("Label is required.");
        else if (trimmed.Length > MaxLabelLength) errors.Add($"Label must be at most {MaxLabelLength} characters.");
        else if (otherLabels.Any(o => string.Equals(o, trimmed, StringComparison.OrdinalIgnoreCase))) errors.Add("A credential with this label already exists.");
        if (string.IsNullOrWhiteSpace(userName)) errors.Add("User name is required.");
        return errors;
    }
}
```

- [ ] **Step 5: GREEN** — `dotnet test tests/RemoteDeck.Core.Tests` → 43 tests verts (33 + 6 + 4). `dotnet build RemoteDeck.sln` 0 avertissement.

- [ ] **Step 6: Commit**

```bash
git add src/RemoteDeck.Core/RemoteDeck.Core.csproj src/RemoteDeck.Core/Security/SecretBytes.cs src/RemoteDeck.Core/Security/ICredentialVault.cs src/RemoteDeck.Core/Security/DpapiCredentialVault.cs src/RemoteDeck.Core/Security/CredentialRules.cs tests/RemoteDeck.Core.Tests/Security/DpapiCredentialVaultTests.cs tests/RemoteDeck.Core.Tests/Security/CredentialRulesTests.cs
git commit -m "feat(core): DPAPI credential vault with a string-free surface, and credential rules"
```

---

### Task 2: Conteneur DI dans l'App

**Files:**
- Modify: `src/RemoteDeck.App/RemoteDeck.App.csproj`, `src/RemoteDeck.App/App.xaml.cs`

**Interfaces:**
- Produces: `public IServiceProvider Services { get; }` sur `App` ; `public static App Current => (App)System.Windows.Application.Current;`. Enregistrements : `SqliteDatabase` (singleton, `DefaultPath()`), `CredentialRepository`, `ConnectionRepository` (singletons — sans état), `ICredentialVault` → `DpapiCredentialVault` (singleton). `DatabaseReady` conservé.

- [ ] **Step 1: Paquets App**

```xml
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.*" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
```

(`10.0.*` : dernière 10.0.x disponible ; consigner la version résolue dans le rapport.)

- [ ] **Step 2: `App.xaml.cs`**

Remplacer la classe (conserver les deux commentaires du lot 0) :

```csharp
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Security;

namespace RemoteDeck.App;

public partial class App : System.Windows.Application
{
    public static new App Current => (App)System.Windows.Application.Current;

    /// <summary>Composition root. Repositories and the vault are stateless singletons.</summary>
    public IServiceProvider Services { get; private set; } = new ServiceCollection().BuildServiceProvider();

    public SqliteDatabase? Database { get; private set; }
    public bool DatabaseReady { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ProbeLog.Write("startup", $"RemoteDeck starting, log at {ProbeLog.Path}");
        try
        {
            Database = new SqliteDatabase(SqliteDatabase.DefaultPath());
            Database.EnsureCreated();
            DatabaseReady = true;
            ProbeLog.Write("startup", $"Database ready at {Database.Path}, schema v{SchemaMigrator.CurrentVersion}");
        }
        catch (SchemaTooNewException ex)
        {
            ProbeLog.Write("startup", $"Database not opened: {ex.Message}");
        }
        catch (Exception ex)
        {
            ProbeLog.Write("startup", $"Database initialisation failed: {ex.GetType().Name}: {ex.Message}");
        }

        var services = new ServiceCollection();
        if (Database is not null && DatabaseReady)
        {
            services.AddSingleton(Database);
            services.AddSingleton<CredentialRepository>();
            services.AddSingleton<ConnectionRepository>();
        }
        services.AddSingleton<ICredentialVault, DpapiCredentialVault>();
        Services = services.BuildServiceProvider();
    }
}
```

Les repositories ne sont enregistrés que si la base est prête : un `GetService<CredentialRepository>()` retourne alors `null`, et l'UI désactive les fonctions correspondantes au lieu de planter.

- [ ] **Step 3: Build, lancer (~10 s), log `Database ready`, WM_CLOSE exit 0. Commit**

```bash
git add src/RemoteDeck.App/RemoteDeck.App.csproj src/RemoteDeck.App/App.xaml.cs
git commit -m "feat(app): minimal DI container exposing database, repositories and vault"
```

---

### Task 3: `CredentialEditorViewModel`, `CredentialEditorWindow`, `CredentialsWindow`

**Files:**
- Create: `src/RemoteDeck.App/ViewModels/CredentialEditorViewModel.cs`, `src/RemoteDeck.App/Views/CredentialEditorWindow.xaml(.cs)`, `src/RemoteDeck.App/Views/CredentialsWindow.xaml(.cs)`

**Interfaces:**
- Consumes: `App.Current.Services` (`CredentialRepository`, `ICredentialVault`), `CredentialRules.Validate`.
- Produces:
  - `CredentialEditorViewModel : ObservableObject` — `Label`, `UserName`, `Domain`, `Errors` (string, joint par `\n`), `IsNew`, `bool Validate(IEnumerable<string> otherLabels)`.
  - `CredentialEditorWindow(Credential? existing)` — `ShowDialog()`; `bool Saved { get; }`. Le mot de passe est saisi dans un `PasswordBox` natif ; **en édition, un champ vide = mot de passe inchangé**.
  - `CredentialsWindow()` — liste (`ListView`), boutons *Add*, *Edit*, *Delete* → *Confirm delete*, InfoBar pour les erreurs. `ShowDialog()`.

- [ ] **Step 1: ViewModel**

```csharp
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
```

- [ ] **Step 2: `CredentialEditorWindow.xaml`**

```xml
<ui:FluentWindow x:Class="RemoteDeck.App.Views.CredentialEditorWindow"
                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
                 Title="Credential" Width="440" SizeToContent="Height"
                 ExtendsContentIntoTitleBar="True" WindowBackdropType="Mica" WindowCornerPreference="Round"
                 WindowStartupLocation="CenterOwner" ResizeMode="NoResize" ShowInTaskbar="False">
    <StackPanel Margin="0,0,0,12">
        <ui:TitleBar Title="Credential" ShowMinimize="False" ShowMaximize="False" />
        <Grid Margin="16,4,16,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="110" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" /><RowDefinition Height="Auto" /><RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" /><RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <TextBlock Grid.Row="0" Text="Label" VerticalAlignment="Center" />
            <ui:TextBox Grid.Row="0" Grid.Column="1" x:Name="LabelInput" Margin="0,4" Text="{Binding Label, UpdateSourceTrigger=PropertyChanged}" />
            <TextBlock Grid.Row="1" Text="User name" VerticalAlignment="Center" />
            <ui:TextBox Grid.Row="1" Grid.Column="1" Margin="0,4" Text="{Binding UserName, UpdateSourceTrigger=PropertyChanged}" />
            <TextBlock Grid.Row="2" Text="Domain" VerticalAlignment="Center" />
            <ui:TextBox Grid.Row="2" Grid.Column="1" Margin="0,4" PlaceholderText="optional" Text="{Binding Domain, UpdateSourceTrigger=PropertyChanged}" />
            <TextBlock Grid.Row="3" Text="Password" VerticalAlignment="Center" />
            <!-- Native PasswordBox on purpose: the only control that keeps the secret out of managed strings. -->
            <PasswordBox Grid.Row="3" Grid.Column="1" x:Name="PasswordInput" Margin="0,4" />
            <TextBlock Grid.Row="4" Grid.Column="1" x:Name="PasswordHint" Margin="0,0,0,4" Opacity="0.7" FontSize="12"
                       Text="Leave empty to keep the current password." />
        </Grid>
        <ui:InfoBar x:Name="ErrorBar" Margin="16,8,16,0" Severity="Error" IsOpen="False" IsClosable="False"
                    Message="{Binding Errors}" />
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="16,12,16,0">
            <ui:Button Content="Cancel" Margin="0,0,8,0" IsCancel="True" Click="OnCancel" />
            <ui:Button Content="Save" Appearance="Primary" IsDefault="True" Click="OnSave" />
        </StackPanel>
    </StackPanel>
</ui:FluentWindow>
```

- [ ] **Step 3: `CredentialEditorWindow.xaml.cs`**

```csharp
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

public partial class CredentialEditorWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly CredentialRepository _repository;
    private readonly ICredentialVault _vault;
    private readonly Credential? _existing;
    private readonly CredentialEditorViewModel _vm;

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
        bool hasPassword = PasswordInput.SecurePassword.Length > 0;
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
            using var secure = PasswordInput.SecurePassword;
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
```

- [ ] **Step 4: `CredentialsWindow.xaml`**

```xml
<ui:FluentWindow x:Class="RemoteDeck.App.Views.CredentialsWindow"
                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
                 Title="Credentials" Width="520" Height="420" MinWidth="420" MinHeight="300"
                 ExtendsContentIntoTitleBar="True" WindowBackdropType="Mica" WindowCornerPreference="Round"
                 WindowStartupLocation="CenterOwner" ShowInTaskbar="False">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" /><RowDefinition Height="Auto" /><RowDefinition Height="*" /><RowDefinition Height="Auto" />
        </Grid.RowDefinitions>
        <ui:TitleBar Grid.Row="0" Title="Credentials" ShowMinimize="False" ShowMaximize="False" />
        <ui:InfoBar Grid.Row="1" x:Name="StatusBar" Margin="16,4,16,0" IsOpen="False" IsClosable="True" />
        <ListView Grid.Row="2" x:Name="List" Margin="16,8" SelectionMode="Single" SelectionChanged="OnSelectionChanged" MouseDoubleClick="OnEdit">
            <ListView.View>
                <GridView>
                    <GridViewColumn Header="Label" Width="200" DisplayMemberBinding="{Binding Label}" />
                    <GridViewColumn Header="User" Width="140" DisplayMemberBinding="{Binding UserName}" />
                    <GridViewColumn Header="Domain" Width="120" DisplayMemberBinding="{Binding Domain}" />
                </GridView>
            </ListView.View>
        </ListView>
        <StackPanel Grid.Row="3" Orientation="Horizontal" Margin="16,0,16,16">
            <ui:Button Content="Add" Appearance="Primary" Margin="0,0,8,0" Click="OnAdd" />
            <ui:Button x:Name="EditButton" Content="Edit" Margin="0,0,8,0" IsEnabled="False" Click="OnEdit" />
            <ui:Button x:Name="DeleteButton" Content="Delete" IsEnabled="False" Click="OnDelete" />
        </StackPanel>
    </Grid>
</ui:FluentWindow>
```

- [ ] **Step 5: `CredentialsWindow.xaml.cs`**

```csharp
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

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
        DeleteButton.Content = "Delete";
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
            DeleteButton.Content = "Delete";
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
            DeleteButton.Content = "Confirm delete";
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Warning, "Delete credential?",
                $"'{c.Label}' will be removed; connections using it will need a new credential. Click again to confirm.");
            return;
        }
        try
        {
            _repository.Delete(c.Id);
            ProbeLog.Write("vault", $"Credential '{c.Label}' deleted");
            StatusBar.IsOpen = false;
            Reload();
        }
        catch (Exception ex)
        {
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error, "Delete failed", ex.Message);
        }
    }

    private void ShowStatus(Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
```

- [ ] **Step 6: Build warning-free ; commit**

```bash
git add src/RemoteDeck.App/ViewModels/CredentialEditorViewModel.cs src/RemoteDeck.App/Views/CredentialEditorWindow.xaml src/RemoteDeck.App/Views/CredentialEditorWindow.xaml.cs src/RemoteDeck.App/Views/CredentialsWindow.xaml src/RemoteDeck.App/Views/CredentialsWindow.xaml.cs
git commit -m "feat(app): credentials management windows (list, editor, two-step delete)"
```

---

### Task 4: Connexion depuis le coffre dans `ShellWindow`

**Files:**
- Modify: `src/RemoteDeck.App/Views/ShellWindow.xaml`, `src/RemoteDeck.App/Views/ShellWindow.xaml.cs`

**Interfaces:**
- Consumes: `App.Current.Services` (`CredentialRepository?`, `ICredentialVault`), `RdpSessionHost.Configure/PutPassword/Connect`, `RdpConnectionProbeSettings`.
- Produces: un `ComboBox` `CredentialInput` (premier élément « Type credentials manually », puis les `Credential` par `Label`) et un bouton *Credentials…* ; à *Connect*, si un identifiant est sélectionné : `UserName`/`Domain` viennent de l'identifiant, le mot de passe est prêté par `vault.UseSecret(credential, bstr => _session.PutPassword(bstr))`, log `[vault] password supplied from credential '<label>'` ; sinon chemin manuel inchangé.

- [ ] **Step 1: XAML** — dans le `WrapPanel` de la barre, **avant** `UserInput` :

```xml
            <ComboBox x:Name="CredentialInput" Width="200" Margin="0,0,8,0" DisplayMemberPath="Label" SelectionChanged="OnCredentialChanged" />
            <ui:Button Content="Credentials…" Margin="0,0,8,0" Click="OnManageCredentials" />
```

- [ ] **Step 2: Code-behind**

Champs :

```csharp
private CredentialRepository? _credentials;
private ICredentialVault? _vault;
private static readonly Credential ManualEntry = new() { Label = "Type credentials manually", UserName = "", SecretBlob = [], Entropy = [] };
```

Dans `OnLoaded`, avant la sélection du contrôle RDP :

```csharp
_credentials = App.Current.Services.GetService<CredentialRepository>();
_vault = App.Current.Services.GetService<ICredentialVault>();
ReloadCredentials();
```

Méthodes :

```csharp
private void ReloadCredentials()
{
    var items = new List<Credential> { ManualEntry };
    if (_credentials is not null) items.AddRange(_credentials.GetAll());
    var selectedId = (CredentialInput.SelectedItem as Credential)?.Id;
    CredentialInput.ItemsSource = items;
    CredentialInput.SelectedItem = items.FirstOrDefault(c => c.Id == selectedId && c.Id != 0) ?? ManualEntry;
}

private void OnCredentialChanged(object sender, SelectionChangedEventArgs e)
{
    bool manual = ReferenceEquals(CredentialInput.SelectedItem, ManualEntry) || CredentialInput.SelectedItem is null;
    UserInput.IsEnabled = manual;
    DomainInput.IsEnabled = manual;
    PasswordInput.IsEnabled = manual;
    if (!manual && CredentialInput.SelectedItem is Credential c)
    {
        UserInput.Text = c.UserName;
        DomainInput.Text = c.Domain ?? "";
        PasswordInput.Clear();
    }
}

private void OnManageCredentials(object sender, RoutedEventArgs e)
{
    if (_credentials is null)
    {
        ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Warning, "Database unavailable", "Credentials cannot be managed until the database opens.");
        return;
    }
    new CredentialsWindow { Owner = this }.ShowDialog();
    ReloadCredentials();
}
```

Dans `OnConnectClick`, remplacer le bloc `if (!settings.UseWebAccount) { … }` par :

```csharp
if (!settings.UseWebAccount)
{
    if (CredentialInput.SelectedItem is Credential stored && !ReferenceEquals(stored, ManualEntry) && _vault is not null)
    {
        // Vault path: DPAPI blob -> UTF-8 bytes -> native BSTR lent to the control -> zeroed. No managed string.
        _vault.UseSecret(stored, bstr => _session.PutPassword(bstr));
        ProbeLog.Write("vault", $"Password supplied from credential '{stored.Label}'");
    }
    else
    {
        // Manual path (lot 0): SecureString -> native BSTR -> IDispatch-free vtable put -> zero+free.
        using var secure = PasswordInput.SecurePassword;
        nint bstr = Marshal.SecureStringToBSTR(secure);
        try { _session.PutPassword(bstr); ProbeLog.Write("R1", "ClearTextPassword set through IMsTscNonScriptable vtable with a native BSTR"); }
        finally { Marshal.ZeroFreeBSTR(bstr); }
        PasswordInput.Clear();
    }
}
```

(`UserInput.Text`/`DomainInput.Text` sont déjà remplis par `OnCredentialChanged`, donc `settings` reste construit comme avant.)

- [ ] **Step 3: Build, lancer, vérifier (sans connexion) : la combo liste « Type credentials manually » + les identifiants ; *Credentials…* ouvre la fenêtre ; créer un identifiant de test (`label=test`, user `x`, mot de passe `y`), il apparaît dans la combo ; le supprimer (deux clics). Log : lignes `[vault] Credential 'test' created (secret sealed)` puis `deleted`. WM_CLOSE exit 0.**

Le scénario **humain** : créer l'identifiant réel de FDC-VM-WIN07 (anonymisé dans tout document), le sélectionner, *Connect* → `Logged on` + `[vault] Password supplied from credential '…'`.

- [ ] **Step 4: Commit**

```bash
git add src/RemoteDeck.App/Views/ShellWindow.xaml src/RemoteDeck.App/Views/ShellWindow.xaml.cs
git commit -m "feat(app): connect with a vault credential from the shell"
```

---

### Task 5: `SECURITY.md`, README, spec

**Files:**
- Create: `SECURITY.md`
- Modify: `README.md`, `docs/superpowers/specs/2026-08-29-remotedeck-design.md` (§3 fichiers Security/, §5.2 noms `Seal`/`UseSecret`)

- [ ] **Step 1: `SECURITY.md`** (anglais) — sections : *Supported versions* (main only, pre-1.0) ; *How credentials are stored* (DPAPI CurrentUser, 32-byte per-credential entropy, SQLite blob, no key in the binary, secrets never exist as managed strings — BSTR lent to the RDP control and zeroed) ; *Threat model — what is covered* (stolen `.db`, backups, other Windows accounts on the machine) ; *What is NOT covered* (malware running in your unlocked session can call DPAPI exactly as RemoteDeck does; local administrators; memory access to the process; the RDP control itself receives the plaintext, as any RDP client must) ; *Other notes* (the app installs a process-wide low-level keyboard hook to catch Ctrl+K/Ctrl+Tab while the remote session has focus — it never records keystrokes; source: `ShortcutInterceptor.cs`) ; *Reporting* (GitHub private vulnerability reporting on the repository; no public issue for security bugs).
- [ ] **Step 2: README** — section *Credentials* (how it works, 3 lines, link to SECURITY.md) ; remplacer « SECURITY.md (to be published with v1) » par le lien réel.
- [ ] **Step 3: Spec** — §3 : ajouter `Security/ SecretBytes, ICredentialVault, DpapiCredentialVault, CredentialRules` ; §5.2 : la méthode s'appelle `UseSecret(Credential, Action<nint>)` et `Seal(Credential, nint)` (pas `UseSecret(long, …)`) ; §12 : L2 fait.
- [ ] **Step 4: Commit**

```bash
git add SECURITY.md README.md docs/superpowers/specs/2026-08-29-remotedeck-design.md
git commit -m "docs: security policy and threat model, credentials section, spec aligned with the vault API"
```

---

## Auto-revue du plan

**Couverture spec** : §5.1 stockage DPAPI + entropie (T1) ; §5.2 chaîne du secret sans string, vérifiée par test de réflexion (T1) et respectée dans les deux fenêtres (T3/T4) ; §5.3 logs sans secret (T3/T4) ; §5.4 publié (T5) ; CRUD identifiants (T3) ; réutilisation N connexions = déjà le modèle L1 ; fin de lot = « connexion réussie avec un identifiant du coffre » (T4, sonde humaine). Hors lot : liste des connexions/recherche (L3), reconnexion avec re-fourniture du secret (L4 — `UseSecret` est réentrant par construction).

**Types** : `ICredentialVault.Seal(Credential, nint)` / `UseSecret(Credential, Action<nint>)` consommés en T3 (`Seal`) et T4 (`UseSecret` avec `_session.PutPassword`) ; `CredentialRules.Validate(string?, string?, IEnumerable<string>)` en T3 ; `App.Current.Services` en T3/T4 ; comptes de tests 33 → 43.

**Points d'attention** : CA1416 sur `ProtectedData` (attribut `SupportedOSPlatform` sur la classe **et** vérifier que l'analyseur n'exige pas l'attribut sur les appelants — les tests et l'App sont Windows : si CA1416 remonte dans `App`, ajouter `<SupportedOSPlatformVersion>` / l'attribut sur `App`) ; `Marshal.Copy(nint, char[], int, int)` existe ; `GCHandle` pinned pour `SysAllocStringLen` évite `unsafe` dans Core ; `PasswordBox.SecurePassword` retourne une copie à disposer (`using var`).
