# RemoteDeck — Lot 0 : squelette, interop RDP et sondes de risque

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produire le squelette des trois projets, embarquer le contrôle ActiveX RDP dans une `FluentWindow`, ouvrir et fermer proprement une session RDP saisie à la main, et **lever les sept risques R1–R7** de la spec par des sondes instrumentées dont les résultats sont consignés.

**Architecture:** `RemoteDeck.Core` (net10.0, aucune référence WPF/COM) + `RemoteDeck.App` (net10.0-windows, WPF + WinForms pour `AxHost`) + `RemoteDeck.Core.Tests` (xUnit). Le contrôle `mstscax.dll` est instancié par un `AxHost` maison à partir d'un CLSID choisi dans un catalogue ordonné ; les interfaces viennent d'un `COMReference` généré au build. Le lot ne livre presque aucune fonctionnalité : il **dérisque**.

**Tech Stack:** .NET SDK 10.0.400, C# nullable, WPF, `System.Windows.Forms.AxHost`, `COMReference MSTSCLib`, WPF-UI 4.3.0, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-remotedeck-design.md` — le plan argumente depuis la spec ; l'exécutant lit les deux.

## Global Constraints

- Cible `net10.0` (Core) / `net10.0-windows` (App). Aucun .NET 8 sur le poste (D1).
- `RemoteDeck.Core` ne référence **ni WPF, ni Windows Forms, ni COM** — vérifié par le compilateur (§3).
- Dépendances autorisées en v1 : `Microsoft.Data.Sqlite`, `System.Security.Cryptography.ProtectedData`, `Microsoft.Extensions.DependencyInjection`, `CommunityToolkit.Mvvm`, `WPF-UI` 4.3.0 (+ xUnit en test). **Le lot 0 n'en ajoute qu'une : `WPF-UI`.** YAGNI.
- Code, commentaires, UI, README, messages de commit : **anglais** (D8). Les documents `docs/superpowers/` restent en français.
- Un secret n'existe **jamais** comme `string` managée (D5, §5.2). Dans ce lot, la seule source de mot de passe est un `PasswordBox` WPF → `SecureStringToBSTR` → `BSTR` natif → `ZeroFreeBSTR`.
- Aucune valeur d'énumération tierce n'est devinée. Valeurs vérifiées pour ce lot :
  - Typelib `MSTSCLib` : `{8C11EFA1-92C3-11D1-BC1E-00C04FA31489}` v1.0 (registre local).
  - CLSID « version 13 » : `{3F859AA3-C2D4-4FAA-B0E4-FD0C9C4E5E3A}` ; « 12 » : `{1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8}` ; « 11 » : `{A0C63C30-F08D-4AB4-907C-34905D770C7D}` ; « 10 » : `{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}` (registre local + doc MS).
  - `KeyboardHookMode` : 0 = combinaisons locales, 1 = distantes, 2 = distantes en plein écran seulement (défaut). Source : <https://learn.microsoft.com/windows/win32/termserv/imsrdpclientsecuredsettings-keyboardhookmode>
  - `AuthenticationLevel` : 0 = pas d'auth serveur, 1 = requise, 2 = tentative + invite (Source : <https://learn.microsoft.com/windows/win32/termserv/imsrdpclientadvancedsettings4-authenticationlevel>)
  - `ControlCloseStatus` : `controlCloseCanProceed` = 0, `controlCloseWaitForEvents` = 1 (Source : <https://learn.microsoft.com/windows/win32/termserv/imsrdpclient-requestclose>)
  - `IID_IMsTscNonScriptable` = `c1e6743a-41c1-4a74-832a-0dd06c1c7a0e` (page MS de l'interface).
- Jamais de `MessageBox` pour une erreur de session : `InfoBar` dans la fenêtre (§6.4).
- `git add` **par fichier** — jamais `git add -A` (un `docs/PROJET.md` généré traîne à la racine, ignoré par `.gitignore`).
- Commit après chaque tâche. Messages en anglais, préfixe `feat:`/`test:`/`chore:`/`docs:`.

---

## Carte des fichiers

| Fichier | Responsabilité |
|---|---|
| `RemoteDeck.sln` | Solution (format `.sln` classique — compatible VS 2022 et CI) |
| `Directory.Build.props` | Nullable, ImplicitUsings, LangVersion, warnings = erreurs |
| `LICENSE`, `README.md` | MIT ; présentation minimale + avertissement SmartScreen |
| `.github/workflows/ci.yml` | Build + tests sur `windows-latest` |
| `src/RemoteDeck.Core/Rdp/RdpControlCatalog.cs` | Liste ordonnée des CLSID candidats ; choix du premier enregistré (pur, testable) |
| `src/RemoteDeck.App/app.manifest` | DPI PerMonitorV2 (R3) |
| `src/RemoteDeck.App/Interop/RdpAxHost.cs` | `AxHost` instanciant le CLSID ; expose l'OCX |
| `src/RemoteDeck.App/Interop/ClsidRegistry.cs` | `IsRegistered(Guid)` via `HKCR\CLSID\{…}\InprocServer32` |
| `src/RemoteDeck.App/Interop/ComSecretPut.cs` | Affectation de `ClearTextPassword` par `IDispatch::Invoke` avec un `BSTR` natif (R1) |
| `src/RemoteDeck.App/Rdp/RdpSessionHost.cs` | Façade : configuration, connexion, événements, fermeture propre (§6.5) |
| `src/RemoteDeck.App/Rdp/ShortcutInterceptor.cs` | Interception `Ctrl+K` / `Ctrl+Tab` en amont du contrôle (R6) |
| `src/RemoteDeck.App/Services/ProbeLog.cs` | Journal des sondes `%APPDATA%\RemoteDeck\logs\probe-l0.log` |
| `src/RemoteDeck.App/Views/ShellWindow.xaml(.cs)` | `FluentWindow`, barre de sonde, `InfoBar`, `WindowsFormsHost` |
| `tests/RemoteDeck.Core.Tests/Rdp/RdpControlCatalogTests.cs` | Tests du catalogue |
| `docs/superpowers/probes/l0-probe-results.md` | Résultats observés R1–R7 |
| `docs/manual-checklist.md` | Check-list manuelle (§10) — items du lot 0 |

---

### Task 1: Squelette de solution, propriétés communes, licence, CI

**Files:**
- Create: `RemoteDeck.sln`, `Directory.Build.props`, `LICENSE`, `README.md`, `.github/workflows/ci.yml`
- Create (via template) : `src/RemoteDeck.Core/RemoteDeck.Core.csproj`, `src/RemoteDeck.App/RemoteDeck.App.csproj`, `tests/RemoteDeck.Core.Tests/RemoteDeck.Core.Tests.csproj`

**Interfaces:**
- Produces: les trois projets référencés dans la solution ; `App → Core`, `Tests → Core`.

- [ ] **Step 1: Créer la solution et les projets**

Depuis `C:\Users\david.simon\source\repos\remotedeck` :

```powershell
dotnet new sln -n RemoteDeck -f sln
dotnet new classlib -n RemoteDeck.Core -o src/RemoteDeck.Core -f net10.0
dotnet new wpf      -n RemoteDeck.App  -o src/RemoteDeck.App  -f net10.0
dotnet new xunit    -n RemoteDeck.Core.Tests -o tests/RemoteDeck.Core.Tests -f net10.0
dotnet sln RemoteDeck.sln add src/RemoteDeck.Core src/RemoteDeck.App tests/RemoteDeck.Core.Tests
dotnet add src/RemoteDeck.App reference src/RemoteDeck.Core
dotnet add tests/RemoteDeck.Core.Tests reference src/RemoteDeck.Core
Remove-Item src/RemoteDeck.Core/Class1.cs
Remove-Item tests/RemoteDeck.Core.Tests/UnitTest1.cs
```

- [ ] **Step 2: Écrire `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <Authors>RemoteDeck contributors</Authors>
    <Copyright>MIT License</Copyright>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Remplacer `src/RemoteDeck.Core/RemoteDeck.Core.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>RemoteDeck.Core</RootNamespace>
  </PropertyGroup>
</Project>
```

Aucune référence Windows : c'est la frontière du §3, vérifiée par le compilateur.

- [ ] **Step 4: Remplacer `src/RemoteDeck.App/RemoteDeck.App.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>RemoteDeck.App</RootNamespace>
    <AssemblyName>RemoteDeck</AssemblyName>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="WPF-UI" Version="4.3.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\RemoteDeck.Core\RemoteDeck.Core.csproj" />
  </ItemGroup>
</Project>
```

`UseWindowsForms` est requis par `AxHost` et `WindowsFormsHost`. `AllowUnsafeBlocks` sert à `ComSecretPut` (Task 5). Le `COMReference` est ajouté en Task 3, pas ici — on valide d'abord que le squelette compile seul.

**Piège de noms** : avec WPF et WinForms activés ensemble, `Application`, `MessageBox`, `Button`, `TextBox` existent dans les deux espaces de noms. Dans le code-behind, ne jamais faire `using System.Windows.Forms;` — qualifier `System.Windows.Forms.Application` en toutes lettres.

- [ ] **Step 5: Créer `src/RemoteDeck.App/app.manifest`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="0.1.0.0" name="RemoteDeck" />
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <!-- R3: per-monitor DPI so the RDP surface is rendered crisp on mixed-DPI setups -->
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/PM</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10 and 11 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
</assembly>
```

- [ ] **Step 6: Remplacer `tests/RemoteDeck.Core.Tests/RemoteDeck.Core.Tests.csproj`**

Conserver les `PackageReference` xUnit générés par le template (versions du SDK), et s'assurer que le fichier contient :

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <IsPackable>false</IsPackable>
  <IsTestProject>true</IsTestProject>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="..\..\src\RemoteDeck.Core\RemoteDeck.Core.csproj" />
</ItemGroup>
```

- [ ] **Step 7: Écrire `LICENSE` (MIT)**

```text
MIT License

Copyright (c) 2026 RemoteDeck contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

- [ ] **Step 8: Écrire `README.md`**

```markdown
# RemoteDeck

A keyboard-first Remote Desktop (RDP) connection manager for Windows 10/11.
Tabs, groups, fuzzy search, a command palette, and a credential vault backed by
Windows DPAPI — built on the native Remote Desktop ActiveX control, so the RDP
protocol itself is Microsoft's, not ours.

> Status: **pre-alpha**. Lot 0 (skeleton and risk probes) in progress.

## Requirements

- Windows 10 20H2+ or Windows 11
- .NET 10 SDK to build

## Build

    dotnet build RemoteDeck.sln
    dotnet test  RemoteDeck.sln

## Security

RemoteDeck stores credentials encrypted with Windows DPAPI, bound to your
Windows user session. See `SECURITY.md` (to be published with v1) for the threat
model — including what DPAPI does **not** protect against.

## SmartScreen

Release binaries are not code-signed. Windows SmartScreen will warn on first
launch: choose *More info* → *Run anyway*. Signing will be reconsidered once
the project has users.

## License

MIT — see `LICENSE`.
```

- [ ] **Step 9: Écrire `.github/workflows/ci.yml`**

```yaml
name: ci

on:
  push:
    branches: [main]
  pull_request:

jobs:
  build:
    # COMReference MSTSCLib needs mstscax.dll's type library registered: Windows only.
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - run: dotnet restore RemoteDeck.sln
      - run: dotnet build RemoteDeck.sln --configuration Release --no-restore
      - run: dotnet test RemoteDeck.sln --configuration Release --no-build
```

- [ ] **Step 10: Build et test à vide**

Run: `dotnet build RemoteDeck.sln`
Expected: `Build succeeded.` avec 0 avertissement (warnings = erreurs).

Run: `dotnet test RemoteDeck.sln`
Expected: succès, 0 test (le projet est vide, c'est voulu).

- [ ] **Step 11: Commit**

```bash
git add RemoteDeck.sln Directory.Build.props LICENSE README.md .github/workflows/ci.yml
git add src/RemoteDeck.Core/RemoteDeck.Core.csproj
git add src/RemoteDeck.App/RemoteDeck.App.csproj src/RemoteDeck.App/app.manifest src/RemoteDeck.App/App.xaml src/RemoteDeck.App/App.xaml.cs src/RemoteDeck.App/MainWindow.xaml src/RemoteDeck.App/MainWindow.xaml.cs src/RemoteDeck.App/AssemblyInfo.cs
git add tests/RemoteDeck.Core.Tests/RemoteDeck.Core.Tests.csproj
git commit -m "chore: solution skeleton (Core, App, Tests), MIT license, CI"
```

(`MainWindow.xaml` du template est remplacé en Task 4 ; on le commite tel quel pour que le squelette tourne.)

---

### Task 2: `RdpControlCatalog` — choix du CLSID (Core, TDD)

**Files:**
- Create: `src/RemoteDeck.Core/Rdp/RdpControlCatalog.cs`
- Test: `tests/RemoteDeck.Core.Tests/Rdp/RdpControlCatalogTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record RdpControlVersion(Guid Clsid, string Label)`
  - `public static class RdpControlCatalog`
    - `public static IReadOnlyList<RdpControlVersion> Candidates { get; }` — du plus récent au plus ancien
    - `public static RdpControlVersion? Select(Func<Guid, bool> isRegistered)` — premier candidat enregistré, `null` sinon

Pourquoi dans Core : la logique (« ordre de préférence, premier disponible ») est pure. Le test de présence registre (`isRegistered`) est injecté — App fournit l'implémentation Windows en Task 3.

- [ ] **Step 1: Écrire les tests**

`tests/RemoteDeck.Core.Tests/Rdp/RdpControlCatalogTests.cs` :

```csharp
using RemoteDeck.Core.Rdp;

namespace RemoteDeck.Core.Tests.Rdp;

public sealed class RdpControlCatalogTests
{
    [Fact]
    public void Candidates_are_ordered_newest_first()
    {
        var labels = RdpControlCatalog.Candidates.Select(c => c.Label).ToArray();

        Assert.Equal(new[] { "13", "12", "11", "10" }, labels);
    }

    [Fact]
    public void Candidates_have_distinct_clsids()
    {
        var clsids = RdpControlCatalog.Candidates.Select(c => c.Clsid).ToArray();

        Assert.Equal(clsids.Length, clsids.Distinct().Count());
    }

    [Fact]
    public void Select_returns_newest_registered_candidate()
    {
        var v12 = RdpControlCatalog.Candidates[1].Clsid;
        var v10 = RdpControlCatalog.Candidates[3].Clsid;

        var chosen = RdpControlCatalog.Select(g => g == v12 || g == v10);

        Assert.NotNull(chosen);
        Assert.Equal("12", chosen.Label);
    }

    [Fact]
    public void Select_returns_null_when_nothing_is_registered()
    {
        Assert.Null(RdpControlCatalog.Select(_ => false));
    }

    [Fact]
    public void Select_stops_probing_after_first_match()
    {
        var probed = new List<Guid>();

        RdpControlCatalog.Select(g => { probed.Add(g); return true; });

        Assert.Single(probed);
    }
}
```

- [ ] **Step 2: Vérifier que ça échoue**

Run: `dotnet test tests/RemoteDeck.Core.Tests`
Expected: échec de compilation — `RemoteDeck.Core.Rdp` n'existe pas.

- [ ] **Step 3: Implémenter**

`src/RemoteDeck.Core/Rdp/RdpControlCatalog.cs` :

```csharp
namespace RemoteDeck.Core.Rdp;

/// <summary>One registered flavour of the Remote Desktop ActiveX control (mstscax.dll).</summary>
/// <param name="Clsid">CLSID of the <c>MsRdpClientNNotSafeForScripting</c> coclass.</param>
/// <param name="Label">Registry label suffix ("Microsoft RDP Client Control - version {Label}").</param>
public sealed record RdpControlVersion(Guid Clsid, string Label);

/// <summary>
/// Ordered list of known control CLSIDs, newest first, and selection of the first one
/// registered on the host. CLSIDs verified against the local registry and
/// https://learn.microsoft.com/windows/win32/termserv/using-remote-desktop-web-connection
/// </summary>
public static class RdpControlCatalog
{
    public static IReadOnlyList<RdpControlVersion> Candidates { get; } =
    [
        new(new Guid("3F859AA3-C2D4-4FAA-B0E4-FD0C9C4E5E3A"), "13"), // Windows 11 / Server 2022+
        new(new Guid("1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8"), "12"),
        new(new Guid("A0C63C30-F08D-4AB4-907C-34905D770C7D"), "11"),
        new(new Guid("8B918B82-7985-4C24-89DF-C33AD2BBFBCD"), "10"), // Windows 8.1 / Server 2012 R2
    ];

    /// <summary>Returns the newest candidate for which <paramref name="isRegistered"/> is true, or null.</summary>
    public static RdpControlVersion? Select(Func<Guid, bool> isRegistered)
    {
        ArgumentNullException.ThrowIfNull(isRegistered);
        foreach (var candidate in Candidates)
        {
            if (isRegistered(candidate.Clsid))
            {
                return candidate;
            }
        }
        return null;
    }
}
```

- [ ] **Step 4: Vérifier que ça passe**

Run: `dotnet test tests/RemoteDeck.Core.Tests`
Expected: 5 tests, tous verts.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteDeck.Core/Rdp/RdpControlCatalog.cs tests/RemoteDeck.Core.Tests/Rdp/RdpControlCatalogTests.cs
git commit -m "feat(core): RDP control catalog with newest-first CLSID selection"
```

---

### Task 3: `COMReference MSTSCLib`, `RdpAxHost`, `ClsidRegistry`, `ProbeLog`

**Files:**
- Modify: `src/RemoteDeck.App/RemoteDeck.App.csproj`
- Create: `src/RemoteDeck.App/Interop/RdpAxHost.cs`, `src/RemoteDeck.App/Interop/ClsidRegistry.cs`, `src/RemoteDeck.App/Services/ProbeLog.cs`

**Interfaces:**
- Consumes: `RdpControlCatalog.Select`, `RdpControlVersion`
- Produces:
  - `internal sealed class RdpAxHost : System.Windows.Forms.AxHost` — `RdpAxHost(RdpControlVersion version)`, `object Ocx { get; }` (lève `InvalidOperationException` si l'OCX n'est pas encore créé), `RdpControlVersion Version { get; }`
  - `internal static class ClsidRegistry` — `bool IsRegistered(Guid clsid)`
  - `internal static class ProbeLog` — `void Write(string probe, string message)`, `string Path { get; }`

- [ ] **Step 1: Ajouter le `COMReference` au csproj**

Dans `src/RemoteDeck.App/RemoteDeck.App.csproj`, ajouter après le `ItemGroup` des `PackageReference` :

```xml
  <ItemGroup>
    <!-- Generated at build time by tlbimp from the registered type library. No binary is committed. -->
    <COMReference Include="MSTSCLib">
      <Guid>{8C11EFA1-92C3-11D1-BC1E-00C04FA31489}</Guid>
      <VersionMajor>1</VersionMajor>
      <VersionMinor>0</VersionMinor>
      <Lcid>0</Lcid>
      <WrapperTool>tlbimp</WrapperTool>
      <Isolated>false</Isolated>
      <EmbedInteropTypes>false</EmbedInteropTypes>
    </COMReference>
  </ItemGroup>
```

`EmbedInteropTypes=false` : l'assembly `Interop.MSTSCLib.dll` est générée dans `obj/` et copiée dans la sortie. C'est le choix sûr pour les interfaces d'événements (`*_Event`) — l'embarquement des types d'événements COM est le cas historiquement fragile.

- [ ] **Step 2: Build pour générer l'interop et vérifier les noms**

Run: `dotnet build src/RemoteDeck.App`
Expected: `Build succeeded.` et présence de `src/RemoteDeck.App/obj/Debug/net10.0-windows/Interop.MSTSCLib.dll` (chemin exact à lire dans la sortie du build).

Puis, inventaire des membres réellement générés — les tâches suivantes en dépendent (signatures d'événements, `RequestClose`, `set_Property`). Avec `pwsh` (PowerShell 7) :

```powershell
$dll = Get-ChildItem -Recurse src/RemoteDeck.App/obj -Filter Interop.MSTSCLib.dll | Select-Object -First 1
$asm = [System.Reflection.Assembly]::LoadFile($dll.FullName)
"--- IMsTscAxEvents_Event ---"
$asm.GetType('MSTSCLib.IMsTscAxEvents_Event').GetEvents() |
  ForEach-Object { '{0,-40} {1}' -f $_.Name, $_.EventHandlerType.GetMethod('Invoke') }
"--- IMsTscNonScriptable.ClearTextPassword ---"
$asm.GetType('MSTSCLib.IMsTscNonScriptable').GetProperty('ClearTextPassword').SetMethod
"--- IMsRdpExtendedSettings ---"
$asm.GetType('MSTSCLib.IMsRdpExtendedSettings').GetMethods() | ForEach-Object { $_.ToString() }
"--- IMsRdpClient10.RequestClose / GetErrorDescription ---"
$asm.GetType('MSTSCLib.IMsRdpClient10').GetMethod('RequestClose')
$asm.GetType('MSTSCLib.IMsRdpClient10').GetMethod('GetErrorDescription')
```

Si `pwsh` n'est pas installé, ouvrir `RemoteDeck.sln` dans Visual Studio → *Object Browser* → `Interop.MSTSCLib` et lire les mêmes membres. Consigner les signatures observées dans `docs/superpowers/probes/l0-probe-results.md` (créé en Task 9 ; noter dans un fichier de brouillon en attendant). Attendu d'après l'IDL :
- `OnDisconnected` handler `(int discReason)` ; `OnFatalError` `(int errorCode)` ; `OnLogonError` `(int lError)` ; `OnConfirmClose` handler **retournant `bool`** ; `OnConnected`, `OnConnecting`, `OnLoginComplete`, `OnAuthenticationWarningDisplayed`, `OnAuthenticationWarningDismissed` sans paramètre.
- `ClearTextPassword` setter : `Void set_ClearTextPassword(System.String)` — **c'est précisément R1** : le setter typé exige une `string` managée, on ne l'utilisera pas.
- `IMsRdpExtendedSettings` : `set_Property(String, Object ByRef)` / `get_Property(String)`.
- `RequestClose(ControlCloseStatus ByRef)` (paramètre `out`) ; `String GetErrorDescription(UInt32, UInt32)`.

Si une signature diffère, **adapter le code des tâches 5–8 à la signature générée** (la typelib fait foi) et le noter dans les résultats de sonde.

- [ ] **Step 3: Écrire `Services/ProbeLog.cs`**

```csharp
using System.IO;

namespace RemoteDeck.App.Services;

/// <summary>
/// Append-only diagnostic log for lot-0 probes. Never receives secrets: callers log
/// outcomes and codes, not inputs.
/// </summary>
internal static class ProbeLog
{
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteDeck", "logs", "probe-l0.log");

    public static void Write(string probe, string message)
    {
        var line = $"{DateTime.UtcNow:O} [{probe}] {message}";
        lock (Gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, line + Environment.NewLine);
        }
        System.Diagnostics.Debug.WriteLine(line);
    }
}
```

- [ ] **Step 4: Écrire `Interop/ClsidRegistry.cs`**

```csharp
using Microsoft.Win32;

namespace RemoteDeck.App.Interop;

/// <summary>Registry-backed implementation of the "is this CLSID usable here?" predicate.</summary>
internal static class ClsidRegistry
{
    public static bool IsRegistered(Guid clsid)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid:B}\InprocServer32");
        return key?.GetValue(null) is string path && path.Length > 0;
    }
}
```

- [ ] **Step 5: Écrire `Interop/RdpAxHost.cs`**

```csharp
using System.Windows.Forms;
using RemoteDeck.Core.Rdp;

namespace RemoteDeck.App.Interop;

/// <summary>
/// Hosts one instance of the Remote Desktop ActiveX control. The OCX is created when the
/// Win32 handle is created (AxHost semantics): call <see cref="Control.CreateControl"/> or
/// parent the host before touching <see cref="Ocx"/>.
/// </summary>
internal sealed class RdpAxHost : AxHost
{
    public RdpControlVersion Version { get; }

    public RdpAxHost(RdpControlVersion version) : base(version.Clsid.ToString("D"))
    {
        Version = version;
        Dock = DockStyle.Fill;
    }

    /// <summary>The raw COM object. Cast it to the MSTSCLib interface you need.</summary>
    public object Ocx => GetOcx()
        ?? throw new InvalidOperationException("The RDP control has not been created yet (no window handle).");
}
```

`AxHost(string clsid)` attend le GUID **sans accolades** (format `"D"`), comme le font les classes générées par AxImp.

- [ ] **Step 6: Build**

Run: `dotnet build src/RemoteDeck.App`
Expected: `Build succeeded.` — 0 avertissement.

- [ ] **Step 7: Commit**

```bash
git add src/RemoteDeck.App/RemoteDeck.App.csproj src/RemoteDeck.App/Interop/RdpAxHost.cs src/RemoteDeck.App/Interop/ClsidRegistry.cs src/RemoteDeck.App/Services/ProbeLog.cs
git commit -m "feat(app): MSTSCLib COM reference, AxHost wrapper, CLSID registry check, probe log"
```

---

### Task 4: `ShellWindow` Fluent + `WindowsFormsHost` (sondes R3, R4)

**Files:**
- Delete: `src/RemoteDeck.App/MainWindow.xaml`, `src/RemoteDeck.App/MainWindow.xaml.cs`
- Modify: `src/RemoteDeck.App/App.xaml`, `src/RemoteDeck.App/App.xaml.cs`
- Create: `src/RemoteDeck.App/Views/ShellWindow.xaml`, `src/RemoteDeck.App/Views/ShellWindow.xaml.cs`

**Interfaces:**
- Consumes: `RdpAxHost`, `ClsidRegistry.IsRegistered`, `RdpControlCatalog.Select`, `ProbeLog.Write`
- Produces: une fenêtre qui affiche le contrôle RDP (non connecté) dans une `FluentWindow` Mica. Les champs `HostInput`, `PortInput`, `UserInput`, `DomainInput`, `PasswordInput`, `WebAccountInput`, `ConnectButton`, `DisconnectButton`, `StatusBar` (InfoBar), `RdpHost` (WindowsFormsHost) sont utilisés par les tâches 5–8.

- [ ] **Step 1: Remplacer `App.xaml`**

```xml
<Application x:Class="RemoteDeck.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             StartupUri="Views/ShellWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ui:ThemesDictionary Theme="Dark" />
                <ui:ControlsDictionary />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 2: Remplacer `App.xaml.cs`**

```csharp
using System.Windows;
using RemoteDeck.App.Services;

namespace RemoteDeck.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ProbeLog.Write("startup", $"RemoteDeck starting, log at {ProbeLog.Path}");
    }
}
```

- [ ] **Step 3: Supprimer `MainWindow.xaml` et `MainWindow.xaml.cs`**

```powershell
git rm src/RemoteDeck.App/MainWindow.xaml src/RemoteDeck.App/MainWindow.xaml.cs
```

- [ ] **Step 4: Écrire `Views/ShellWindow.xaml`**

```xml
<ui:FluentWindow x:Class="RemoteDeck.App.Views.ShellWindow"
                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
                 Title="RemoteDeck"
                 Width="1280" Height="800"
                 MinWidth="800" MinHeight="500"
                 ExtendsContentIntoTitleBar="True"
                 WindowBackdropType="Mica"
                 WindowCornerPreference="Round"
                 WindowStartupLocation="CenterScreen">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <ui:TitleBar Grid.Row="0" Title="RemoteDeck" />

        <!-- Lot 0 probe toolbar. Replaced by the connection pane in lot 3. -->
        <WrapPanel Grid.Row="1" Margin="12,4" ItemHeight="36">
            <ui:TextBox x:Name="HostInput" Width="220" Margin="0,0,8,0" PlaceholderText="host" />
            <ui:NumberBox x:Name="PortInput" Width="90" Margin="0,0,8,0" Value="3389" Minimum="1" Maximum="65535" SpinButtonPlacementMode="Hidden" />
            <ui:TextBox x:Name="UserInput" Width="160" Margin="0,0,8,0" PlaceholderText="user" />
            <ui:TextBox x:Name="DomainInput" Width="120" Margin="0,0,8,0" PlaceholderText="domain (optional)" />
            <ui:PasswordBox x:Name="PasswordInput" Width="160" Margin="0,0,8,0" PlaceholderText="password" />
            <CheckBox x:Name="WebAccountInput" Margin="0,0,8,0" VerticalAlignment="Center" Content="Use web account (experimental)" />
            <ui:Button x:Name="ConnectButton" Margin="0,0,8,0" Appearance="Primary" Content="Connect" IsDefault="True" Click="OnConnectClick" />
            <ui:Button x:Name="DisconnectButton" Content="Disconnect" IsEnabled="False" Click="OnDisconnectClick" />
        </WrapPanel>

        <ui:InfoBar Grid.Row="2" x:Name="StatusBar" Margin="12,0,12,4" IsOpen="False" IsClosable="True" />

        <Border Grid.Row="3" Margin="12,0,12,12" CornerRadius="6" Background="#FF000000">
            <WindowsFormsHost x:Name="RdpHost" />
        </Border>
    </Grid>
</ui:FluentWindow>
```

`WindowsFormsHost` vit dans l'espace de noms `System.Windows.Forms.Integration`, mappé automatiquement dans le xmlns WPF par défaut quand `UseWindowsForms` est actif.

- [ ] **Step 5: Écrire `Views/ShellWindow.xaml.cs` (version Task 4 — sans connexion)**

```csharp
using System.Windows;
using RemoteDeck.App.Interop;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Rdp;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

public partial class ShellWindow : Wpf.Ui.Controls.FluentWindow
{
    private RdpAxHost? _ax;

    public ShellWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        HostInput.Text = Environment.GetEnvironmentVariable("REMOTEDECK_PROBE_HOST") ?? "";
        UserInput.Text = Environment.GetEnvironmentVariable("REMOTEDECK_PROBE_USER") ?? "";
        DomainInput.Text = Environment.GetEnvironmentVariable("REMOTEDECK_PROBE_DOMAIN") ?? "";

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var version = RdpControlCatalog.Select(ClsidRegistry.IsRegistered);
        if (version is null)
        {
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error, "No Remote Desktop control found",
                "None of the known mstscax.dll CLSIDs is registered on this machine.");
            ConnectButton.IsEnabled = false;
            return;
        }

        _ax = new RdpAxHost(version);
        RdpHost.Child = _ax;
        _ax.CreateControl();

        var dpi = VisualTreeHelper.GetDpi(this);
        ProbeLog.Write("R4", $"FluentWindow + WindowsFormsHost created; control version {version.Label} ({version.Clsid:D})");
        ProbeLog.Write("R3", $"Window DPI scale X={dpi.DpiScaleX:F2} Y={dpi.DpiScaleY:F2}");

        ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Informational, $"RDP control v{version.Label} ready", "Enter a host and press Connect.");
    }

    private void ShowStatus(Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        // Wired in Task 5.
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        // Wired in Task 5.
    }
}
```

`VisualTreeHelper` requiert `using System.Windows.Media;` — l'ajouter en tête.

- [ ] **Step 6: Build et lancer**

Run: `dotnet build src/RemoteDeck.App && dotnet run --project src/RemoteDeck.App`
Expected :
- La fenêtre s'ouvre avec barre de titre intégrée et fond Mica (Windows 11) — coins arrondis, thème sombre.
- L'InfoBar affiche « RDP control v13 ready ».
- La zone noire en bas héberge le contrôle (surface vide, c'est normal : pas connecté).
- `%APPDATA%\RemoteDeck\logs\probe-l0.log` contient les lignes `R4` et `R3`.

**Sonde R4 (à consigner)** : la barre de titre custom réagit-elle correctement (déplacer, double-clic maximise, boutons système) **alors que le `WindowsFormsHost` est visible** ? Redimensionner par les bords : l'hôte suit-il sans artefact ? Si la barre de titre ou le redimensionnement se bloque → repli spec §13 R4 (`Window` standard + styles maison), à noter.

**Sonde R3 (à consigner)** : avec deux écrans à DPI différents (ex. 100 % et 150 %), déplacer la fenêtre de l'un à l'autre. Le contrôle doit se redessiner net, sans flou ni décalage. Noter les valeurs `DpiScale` journalisées.

- [ ] **Step 7: Commit**

```bash
git add src/RemoteDeck.App/App.xaml src/RemoteDeck.App/App.xaml.cs src/RemoteDeck.App/Views/ShellWindow.xaml src/RemoteDeck.App/Views/ShellWindow.xaml.cs
git commit -m "feat(app): Fluent shell window hosting the RDP control (probes R3, R4)"
```

---

### Task 5: `ComSecretPut` (R1) + `RdpSessionHost` : connexion et événements (R2)

**Files:**
- Create: `src/RemoteDeck.App/Interop/ComSecretPut.cs`, `src/RemoteDeck.App/Rdp/RdpSessionHost.cs`, `src/RemoteDeck.App/Rdp/RdpConnectionProbeSettings.cs`
- Modify: `src/RemoteDeck.App/Views/ShellWindow.xaml.cs`

**Interfaces:**
- Consumes: `RdpAxHost.Ocx`, `ProbeLog.Write`
- Produces:
  - `internal sealed record RdpConnectionProbeSettings(string Host, int Port, string UserName, string? Domain, bool UseWebAccount)`
  - `internal static unsafe class ComSecretPut` — `static void PutClearTextPassword(object ocx, nint bstr)`
  - `internal sealed class RdpSessionHost : IDisposable`
    - `RdpSessionHost(RdpAxHost host)`
    - `void Configure(RdpConnectionProbeSettings settings, int desktopWidth, int desktopHeight)`
    - `void PutPassword(nint bstr)` — délègue à `ComSecretPut`
    - `void Connect()` / `void Disconnect()`
    - `bool IsConnected { get; }`
    - `event Action<string>? StatusChanged` — texte court d'état
    - `event Action<RdpDisconnectInfo>? Disconnected`
  - `internal sealed record RdpDisconnectInfo(int Reason, int ExtendedReason, string Description)`

- [ ] **Step 1: Écrire `Rdp/RdpConnectionProbeSettings.cs`**

```csharp
namespace RemoteDeck.App.Rdp;

/// <summary>Everything needed to open one session except the secret, which never travels as a string.</summary>
internal sealed record RdpConnectionProbeSettings(
    string Host,
    int Port,
    string UserName,
    string? Domain,
    bool UseWebAccount);
```

- [ ] **Step 2: Écrire `Interop/ComSecretPut.cs`**

Pourquoi ce fichier existe : l'interop généré expose `ClearTextPassword` comme `set_ClearTextPassword(string)`. Passer par ce setter obligerait à matérialiser le mot de passe en `string` managée — exactement ce que D5 interdit. On appelle donc `IDispatch::Invoke` de l'interface `IMsTscNonScriptable` directement, avec un `VARIANT` `VT_BSTR` pointant sur le `BSTR` natif que l'appelant possède et efface.

```csharp
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace RemoteDeck.App.Interop;

/// <summary>
/// Sets <c>IMsTscNonScriptable::ClearTextPassword</c> from a native BSTR through raw
/// <c>IDispatch::Invoke</c>, so the secret is never materialised as a managed string (spec D5, R1).
/// The caller owns the BSTR and must <see cref="Marshal.ZeroFreeBSTR"/> it afterwards.
/// </summary>
internal static unsafe class ComSecretPut
{
    // IID from https://learn.microsoft.com/windows/win32/termserv/imstscnonscriptable-interface
    private static readonly Guid IidIMsTscNonScriptable = new("c1e6743a-41c1-4a74-832a-0dd06c1c7a0e");

    private const ushort DispatchPropertyPut = 0x4;   // DISPATCH_PROPERTYPUT
    private const int DispidPropertyPut = -3;         // DISPID_PROPERTYPUT
    private const ushort VtBstr = 8;                  // VT_BSTR
    private const int VariantSize = 24;               // sizeof(VARIANT) on x64 (16 on x86; 24 is safe for both)

    public static void PutClearTextPassword(object ocx, nint bstr)
    {
        ArgumentNullException.ThrowIfNull(ocx);
        if (bstr == 0) throw new ArgumentException("BSTR must not be null.", nameof(bstr));

        nint unknown = Marshal.GetIUnknownForObject(ocx);
        try
        {
            Guid iid = IidIMsTscNonScriptable;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint dispatch));
            try
            {
                // IMsTscNonScriptable is a dual interface: vtable = IUnknown(3) + IDispatch(4) + members.
                nint* vtable = *(nint**)dispatch;
                var getIdsOfNames = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, uint, uint, int*, int>)vtable[5];
                var invoke = (delegate* unmanaged[Stdcall]<nint, int, Guid*, uint, ushort, DISPPARAMS*, nint, nint, nint, int>)vtable[6];

                Guid nil = Guid.Empty;
                int dispId;
                nint name = Marshal.StringToCoTaskMemUni("ClearTextPassword");
                try
                {
                    Marshal.ThrowExceptionForHR(getIdsOfNames(dispatch, &nil, &name, 1, 0, &dispId));
                }
                finally
                {
                    Marshal.FreeCoTaskMem(name);
                }

                // VARIANT layout: vt (ushort) at offset 0, union payload at offset 8.
                byte* variant = stackalloc byte[VariantSize];
                new Span<byte>(variant, VariantSize).Clear();
                *(ushort*)variant = VtBstr;
                *(nint*)(variant + 8) = bstr;

                int namedArg = DispidPropertyPut;
                var parameters = new DISPPARAMS
                {
                    rgvarg = (nint)variant,
                    rgdispidNamedArgs = (nint)(&namedArg),
                    cArgs = 1,
                    cNamedArgs = 1,
                };

                Marshal.ThrowExceptionForHR(invoke(dispatch, dispId, &nil, 0, DispatchPropertyPut, &parameters, 0, 0, 0));
            }
            finally
            {
                Marshal.Release(dispatch);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
```

Le `VARIANT` ne possède pas le `BSTR` : `Invoke` le copie côté contrôle (sémantique `[in]`). Rien à libérer ici ; l'appelant efface le sien.

- [ ] **Step 3: Écrire `Rdp/RdpSessionHost.cs`**

```csharp
using MSTSCLib;
using RemoteDeck.App.Interop;
using RemoteDeck.App.Services;

namespace RemoteDeck.App.Rdp;

internal sealed record RdpDisconnectInfo(int Reason, int ExtendedReason, string Description);

/// <summary>
/// Thin façade over one RDP control instance: configuration, connect/disconnect, event
/// forwarding. State machine and reconnect policy arrive in lot 4; this is the probe-grade version.
/// </summary>
internal sealed class RdpSessionHost : IDisposable
{
    private readonly RdpAxHost _host;
    private readonly IMsRdpClient10 _client;
    private readonly IMsTscAxEvents_Event _events;
    private bool _disposed;

    public event Action<string>? StatusChanged;
    public event Action<RdpDisconnectInfo>? Disconnected;

    public bool IsConnected => _client.Connected != 0;

    public RdpSessionHost(RdpAxHost host)
    {
        _host = host;
        _client = (IMsRdpClient10)host.Ocx;
        _events = (IMsTscAxEvents_Event)host.Ocx;

        // R2 probe: does subscribing to the COM event interface work through COMReference?
        _events.OnConnecting += () => Raise("Connecting…");
        _events.OnConnected += () => Raise("Connected");
        _events.OnLoginComplete += () => Raise("Logged on");
        _events.OnAuthenticationWarningDisplayed += () => ProbeLog.Write("R5", "OnAuthenticationWarningDisplayed fired (certificate warning shown by the control)");
        _events.OnAuthenticationWarningDismissed += () => ProbeLog.Write("R5", "OnAuthenticationWarningDismissed fired");
        _events.OnLogonError += error => ProbeLog.Write("session", $"OnLogonError lError={error}");
        _events.OnFatalError += code => ProbeLog.Write("session", $"OnFatalError errorCode={code}");
        _events.OnDisconnected += OnDisconnected;
        ProbeLog.Write("R2", "Subscribed to IMsTscAxEvents_Event via COMReference interop");
    }

    public void Configure(RdpConnectionProbeSettings settings, int desktopWidth, int desktopHeight)
    {
        _client.Server = settings.Host;
        _client.UserName = settings.UserName;
        _client.Domain = settings.Domain ?? string.Empty;
        _client.DesktopWidth = desktopWidth;
        _client.DesktopHeight = desktopHeight;
        _client.ColorDepth = 32;

        var advanced = _client.AdvancedSettings9;      // IMsRdpClientAdvancedSettings8, inherits all previous
        advanced.RDPPort = settings.Port;
        advanced.EnableCredSspSupport = true;
        advanced.RedirectClipboard = true;             // spec §2 default: clipboard on, everything else off
        advanced.RedirectDrives = false;
        advanced.RedirectPrinters = false;
        advanced.AuthenticationLevel = 2;              // attempt + prompt (verified value, see plan constraints)
        advanced.SmartSizing = false;

        // Windowed use: Windows key combos stay local unless full screen (documented value 2 = default).
        _client.SecuredSettings2.KeyboardHookMode = 2;

        if (settings.UseWebAccount)
        {
            TryEnableWebAccount();
        }
    }

    /// <summary>R7 probe. Property name is NOT in Microsoft's documented list; we only observe.</summary>
    private void TryEnableWebAccount()
    {
        try
        {
            var extended = (IMsRdpExtendedSettings)_host.Ocx;
            object value = true;
            extended.set_Property("EnableRdsAadAuth", ref value);
            ProbeLog.Write("R7", "set_Property(\"EnableRdsAadAuth\", true) returned without error");
        }
        catch (Exception ex)
        {
            ProbeLog.Write("R7", $"set_Property(\"EnableRdsAadAuth\") failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    public void PutPassword(nint bstr) => ComSecretPut.PutClearTextPassword(_host.Ocx, bstr);

    public void Connect()
    {
        Raise("Connect requested");
        _client.Connect();
    }

    public void Disconnect()
    {
        if (IsConnected)
        {
            _client.Disconnect();
        }
    }

    private void OnDisconnected(int reason)
    {
        int extended = (int)_client.ExtendedDisconnectReason;
        string description;
        try
        {
            description = _client.GetErrorDescription((uint)reason, (uint)extended);
        }
        catch (Exception ex)
        {
            description = $"(GetErrorDescription failed: {ex.Message})";
        }
        ProbeLog.Write("session", $"OnDisconnected reason={reason} extended={extended} \"{description}\"");
        Disconnected?.Invoke(new RdpDisconnectInfo(reason, extended, description));
    }

    private void Raise(string status)
    {
        ProbeLog.Write("session", status);
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _events.OnDisconnected -= OnDisconnected;
    }
}
```

Points à ajuster **contre l'interop généré** (Task 3, Step 2) si le compilateur proteste : le type de `Connected` (`short`), la présence de `AdvancedSettings9` sur `IMsRdpClient10` (héritée de `IMsRdpClient8`), la forme `set_Property(string, ref object)`, le type de `ExtendedDisconnectReason` (enum `ExtendedDisconnectReasonCode`, d'où le cast). La typelib fait foi ; noter tout écart dans les résultats de sonde.

- [ ] **Step 4: Câbler la fenêtre**

Dans `Views/ShellWindow.xaml.cs` :

Ajouter les `using` :

```csharp
using System.Runtime.InteropServices;
using RemoteDeck.App.Rdp;
```

Ajouter le champ :

```csharp
private RdpSessionHost? _session;
```

À la fin de `OnLoaded` (après `_ax.CreateControl();`), créer la session :

```csharp
_session = new RdpSessionHost(_ax);
_session.StatusChanged += status => Dispatcher.Invoke(() =>
    ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Informational, status, ""));
_session.Disconnected += info => Dispatcher.Invoke(() =>
{
    ConnectButton.IsEnabled = true;
    DisconnectButton.IsEnabled = false;
    var severity = info.Reason == 3 // disconnectReasonByServer: not an error (spec §6.4)
        ? Wpf.Ui.Controls.InfoBarSeverity.Informational
        : Wpf.Ui.Controls.InfoBarSeverity.Error;
    ShowStatus(severity, $"Disconnected (reason {info.Reason}, extended {info.ExtendedReason})", info.Description);
});
```

Remplacer les deux gestionnaires :

```csharp
private void OnConnectClick(object sender, RoutedEventArgs e)
{
    if (_session is null || _ax is null) return;

    var settings = new RdpConnectionProbeSettings(
        Host: HostInput.Text.Trim(),
        Port: (int)(PortInput.Value ?? 3389),
        UserName: UserInput.Text.Trim(),
        Domain: string.IsNullOrWhiteSpace(DomainInput.Text) ? null : DomainInput.Text.Trim(),
        UseWebAccount: WebAccountInput.IsChecked == true);

    if (settings.Host.Length == 0)
    {
        ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Warning, "Host required", "Enter a host name or address.");
        return;
    }

    var dpi = VisualTreeHelper.GetDpi(this);
    int width = Math.Max(640, (int)(RdpHost.ActualWidth * dpi.DpiScaleX));
    int height = Math.Max(480, (int)(RdpHost.ActualHeight * dpi.DpiScaleY));

    try
    {
        _session.Configure(settings, width, height);

        if (!settings.UseWebAccount)
        {
            // R1 probe: SecureString -> native BSTR -> IDispatch put -> zero+free. No managed string.
            nint bstr = Marshal.SecureStringToBSTR(PasswordInput.SecurePassword);
            try
            {
                _session.PutPassword(bstr);
                ProbeLog.Write("R1", "ClearTextPassword set through IDispatch::Invoke with a native BSTR");
            }
            finally
            {
                Marshal.ZeroFreeBSTR(bstr);
            }
            PasswordInput.Password = string.Empty;
        }

        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = true;
        _session.Connect();
    }
    catch (Exception ex)
    {
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        ProbeLog.Write("session", $"Connect failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error, "Connect failed", $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
    }
}

private void OnDisconnectClick(object sender, RoutedEventArgs e)
{
    _session?.Disconnect();
}
```

Remarque sur `PasswordInput.Password = string.Empty` : `PasswordBox` de WPF-UI expose `Password` en `string` pour le binding — on ne le **lit** jamais, on le vide. La source du secret est `SecurePassword`. Si `Wpf.Ui.Controls.PasswordBox` n'expose pas `SecurePassword`, remplacer le contrôle par le `PasswordBox` WPF natif (`<PasswordBox x:Name="PasswordInput" …/>`) — il l'expose toujours.

- [ ] **Step 5: Build et sonde de connexion réelle**

Run: `dotnet build src/RemoteDeck.App && dotnet run --project src/RemoteDeck.App`

Scénarios à exécuter et consigner (tous dans `probe-l0.log`) :

1. **Connexion nominale** vers une VM de test avec utilisateur/domaine/mot de passe valides → attendu : InfoBar « Connecting… » puis « Connected » puis « Logged on », le bureau distant s'affiche dans la zone. **R1 validé** si la ligne `[R1]` est présente et que l'ouverture de session réussit (le mot de passe est bien arrivé). **R2 validé** si les lignes `Connecting…/Connected/Logged on` apparaissent (les événements COM arrivent).
2. **Mot de passe faux** → attendu : `OnLogonError` journalisé, ou invite CredSSP du contrôle ; pas de crash.
3. **Hôte inexistant** (`nonexistent.invalid`) → attendu : `OnDisconnected` avec un code DNS (260 ou 1288 ou 520 d'après la doc) et un `Description` non vide, InfoBar en `Error`.
4. **Déconnexion volontaire** par le bouton → attendu : `OnDisconnected reason=…`, boutons réarmés.
5. **Compte web** (case cochée, sans mot de passe) vers un poste Entra-joined en le nommant par son **hostname** (pas d'IP) → observer : flux web comme mstsc ? erreur ? ligne `[R7]` dans le log. **C'est une sonde, l'échec est un résultat.**

**Résultats R1 attendus si tout va bien** : aucune `string` contenant le mot de passe n'a été créée côté managé. Vérification : chercher `ClearTextPassword` dans le projet → seule occurrence dans `ComSecretPut` (chaîne du **nom de propriété**, pas du secret) et dans les commentaires.

- [ ] **Step 6: Commit**

```bash
git add src/RemoteDeck.App/Interop/ComSecretPut.cs src/RemoteDeck.App/Rdp/RdpSessionHost.cs src/RemoteDeck.App/Rdp/RdpConnectionProbeSettings.cs src/RemoteDeck.App/Views/ShellWindow.xaml.cs
git commit -m "feat(app): RDP session host with COM events and native-BSTR password (probes R1, R2, R7)"
```

---

### Task 6: Fermeture propre `RequestClose` (§6.5)

**Files:**
- Modify: `src/RemoteDeck.App/Rdp/RdpSessionHost.cs`, `src/RemoteDeck.App/Views/ShellWindow.xaml.cs`

**Interfaces:**
- Produces: `Task RdpSessionHost.CloseAsync(TimeSpan timeout)` — met en œuvre le protocole `RequestClose` → attente `OnDisconnected`/`OnConfirmClose` → repli `Disconnect()`.

- [ ] **Step 1: Ajouter `CloseAsync` à `RdpSessionHost`**

Ajouter les champs et l'abonnement dans le constructeur :

```csharp
private TaskCompletionSource? _closed;
```

Dans le constructeur, après `_events.OnDisconnected += OnDisconnected;` :

```csharp
// RequestClose contract: if the user is logged on, the control asks before closing.
// Returning true lets it disconnect; OnDisconnected then completes the close.
_events.OnConfirmClose += () => { ProbeLog.Write("close", "OnConfirmClose → allowing"); return true; };
```

Dans `OnDisconnected`, en dernière ligne :

```csharp
_closed?.TrySetResult();
```

Ajouter la méthode :

```csharp
/// <summary>
/// Graceful shutdown per IMsRdpClient::RequestClose:
/// controlCloseCanProceed (0) → dispose now; controlCloseWaitForEvents (1) → wait for
/// OnDisconnected/OnConfirmClose up to <paramref name="timeout"/>, then force Disconnect().
/// https://learn.microsoft.com/windows/win32/termserv/imsrdpclient-requestclose
/// </summary>
public async Task CloseAsync(TimeSpan timeout)
{
    if (!IsConnected)
    {
        ProbeLog.Write("close", "Not connected; nothing to close");
        return;
    }

    _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _client.RequestClose(out ControlCloseStatus status);
    ProbeLog.Write("close", $"RequestClose → {status} ({(int)status})");

    if (status == ControlCloseStatus.controlCloseCanProceed)
    {
        return;
    }

    var finished = await Task.WhenAny(_closed.Task, Task.Delay(timeout)).ConfigureAwait(true);
    if (finished != _closed.Task)
    {
        ProbeLog.Write("close", $"Timed out after {timeout.TotalSeconds:F0}s; forcing Disconnect()");
        _client.Disconnect();
    }
    else
    {
        ProbeLog.Write("close", "Closed gracefully (OnDisconnected received)");
    }
}
```

Si l'interop a généré `RequestClose` avec un **retour** plutôt qu'un `out` (voir Task 3 Step 2), adapter : `var status = _client.RequestClose();`.

- [ ] **Step 2: Appeler `CloseAsync` à la fermeture de la fenêtre**

Dans `ShellWindow.xaml.cs`, ajouter un champ et brancher `Closing` dans le constructeur :

```csharp
private bool _closeConfirmed;
```

```csharp
Closing += OnClosing;
```

```csharp
private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
{
    if (_closeConfirmed || _session is null || !_session.IsConnected)
    {
        _session?.Dispose();
        return;
    }

    // First pass: cancel the close, run the graceful protocol, then close for real.
    e.Cancel = true;
    ConnectButton.IsEnabled = false;
    DisconnectButton.IsEnabled = false;
    ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Informational, "Closing session…", "");

    await _session.CloseAsync(TimeSpan.FromSeconds(5));

    _closeConfirmed = true;
    Close();
}
```

- [ ] **Step 3: Build et sonde anti-zombie**

Run: `dotnet build src/RemoteDeck.App && dotnet run --project src/RemoteDeck.App`

1. Se connecter, ouvrir une session (bureau visible).
2. Fermer la fenêtre par la croix.
3. Sur le **serveur**, dans une console : `query session` (ou `quser`). Attendu : la session de l'utilisateur est en état **Disc** (déconnectée) et non **Active** avec un client fantôme ; aucune seconde session ne s'est créée.
4. Lire `probe-l0.log` : séquence attendue `RequestClose → controlCloseWaitForEvents (1)` puis `OnConfirmClose → allowing` puis `OnDisconnected …` puis `Closed gracefully`.

Consigner la séquence exacte observée. Si le timeout se déclenche systématiquement, c'est un résultat : noter et garder le repli `Disconnect()`.

- [ ] **Step 4: Commit**

```bash
git add src/RemoteDeck.App/Rdp/RdpSessionHost.cs src/RemoteDeck.App/Views/ShellWindow.xaml.cs
git commit -m "feat(app): graceful RequestClose shutdown protocol on window close"
```

---

### Task 7: `ShortcutInterceptor` — Ctrl+K / Ctrl+Tab en amont du contrôle (R6)

**Files:**
- Create: `src/RemoteDeck.App/Rdp/ShortcutInterceptor.cs`
- Modify: `src/RemoteDeck.App/Views/ShellWindow.xaml.cs`

**Interfaces:**
- Produces: `internal sealed class ShortcutInterceptor : IDisposable` — `ShortcutInterceptor(ShortcutInterceptor.Mechanism mechanism)`, `event Action<string>? Triggered` (valeurs `"Ctrl+K"`, `"Ctrl+Tab"`, `"Ctrl+Shift+Tab"`), `enum Mechanism { WpfThreadFilter, WinFormsMessageFilter, KeyboardHook }`.

Les trois mécanismes de la spec §7.3 sont implémentés côte à côte ; la sonde consiste à les essayer **dans l'ordre** pendant qu'une session RDP a le focus et à retenir le premier qui déclenche. On commence par `WpfThreadFilter` : c'est le mécanisme natif de la boucle de messages WPF, celui par lequel `WindowsFormsHost` fait déjà transiter les messages.

- [ ] **Step 1: Écrire `Rdp/ShortcutInterceptor.cs`**

```csharp
using System.Runtime.InteropServices;
using System.Windows.Interop;
using RemoteDeck.App.Services;

namespace RemoteDeck.App.Rdp;

/// <summary>
/// Catches application shortcuts before the RDP control swallows them (spec §7.3, R6).
/// Three interchangeable mechanisms; the lot-0 probe picks the first that fires while
/// the remote session has keyboard focus.
/// </summary>
internal sealed class ShortcutInterceptor : IDisposable
{
    public enum Mechanism { WpfThreadFilter, WinFormsMessageFilter, KeyboardHook }

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkTab = 0x09;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkK = 0x4B;
    private const int WhKeyboard = 2;

    public event Action<string>? Triggered;

    private readonly Mechanism _mechanism;
    private readonly WinFormsFilter? _winFormsFilter;
    private readonly HookProc? _hookProc;   // kept alive: the native side holds a raw pointer
    private nint _hook;
    private bool _disposed;

    public ShortcutInterceptor(Mechanism mechanism)
    {
        _mechanism = mechanism;
        switch (mechanism)
        {
            case Mechanism.WpfThreadFilter:
                ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
                break;
            case Mechanism.WinFormsMessageFilter:
                _winFormsFilter = new WinFormsFilter(this);
                System.Windows.Forms.Application.AddMessageFilter(_winFormsFilter);
                break;
            case Mechanism.KeyboardHook:
                _hookProc = HookCallback;
                _hook = SetWindowsHookEx(WhKeyboard, _hookProc, 0, GetCurrentThreadId());
                if (_hook == 0) throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
                break;
        }
        ProbeLog.Write("R6", $"ShortcutInterceptor armed with {mechanism}");
    }

    // --- Mechanism 1: WPF dispatcher filter ---
    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (handled) return;
        if (msg.message is not (WmKeyDown or WmSysKeyDown)) return;
        handled = Handle((int)msg.wParam);
    }

    // --- Mechanism 2: Windows Forms filter ---
    private sealed class WinFormsFilter(ShortcutInterceptor owner) : System.Windows.Forms.IMessageFilter
    {
        public bool PreFilterMessage(ref System.Windows.Forms.Message m)
        {
            if (m.Msg is not (WmKeyDown or WmSysKeyDown)) return false;
            return owner.Handle((int)m.WParam);
        }
    }

    // --- Mechanism 3: thread-local WH_KEYBOARD hook ---
    private delegate nint HookProc(int code, nint wParam, nint lParam);

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        // lParam bit 31 set = key up; we only act on key down.
        if (code >= 0 && ((long)lParam & 0x80000000L) == 0 && Handle((int)wParam))
        {
            return 1; // swallow
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool Handle(int virtualKey)
    {
        bool ctrl = (GetKeyState(VkControl) & 0x8000) != 0;
        if (!ctrl) return false;

        string? shortcut = virtualKey switch
        {
            VkK => "Ctrl+K",
            VkTab => (GetKeyState(VkShift) & 0x8000) != 0 ? "Ctrl+Shift+Tab" : "Ctrl+Tab",
            _ => null,
        };
        if (shortcut is null) return false;

        ProbeLog.Write("R6", $"{shortcut} intercepted by {_mechanism}");
        Triggered?.Invoke(shortcut);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        switch (_mechanism)
        {
            case Mechanism.WpfThreadFilter:
                ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
                break;
            case Mechanism.WinFormsMessageFilter:
                if (_winFormsFilter is not null) System.Windows.Forms.Application.RemoveMessageFilter(_winFormsFilter);
                break;
            case Mechanism.KeyboardHook:
                if (_hook != 0) UnhookWindowsHookEx(_hook);
                break;
        }
    }

    [DllImport("user32.dll")] private static extern short GetKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
```

- [ ] **Step 2: Brancher dans `ShellWindow`**

Champ :

```csharp
private ShortcutInterceptor? _shortcuts;
```

Dans `OnLoaded`, après la création de `_session` :

```csharp
var mechanismName = Environment.GetEnvironmentVariable("REMOTEDECK_PROBE_SHORTCUTS") ?? "WpfThreadFilter";
var mechanism = Enum.Parse<ShortcutInterceptor.Mechanism>(mechanismName, ignoreCase: true);
_shortcuts = new ShortcutInterceptor(mechanism);
_shortcuts.Triggered += shortcut => Dispatcher.Invoke(() =>
    ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Success, $"{shortcut} intercepted", $"via {mechanism} — command palette arrives in lot 5"));
```

Dans `OnClosing`, avant `_session?.Dispose()` (les deux branches) :

```csharp
_shortcuts?.Dispose();
```

- [ ] **Step 3: Build et sonde R6**

Run: `dotnet build src/RemoteDeck.App`

Protocole, **avec une session RDP connectée et le focus dans le bureau distant** (cliquer dans le bureau distant d'abord) :

1. `dotnet run --project src/RemoteDeck.App` (mécanisme par défaut `WpfThreadFilter`) → appuyer `Ctrl+K`, puis `Ctrl+Tab`. Attendu : InfoBar verte « Ctrl+K intercepted via WpfThreadFilter » et la frappe **n'atteint pas** la session distante (ouvrir Notepad côté distant pour le vérifier : rien ne doit s'y passer).
2. Si rien ne se déclenche : `$env:REMOTEDECK_PROBE_SHORTCUTS = "WinFormsMessageFilter"` puis relancer, même test.
3. Si rien : `$env:REMOTEDECK_PROBE_SHORTCUTS = "KeyboardHook"`, même test.

Consigner pour chaque mécanisme : déclenche / ne déclenche pas, et si la frappe fuit vers la session. Le premier mécanisme qui déclenche **sans fuite** devient le défaut du lot 3 (retirer la variable d'environnement à ce moment-là).

- [ ] **Step 4: Commit**

```bash
git add src/RemoteDeck.App/Rdp/ShortcutInterceptor.cs src/RemoteDeck.App/Views/ShellWindow.xaml.cs
git commit -m "feat(app): shortcut interceptor with three switchable mechanisms (probe R6)"
```

---

### Task 8: Sonde R5 — existe-t-il une surface pour l'empreinte du certificat ?

**Files:**
- Modify: `src/RemoteDeck.App/Rdp/RdpSessionHost.cs`

**Interfaces:**
- Produces: `static void RdpSessionHost.LogCertificateSurface()` — inventaire par réflexion de l'interop.

La doc ne liste aucune API d'empreinte. Plutôt que de l'affirmer, on le **mesure** : on énumère tous les membres de l'assembly d'interop dont le nom contient `Cert`, `Thumb` ou `Auth`, et on journalise. Le résultat (vide ou non) tranche R5.

- [ ] **Step 1: Ajouter la méthode**

Dans `RdpSessionHost` :

```csharp
/// <summary>R5 probe: list every interop member that might expose server certificate data.</summary>
public static void LogCertificateSurface()
{
    var assembly = typeof(IMsRdpClient10).Assembly;
    var hits = assembly.GetTypes()
        .SelectMany(t => t.GetMembers().Select(m => $"{t.Name}.{m.Name}"))
        .Where(n => n.Contains("Cert", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("Thumb", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("AuthenticationWarning", StringComparison.OrdinalIgnoreCase))
        .Distinct()
        .OrderBy(n => n)
        .ToArray();

    ProbeLog.Write("R5", hits.Length == 0
        ? "No interop member mentions Cert/Thumb/AuthenticationWarning"
        : $"{hits.Length} candidate member(s): {string.Join(", ", hits)}");
}
```

- [ ] **Step 2: L'appeler au démarrage**

Dans `ShellWindow.OnLoaded`, après la création de `_session` :

```csharp
RdpSessionHost.LogCertificateSurface();
```

- [ ] **Step 3: Build, lancer, lire le log**

Run: `dotnet build src/RemoteDeck.App && dotnet run --project src/RemoteDeck.App`

Lire la ligne `[R5]`. Attendu d'après la doc : seulement les événements `OnAuthenticationWarningDisplayed` / `OnAuthenticationWarningDismissed` et `AuthenticationLevel` — **aucun** membre donnant le certificat lui-même. Si un membre inattendu apparaît (par ex. sur une interface `NonScriptable` récente), le noter : c'est une piste pour la v1.5, pas une promesse pour la v1.

Puis se connecter à un hôte au certificat auto-signé : vérifier que la boîte de dialogue de certificat du contrôle s'affiche et que `OnAuthenticationWarningDisplayed` est journalisé (Task 5 l'a câblé).

- [ ] **Step 4: Commit**

```bash
git add src/RemoteDeck.App/Rdp/RdpSessionHost.cs src/RemoteDeck.App/Views/ShellWindow.xaml.cs
git commit -m "feat(app): reflection probe for certificate surface (probe R5)"
```

---

### Task 9: Consigner les résultats, mettre à jour la spec, check-list manuelle

**Files:**
- Create: `docs/superpowers/probes/l0-probe-results.md`, `docs/manual-checklist.md`
- Modify: `docs/superpowers/specs/2026-08-29-remotedeck-design.md` (§13 — colonne « Résultat »)

**Interfaces:**
- Produces: le document qui clôt le lot 0 et les décisions qu'il impose aux lots 1–5.

- [ ] **Step 1: Écrire `docs/superpowers/probes/l0-probe-results.md`**

Remplir chaque cellule « Observé » avec ce que `probe-l0.log` et les tests manuels ont montré. Chaque ligne se termine par une décision, pas par une impression.

```markdown
# Lot 0 — résultats des sondes de risque

Date d'exécution : (date)
Machine : Windows 11 10.0.26100, mstscax.dll 10.0.26100.8875
Contrôle retenu : version (label) — CLSID (guid)

| Risque | Question | Observé | Décision pour les lots suivants |
|---|---|---|---|
| R1 | `ClearTextPassword` accepte-t-il un BSTR natif via `IDispatch::Invoke`, sans `string` managée ? | (ouverture de session OK/KO ; ligne `[R1]` présente ; HRESULT en cas d'échec) | (garder `ComSecretPut` / repli) |
| R2 | Les événements `IMsTscAxEvents_Event` arrivent-ils via `COMReference` ? | (séquence `Connecting…/Connected/Logged on` observée ?) | (garder `COMReference` / repli AxImp) |
| R3 | Rendu net en DPI mixte ? | (valeurs `DpiScale` ; flou oui/non en changeant d'écran) | (PerMonitorV2 suffit / ajustement) |
| R4 | `FluentWindow` + `WindowsFormsHost` cohabitent-ils ? | (barre de titre, redimensionnement, Mica : OK/KO) | (garder WPF-UI / repli styles maison) |
| R5 | Une surface expose-t-elle le certificat serveur ? | (sortie de `LogCertificateSurface`) | (empreinte hors v1 confirmé / piste trouvée) |
| R6 | Quel mécanisme intercepte `Ctrl+K`/`Ctrl+Tab` sans fuite vers la session ? | (résultat par mécanisme) | (mécanisme par défaut du lot 3) |
| R7 | `set_Property("EnableRdsAadAuth", true)` déclenche-t-il le flux compte web ? | (ligne `[R7]` ; comportement à la connexion) | (case visible en v1 / masquée + §14) |

## Signatures d'interop constatées (Task 3, Step 2)

(coller la sortie du script PowerShell : événements, `set_ClearTextPassword`, `set_Property`, `RequestClose`, `GetErrorDescription`)

## Séquence de fermeture observée (Task 6)

(coller les lignes `[close]` de probe-l0.log ; état `query session` côté serveur)

## Écarts par rapport à la spec

(toute divergence entre ce que la spec prévoyait et ce qui a été constaté, avec la section concernée)
```

- [ ] **Step 2: Reporter les décisions dans la spec**

Dans `docs/superpowers/specs/2026-08-29-remotedeck-design.md`, §13, ajouter une colonne **Résultat (lot 0)** au tableau des risques et y reporter, pour R1–R7, la décision prise. Si un repli est activé, mettre à jour la section concernée (§5.2, §6.1, §6.6, §6.7, §7.1, §7.3) pour qu'elle décrive ce qui sera **réellement** construit — la spec est le contrat des lots suivants, elle ne doit pas décrire une option abandonnée.

- [ ] **Step 3: Écrire `docs/manual-checklist.md`**

```markdown
# Manual verification checklist

Run before tagging a release. Items are grouped by the lot that introduced them.
Automated tests cover `RemoteDeck.Core`; everything below touches COM or WPF and
cannot be automated reliably.

## Lot 0 — control hosting

- [ ] App starts on a machine with mstscax.dll; the InfoBar names the control version.
- [ ] App starts on a machine **without** the newest control (or with the CLSID list
      temporarily reordered): an older version is picked, no crash.
- [ ] Connect with valid credentials: remote desktop renders inside the window.
- [ ] Connect to a nonexistent host: InfoBar shows an error with reason code and
      Windows description; no MessageBox.
- [ ] Wrong password: no crash; OnLogonError or CredSSP prompt.
- [ ] Close the window while connected: on the server, `query session` shows the
      session as Disc (not Active), and no duplicate session exists.
- [ ] Move the window between monitors with different DPI: rendering stays crisp.
- [ ] Ctrl+K and Ctrl+Tab are intercepted while the remote desktop has focus, and
      the keystrokes do not reach the remote session.
- [ ] Title bar drag, double-click maximise and system buttons work with the RDP
      control visible.
```

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/probes/l0-probe-results.md docs/manual-checklist.md docs/superpowers/specs/2026-08-29-remotedeck-design.md
git commit -m "docs: lot 0 probe results, spec risk outcomes, manual checklist"
```

- [ ] **Step 5: Vérification finale du lot**

Run: `dotnet build RemoteDeck.sln && dotnet test RemoteDeck.sln`
Expected: build sans avertissement, 5 tests verts.

Run: `git status --short`
Expected: vide (rien d'oublié, `docs/PROJET.md` ignoré).

Critère de fin de lot (spec §12) : **une session RDP s'affiche et se ferme proprement ; R1 à R7 ont chacun une décision écrite.**

---

## Auto-revue du plan

**Couverture de la spec (lot 0)** : squelette 3 projets (T1) · frontière Core sans WPF/COM (T1 csproj) · `COMReference` + `AxHost` (T3) · catalogue CLSID avec repli de version (T2, T3) · `FluentWindow`/Mica (T4) · manifeste DPI (T1, sonde T4) · chaîne du secret sans `string` (T5) · événements COM (T5) · erreurs explicites en InfoBar avec code + description Windows, code 3 non traité en erreur (T5) · `RequestClose` (T6) · interception clavier, 3 mécanismes (T7) · sonde certificat (T8) · sonde compte web (T5) · CI (T1) · check-list manuelle (T9) · report des décisions dans la spec (T9).

**Hors lot 0, volontairement** : SQLite, coffre DPAPI, liste/recherche, onglets, reconnexion, palette, import, `.resx` — lots 1 à 5. `KeyboardHookMode` est réglé (valeur documentée), pas sondé : ce n'est pas un risque.

**Cohérence des types** : `RdpControlVersion(Guid Clsid, string Label)` utilisé tel quel en T3/T4 · `RdpAxHost.Ocx : object` consommé par `RdpSessionHost` et `ComSecretPut(object, nint)` · `RdpDisconnectInfo(int, int, string)` consommé par `ShellWindow` · `ShortcutInterceptor.Mechanism` parsé depuis l'env en T7 · `ProbeLog.Write(string probe, string message)` partout.

**Dépendances aux signatures générées** : cinq points (événements, `set_ClearTextPassword`, `set_Property`, `RequestClose`, `GetErrorDescription`) sont vérifiés en T3 Step 2 **avant** d'être utilisés en T5/T6 ; chaque usage indique l'adaptation à faire si la typelib diffère.
