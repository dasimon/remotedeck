# RemoteDeck — conception v1

**Date** : 2026-08-29
**Auteur** : David Simon (conception assistée)
**Statut** : validé — **amendé le 2026-08-29 par les résultats du lot 0**
(D2, D3, §2, §3, §5.2, §6.1, §6.4, §6.5, §6.6, §6.7, §7.1, §7.3, §11, §12, §13, §14).
Les sondes et leurs traces sont consignées dans
`docs/superpowers/probes/l0-probe-results.md` ; ce qui n'a pu être vérifié y est dit
comme tel et reporté sur `docs/manual-checklist.md`.

> Note de langue : ce document de conception est en français (document de travail).
> Le code, les commentaires, l'UI par défaut et la documentation publique
> (README, SECURITY, CONTRIBUTING) sont en anglais — voir §9.

---

## 1. Objectif

Remplacer Windows App / `mstsc.exe` par un client RDP de bureau dont le point fort
est la **gestion des connexions**, faiblesse commune des outils existants.

Cible : administrateurs gérant plusieurs dizaines de machines (VM Hyper-V, postes
distants), au clavier, sur Windows 10/11.

Le rendu RDP n'est **pas** réimplémenté : on embarque le contrôle ActiveX natif de
Windows (`mstscax.dll`). Le projet apporte la gestion, pas le protocole.

### Critères de succès v1

1. Ouvrir une connexion enregistrée sans toucher la souris, en moins de 3 s.
2. Aucun mot de passe en clair sur disque, dans les logs, ni dans un dump managé.
3. Plusieurs sessions simultanées en onglets, sans session zombie côté serveur.
4. Toute erreur de connexion produit un message explicite et actionnable,
   jamais un code brut seul ni une `MessageBox` générique.
5. Une coupure réseau se rétablit seule, sans boucle infinie ni verrouillage de
   compte Active Directory.

---

## 2. Décisions

| # | Décision | Justification |
|---|---|---|
| D1 | Cible `net10.0` / `net10.0-windows` | Seuls les SDK .NET 10 (10.0.303, 10.0.400) sont installés sur le poste de développement. Aucun .NET 8. |
| D2 | Contrôle ActiveX choisi **à l'exécution** dans un catalogue de CLSID, du plus récent au plus ancien | ~~CLSID `{3F859AA3-…}` (version 13) en dur.~~ **Corrigé au lot 0** : cette CLSID est bien enregistrée sur le poste, mais sa fabrique de classe refuse de se créer (`CoGetClassObject` → `0x80040111` `CLASS_E_CLASSNOTAVAILABLE`). La version réellement instanciée est la **12**, CLSID `{1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8}`. La présence en base de registre ne prouve donc rien : le catalogue teste la **créabilité**, pas l'enregistrement (§6.1). |
| D3 | Interop **généré à la compilation** par `TlbImp.exe` (cible MSBuild) + `AxHost` maison | `AxImp.exe` est un outil .NET Framework ; ses assemblies référencent `System.Windows.Forms 4.0.0.0`, que .NET 10 ne résout pas. ~~`COMReference` est géré nativement par le SDK .NET.~~ **Corrigé au lot 0** : `<COMReference>` n'est **pas** compilable par `dotnet build` (MSB4803, SDK 10.0.400) — il n'est résolu que par MSBuild.exe complet. Une cible MSBuild appelle donc directement `TlbImp.exe` (§6.1). Intention inchangée : aucun binaire d'interop n'est commité, et le CLSID reste une donnée, donc le repli de version reste possible. |
| D4 | Secrets : DPAPI `CurrentUser` + entropie par identifiant | Chiffrement lié à la session Windows, sans clé dans le binaire. Corrige le défaut historique de mRemoteNG (clé de chiffrement en dur). |
| D5 | Secret déchiffré → `BSTR` natif direct → effacement | Le mot de passe n'existe jamais comme chaîne managée : ni duplication par le GC, ni présence dans un dump managé. Pas de `SecureString` : sur .NET Core/.NET 5+ il n'est **pas** chiffré en mémoire (contrairement à .NET Framework) et Microsoft en déconseille l'usage pour du code neuf — il n'apporterait qu'un intermédiaire de plus à remplir puis vider. |
| D6 | Résolution dynamique par défaut | `UpdateSessionDisplaySettings` rend l'image nette au pixel près lors d'un redimensionnement, au lieu de l'étirement flou de `SmartSizing`. |
| D7 | Projet public, licence MIT | Aucune dépendance interne : base locale, aucun service distant. |
| D8 | Anglais dans le code et l'UI, localisation `.resx` | Condition d'adoption et de contribution sur un projet public. Le français est livré comme première traduction. |
| D9 | Nom `RemoteDeck` | Vérifié : 22 dépôts homonymes sur GitHub, le plus étoilé à 4★, aucun dans le domaine du bureau distant ; identifiant NuGet libre. (`RdpDeck` était occupé par un homonyme du même créneau.) |
| D10 | UI bâtie sur WPF-UI (lepoco) 4.3.0, MIT | Rendu WinUI 3 sans quitter WPF, donc sans renoncer à `WindowsFormsHost` — l'hébergement ActiveX en WinUI 3 est nettement plus coûteux. |
| D11 | Distribution GitHub Releases + winget, non signée | Coût nul. Le message SmartScreen est documenté dans le README ; la réputation s'accumule avec les téléchargements. Une signature EV (300–600 €/an) n'est pas justifiable avant d'avoir des utilisateurs. |
| D12 | Onglets : barre d'onglets custom + panneau de sessions persistant — **pas** de `TabControl` WPF pour le contenu | Un `TabControl` ne garde vivant que le contenu de l'onglet actif : changer d'onglet décharge l'arbre visuel, détruit le `WindowsFormsHost` et donc **coupe la session RDP**. Les hôtes vivent tous dans un `Grid` commun, basculés par `Visibility` ; la barre d'onglets est un simple `ItemsControl` stylé. |

### Vérifications effectuées

Conformément à la règle « ne jamais supposer la valeur d'une énumération tierce »,
les éléments suivants proviennent de la documentation officielle, pas de la mémoire :

- **Décalage de version du contrôle** : la classe `MsRdpClient12NotSafeForScripting`
  est documentée par Microsoft comme « Microsoft RDP Client Control - version 13 ».
  Le libellé du registre est donc décalé de +1 par rapport au nom de classe.
  Source : <https://learn.microsoft.com/windows/win32/termserv/msrdpclient12notsafeforscripting>
- **`ControlCloseStatus`** : `controlCloseCanProceed` = `0x0000`,
  `controlCloseWaitForEvents` = `0x0001`.
  Source : <https://learn.microsoft.com/windows/win32/termserv/imsrdpclient-requestclose>
- **Codes de déconnexion** : voir §6.4.
  Source : <https://learn.microsoft.com/windows/win32/termserv/imstscaxevents-ondisconnected>
- **`SmartSizing`** modifiable alors que le contrôle est connecté.
  Source : <https://learn.microsoft.com/windows/win32/termserv/imsrdpclientadvancedsettings-smartsizing>
- **`KeyboardHookMode` = `2`** — « appliquer les combinaisons Windows au poste distant
  uniquement en plein écran ». Valeur lue sur la page dédiée au lot 0, puis posée dans
  `RdpSessionHost` (`SecuredSettings2.KeyboardHookMode = 2`). C'est le comportement
  voulu en fenêtré : les combinaisons système restent locales.
  Source : <https://learn.microsoft.com/windows/win32/termserv/imsrdpclientsecuredsettings-keyboardhookmode>
- **`IMsTscNonScriptable` n'est pas une interface duale** — elle est
  `IUnknown`-derived (`InterfaceIsIUnknown` dans l'interop généré, conforme à la
  documentation). Elle **n'expose aucun `IDispatch`** : la conception initiale du §5.2,
  qui prévoyait `IDispatch::Invoke`, était fausse. Vérifié au lot 0.
  Source : <https://learn.microsoft.com/windows/win32/termserv/imstscnonscriptable-interface>
- **Une CLSID enregistrée n'est pas forcément instanciable** : sur le poste de
  référence, la version 13 (`{3F859AA3-…}`) possède une clé `InprocServer32` complète
  et retourne pourtant `CLASS_E_CLASSNOTAVAILABLE` (`0x80040111`) à
  `CoGetClassObject`. Constaté au lot 0, pas supposé (voir D2 et §6.1).

---

## 3. Architecture

Trois projets. La frontière est vérifiée par le compilateur : `RemoteDeck.Core` ne
référence ni WPF, ni Windows Forms, ni COM. Rien de métier ne peut donc fuir dans
les vues, et tout ce qui compte se teste sans STA ni COM.

```
remotedeck/
├─ RemoteDeck.sln
├─ LICENSE (MIT) · README.md · SECURITY.md · CONTRIBUTING.md · .editorconfig
├─ .github/workflows/
│  ├─ ci.yml            build + test sur push et PR
│  └─ release.yml       publish single-file self-contained sur tag v*
├─ src/
│  ├─ RemoteDeck.Core/                    net10.0, nullable enable
│  │  ├─ Model/         Connection, Credential, DisplayMode, SessionState,
│  │  │                 ConnectionRules           (validation de l'éditeur, §7)
│  │  ├─ Data/          SqliteDatabase, SchemaMigrator,
│  │  │                 ConnectionRepository, CredentialRepository
│  │  ├─ Security/      SecretBytes, ICredentialVault, DpapiCredentialVault,
│  │  │                 CredentialRules
│  │  ├─ Search/        TextNormalizer            (repli casse + accents, un caractère pour un)
│  │  │                 ConnectionFilter          (correspondance floue + plages de surlignage)
│  │  ├─ Settings/      AppSettings, SettingsStore (settings.json, §7.2)
│  │  ├─ Sessions/      ReconnectPolicy           (§6.3 : ShouldReconnect + DelayFor, backoff)
│  │  ├─ Import/        RdpFileImporter           (.rdp + registre)              [lot 5]
│  │  └─ Diagnostics/   DisconnectReason          (§6.4 : code → DisconnectDescription,
│  │                                               catégorie + libellé court)
│  └─ RemoteDeck.App/                     net10.0-windows, UseWPF + UseWindowsForms
│     ├─ Interop/       RdpAxHost, RdpEventSink, RdpControlCatalog,
│     │                 ClsidRegistry (créabilité, §6.1), ComSecretPut (vtable, §5.2)
│     ├─ Rdp/           RdpSession (machine à états, reconnexion, résolution dynamique),
│     │                 SessionState (§6.2), RdpSessionHost (façade OCX, UpdateDisplay,
│     │                 fermeture §6.5), RdpConnectionSettings, ShortcutInterceptor (§7.3)
│     ├─ Controls/      InfoBarExtensions (InfoBar partagée), HighlightTextBlock
│     │                 (surlignage des correspondances), PasswordPlaceholder (adorner, §7.1)
│     ├─ ViewModels/    ConnectionListViewModel, SessionsViewModel (onglets, D12),
│     │                 SessionTabViewModel (un onglet : état, pastille, compte à rebours),
│     │                 ConnectionEditorViewModel, CredentialEditorViewModel,
│     │                 CommandPaletteViewModel                                  [lot 5]
│     ├─ Views/         ShellWindow, ConnectionPane, SessionTabStrip (§7.1/§7.2),
│     │                 ConnectionEditorWindow, CredentialsWindow,
│     │                 CredentialEditorWindow, CommandPaletteWindow             [lot 5]
│     ├─ Resources/     Strings.resx (en) · Strings.fr.resx · PasswordBox.xaml (§7.1) · Theme/
│     └─ Services/      FileLogger, DialogService
└─ tests/
   └─ RemoteDeck.Core.Tests/              xUnit
```

### Dépendances — 5 paquets

| Paquet | Version | Rôle |
|---|---|---|
| `Microsoft.Data.Sqlite` | 10.0.11 | Accès SQLite |
| `System.Security.Cryptography.ProtectedData` | 10.0.11 | DPAPI (absent du BCL depuis .NET Core) |
| `Microsoft.Extensions.DependencyInjection` | 10.x | Composition |
| `CommunityToolkit.Mvvm` | 8.4.2 | Générateurs de source MVVM |
| `WPF-UI` | 4.3.0 | Contrôles et thèmes Fluent |
| `xunit` + `xunit.runner.visualstudio` | — | Tests (projet de test uniquement) |

Écartés : EF Core (surdimensionné pour deux tables), frameworks MVVM lourds
(Prism, Caliburn), thèmes Material (rendu Android, inadapté à un outil
d'administration Windows).

**Contrainte de licence** : mRemoteNG est en GPL-2.0. Aucun code, aucune ressource
et aucun fragment de configuration issu de ce projet ne peut être repris ici.

---

## 4. Modèle de données

Fichier : `%APPDATA%\RemoteDeck\connections.db`, mode journal WAL.
Créé au premier lancement avec des ACL restreintes à l'utilisateur courant.

```sql
CREATE TABLE SchemaVersion (
  Version     INTEGER NOT NULL,
  AppliedUtc  TEXT    NOT NULL);

CREATE TABLE Credential (
  Id          INTEGER PRIMARY KEY,
  Label       TEXT    NOT NULL UNIQUE,   -- « Domain admin », affiché dans l'UI
  Domain      TEXT    NULL,
  UserName    TEXT    NOT NULL,
  SecretBlob  BLOB    NOT NULL,          -- DPAPI CurrentUser
  Entropy     BLOB    NOT NULL,          -- 32 octets aléatoires, propres à la ligne
  ModifiedUtc TEXT    NOT NULL);

CREATE TABLE Connection (
  Id                     INTEGER PRIMARY KEY,
  Name                   TEXT    NOT NULL,
  Host                   TEXT    NOT NULL,
  Port                   INTEGER NOT NULL DEFAULT 3389,
  GroupName              TEXT    NOT NULL DEFAULT '',
  CredentialId           INTEGER NULL REFERENCES Credential(Id) ON DELETE SET NULL,
  IsFavorite             INTEGER NOT NULL DEFAULT 0,
  DisplayMode            INTEGER NOT NULL DEFAULT 0,  -- 0 Dynamic, 1 Scaled, 2 Fixed
  FixedWidth             INTEGER NULL,
  FixedHeight            INTEGER NULL,
  RedirectClipboard      INTEGER NOT NULL DEFAULT 1,
  RedirectDrives         INTEGER NOT NULL DEFAULT 0,
  RedirectPrinters       INTEGER NOT NULL DEFAULT 0,
  RedirectAudio          INTEGER NOT NULL DEFAULT 0,
  AdminSession           INTEGER NOT NULL DEFAULT 0,
  UseWebAccount          INTEGER NOT NULL DEFAULT 0,  -- §6.7 ; expérimental (R7)
  AuthenticationLevel    INTEGER NULL,                -- §6.6 ; NULL = défaut système
  AcceptedCertThumbprint TEXT    NULL,                -- réservé, inutilisée en v1 (R5, §6.6)
  Notes                  TEXT    NOT NULL DEFAULT '',
  LastConnectedUtc       TEXT    NULL,
  CreatedUtc             TEXT    NOT NULL);

CREATE INDEX IX_Connection_GroupName ON Connection(GroupName);
CREATE INDEX IX_Connection_Favorite  ON Connection(IsFavorite) WHERE IsFavorite = 1;
```

Choix à souligner :

- `GroupName` et non `Group` — mot réservé SQL.
- **Un seul axe de classement.** L'énoncé parlait de « groupe/tag » avec des
  exemples mutuellement exclusifs (Prod, Recette, Dev). Un champ texte suffit et
  donne une arborescence à un niveau. Une table de tags N-N reste possible plus
  tard sans casser l'existant ; l'introduire maintenant serait spéculatif.
- `ON DELETE SET NULL` sur `CredentialId` : supprimer un compte ne détruit pas les
  connexions qui l'utilisaient ; elles repassent en « identifiant à choisir ».
- Les dates sont stockées en ISO-8601 UTC (`TEXT`), suffixe `Utc` dans le nom.
- `DisplayMode` est un entier dont le sens est fixé **par ce projet** (pas une
  énumération tierce) : 0 Dynamic, 1 Scaled, 2 Fixed.

Migrations : `SchemaMigrator` applique des scripts numérotés et incrémente
`SchemaVersion`. Une base plus récente que l'application refuse de s'ouvrir avec un
message explicite plutôt que de risquer une corruption.

---

## 5. Coffre-fort d'identifiants

### 5.1 Stockage

`ProtectedData.Protect(secretUtf8, entropy, DataProtectionScope.CurrentUser)`.

L'entropie est un tableau de 32 octets tiré par `RandomNumberGenerator`, propre à
chaque ligne et stocké à côté du blob. Elle ne remplace pas DPAPI, elle s'y ajoute :
deux identifiants partageant le même mot de passe produisent des blobs différents,
et le fichier `.db` seul, sans le profil Windows, reste inexploitable.

### 5.2 Chaîne du secret

Pas de `SecureString` (voir D5 : non chiffré en mémoire sur .NET moderne,
usage déconseillé par Microsoft). Le secret passe du blob DPAPI au `BSTR` natif
sans intermédiaire superflu :

```
DPAPI Unprotect → byte[] UTF-8 → chars → SysAllocStringLen (BSTR natif)
  → put_ClearTextPassword (appel direct de vtable) → Marshal.ZeroFreeBSTR (finally)
  → CryptographicOperations.ZeroMemory(byte[]/chars) (finally)
```

**Comment le `BSTR` atteint le contrôle** (vérifié au lot 0, risque R1). L'interop
généré expose `set_ClearTextPassword(System.String)` : l'appeler forcerait une chaîne
managée et ruinerait la garantie du D5. Le contournement n'est **pas** `IDispatch::Invoke` —
`IMsTscNonScriptable` est `IUnknown`-derived, elle n'a pas d'`IDispatch` (§2). C'est un
**appel direct de vtable** :

1. `QueryInterface` sur l'objet du contrôle avec l'IID `IMsTscNonScriptable`
   `{C1E6743A-41C1-4A74-832A-0DD06C1C7A0E}` ;
2. les slots 0–2 sont `QueryInterface`/`AddRef`/`Release`, sans bloc `IDispatch` à
   sauter ; le premier membre de l'interface est donc **le slot 3 =
   `put_ClearTextPassword(BSTR)`** ;
3. appel par pointeur de fonction `delegate* unmanaged[Stdcall]<nint, nint, int>`, puis
   `Marshal.ZeroFreeBSTR` dans le `finally`.

Le code est isolé dans `Interop/ComSecretPut` : c'est le seul endroit du projet qui
manipule une vtable, et il ne fait que cela.

Règles imposées par les signatures, non par la discipline :

- `ICredentialVault` **n'expose aucune méthode acceptant ou retournant un `string`**
  pour un secret. Il expose deux membres, et un pattern de portée fermée :
  `void Seal(Credential credential, nint secretBstr)` — le `BSTR` fourni par
  l'appelant (qui en garde la propriété) est chiffré dans `SecretBlob` avec une
  entropie neuve ; et `void UseSecret(Credential credential, Action<nint> useBstr)`
  — le `BSTR` est alloué, prêté au callback, puis libéré et écrasé dans le
  `finally`, y compris si le callback ou l'affectation COM lève.
- Tout tampon intermédiaire (`byte[]`, `char[]`) est effacé par
  `CryptographicOperations.ZeroMemory` dans un `finally`.
- Le modèle `Credential` ne porte pas le secret déchiffré. Il n'a donc rien à fuir
  s'il est sérialisé ou journalisé.

### 5.3 Journalisation

`FileLogger` écrit dans `%APPDATA%\RemoteDeck\logs\`. Les identifiants sont
journalisés par `Label` et `UserName`, jamais par secret. Un test vérifie que
`ICredentialVault` n'expose aucune surface `string` (§10).

### 5.4 Modèle de menace — à publier tel quel dans SECURITY.md

**Couvert** : vol du fichier `.db` seul ; copie de sauvegarde ; accès par un autre
compte Windows de la même machine ; absence totale de clé de chiffrement dans le
binaire distribué.

**Non couvert, et dit explicitement** : un code malveillant s'exécutant **sous la
session Windows de l'utilisateur, déverrouillée**, peut appeler `Unprotect`
exactement comme l'application le fait. Aucun coffre local ne protège de ce scénario.
Un attaquant administrateur de la machine, ou disposant d'un accès mémoire au
processus, est également hors périmètre.

Cette limite doit être écrite, pas tue : c'est la différence entre un projet
sérieux et une promesse invérifiable.

---

## 6. Couche RDP

### 6.1 Interop

`RdpControlCatalog` liste les CLSID connus, du plus récent au plus ancien, et
sélectionne le premier **réellement instanciable** sur la machine hôte. « Instanciable »
n'est pas « enregistré » : le prédicat `ClsidRegistry.IsUsable` garde la lecture du
registre comme simple pré-filtre, puis demande la fabrique de classe
(`CoGetClassObject`, `IID_IClassFactory`) et n'accepte la CLSID que si l'appel réussit.
Ce n'est pas de la prudence gratuite — sur le poste de référence, la version 13 est
enregistrée et retourne `CLASS_E_CLASSNOTAVAILABLE` ; le repli sur la version 12 est le
chemin **normal**, pas un cas dégradé (D2).

Le catalogue contient **trois** candidats — **13, 12, 11** — et s'arrête là. Le plancher
supporté est Windows 10 20H2 (README), dont le `mstscax.dll` livre déjà la version 11 : un
candidat plus ancien ne serait jamais sélectionné sur une machine supportée. La version 10
(`{8B918B82-…}`) a d'ailleurs été **retirée au lot 0** parce qu'elle serait nuisible et non
inerte : ce coclass n'implémente pas `IMsRdpClient10`, donc le sélectionner remplacerait le
message « aucun contrôle trouvé » par un échec de transtypage au démarrage.

`RdpAxHost : System.Windows.Forms.AxHost` héberge le contrôle. `RdpEventSink` s'abonne
au dispinterface `IMsTscAxEvents` et retransmet les événements sur le thread UI.

**Génération de l'interop** (D3). `<COMReference>` n'est pas compilable par
`dotnet build` : le SDK .NET 10.0.400 échoue en MSB4803, la tâche `ResolveComReference`
n'existant que dans MSBuild.exe complet. L'assembly d'interop est donc produit **à
chaque build** par une cible MSBuild (`GenerateMstscInterop`) qui appelle `TlbImp.exe`
sur `%SystemRoot%\System32\mstscax.dll` :

- **`/transform:DispRet` est obligatoire** — `ResolveComReference` appliquait la
  transformation `[out, retval]` implicitement, `TlbImp.exe` non. Sans ce commutateur,
  trois gestionnaires d'événements changent de forme (`OnConfirmClose`,
  `OnReceivedTSPublicKey`, `OnAutoReconnecting` passent d'une valeur de retour à un
  paramètre `ByRef`) et le code d'abonnement ne compile plus.
- **`/machine:X64` et un `TlbImp.exe` x64** — un TlbImp 32 bits est redirigé par WOW64
  vers `SysWOW64\mstscax.dll` et décrit la vue 32 bits du contrôle.
- **`/silence:3015`** — supprime les 78 avertissements TI3015 sur
  `get_UIParentWindowHandle` (retourne `_RemotableHandle*`, non marshalable). Build à
  zéro avertissement.
- **Prérequis de build** : Windows SDK ou .NET Framework 4.8 Developer Pack, pour
  disposer de `TlbImp.exe` ; chemin surchargeable par `-p:TlbImpPath=…`. Documenté dans
  le README.
- Aucun binaire d'interop n'est commité : `Interop.MSTSCLib.dll` vit dans `obj/`.
- À `/transform:DispRet` près, l'assembly produit est **identique membre par membre** à
  celui que produisait `COMReference` : le changement est un changement d'outil, pas de
  contrat.

L'ensemble est encapsulé derrière une interface `IRdpControl` définie dans
`RemoteDeck.App` : `RdpSession` ne manipule jamais le COM directement, ce qui rend
la machine à états lisible et le repli du risque R2 sans impact au-delà d'`Interop/`.

### 6.2 Machine à états

```
Idle → Connecting → Connected → Interrupted → Reconnecting(n) → Connected
                        ↓                            ↓
                     Closing                       Failed
```

Transitions pilotées par les événements du contrôle (`OnConnecting`, `OnConnected`,
`OnDisconnected`, `OnFatalError`, `OnLoginComplete`) et par les actions
utilisateur. `RdpSession` expose son état ; l'onglet s'y lie.

**Livré au lot 4** — `RemoteDeck.App/Rdp/SessionState.cs`, huit valeurs :

| État | Signification |
|---|---|
| `Idle` | Jamais démarrée, **ou** déconnectée normalement (codes 0–3, §6.4). L'onglet reste ouvert avec *Reconnect*. Ce n'est pas un échec. |
| `Connecting` | Première tentative en vol. |
| `Connected` | `OnConnected` reçu. |
| `Interrupted` | Chute sur un code reconnectable : une tentative est **planifiée**, le compte à rebours tourne. |
| `Reconnecting` | Une tentative est en vol (compte à rebours arrivé à zéro, ou *Reconnect* demandé). |
| `Failed` | Code non reconnectable, budget de tentatives épuisé, compte à rebours annulé par l'utilisateur, ou échec avant la mise sur le fil (exception de `supplyAndConnect`). |
| `Closing` | Protocole §6.5 en cours : `RequestClose` émis, attente. |
| `Closed` | Fermée et libérée. Terminal — une session fermée ne redémarre pas. |

`Idle` et `Failed` sont **délibérément distincts** : une déconnexion voulue ne doit
pas être peinte en rouge. La pastille de l'onglet suit ce découpage — vert
`Connected`, ambre `Interrupted`/`Reconnecting`, rouge `Failed`, neutre partout
ailleurs — et chaque changement (état, n° de tentative, tic du compte à rebours)
lève `RdpSession.Changed`, sur le thread d'interface uniquement.

### 6.3 Reconnexion automatique

`ReconnectPolicy` (dans `Core`, donc testable) décide **si** l'on reconnecte et
**quand** :

- Reconnexion uniquement sur les codes réseau.
- **Jamais** sur `disconnectReasonByServer` (3) — déconnexion volontaire, ce n'est
  pas une erreur.
- **Jamais** sur un refus d'authentification. Se reconnecter en boucle avec un
  mauvais mot de passe verrouille le compte Active Directory : c'est le
  comportement à proscrire absolument.
- Backoff : **2 s, 5 s, 10 s, 30 s, 60 s**, puis état `Failed` avec un bouton
  *Reconnect*. Cinq tentatives, pas de boucle infinie.
- Annulable à tout instant ; un compte à rebours visible indique la prochaine tentative.
- **Le secret est re-fourni à chaque tentative** : `Connect()` exige que
  `ClearTextPassword` soit repositionné. `RdpSession` porte le `CredentialId`,
  jamais le secret ; chaque tentative repasse par `ICredentialVault.UseSecret`
  (§5.2). Aucun secret n'est retenu entre deux tentatives.
- **La reconnexion intégrée du contrôle ActiveX est désactivée**
  (`EnableAutoReconnect = false`, `IMsRdpClientAdvancedSettings2`, activée par
  défaut) : sinon sa propre boucle « Reconnexion en cours… 1 sur 5 » s'exécute
  d'abord et ne remonte `OnDisconnected` qu'une fois ses cinq tentatives
  épuisées — les deux mécanismes s'empilent (5 + 5) et l'onglet affiche
  « Connected » pendant toute la phase du contrôle. `ReconnectPolicy` est le
  seul mécanisme de reconnexion, ce qui garantit aussi que le secret est
  re-fourni par le coffre à chaque tentative.

**Fait au lot 4 (2026-08-30).** `RemoteDeck.Core/Sessions/ReconnectPolicy.cs`. L'ensemble
des codes reconnectables est **fermé et explicite** — six valeurs, rien d'autre :

| Code | Constante | Pourquoi on réessaie |
|---|---|---|
| 264 | `disconnectReasonConnectionTimedOut` | Délai dépassé : l'hôte peut répondre à la tentative suivante |
| 516 | `disconnectReasonSocketConnectFailed` | Échec de `connect()` — coupure transitoire |
| 772 | `disconnectReasonWinsockSendFailed` | Émission perdue sur une socket établie |
| 1028 | `disconnectReasonSocketRecvFailed` | Réception perdue sur une socket établie |
| 1796 | `disconnectReasonTimeoutOccurred` | Temporisation du client |
| 2308 | `disconnectReasonAtClientWinsockFDCLOSE` | Socket fermée par le réseau |

Le critère est « la socket ou le minuteur a lâché, **mais l'hôte est toujours censé
répondre** ». D'où les exclusions, qui ne sont pas des oublis :

- **Codes de nom et d'adresse — 260, 520, 776, 1288, 1540, 2052** (`DNSLookupFailed`,
  `HostNotFound`, `InvalidIPAddr`, `DNSLookupFailed2`, `GetHostByNameFailed`,
  `InvalidIP`) : un nom qui ne résout pas ou une IP invalide ne deviendront pas
  valides en 60 secondes. Ce sont des erreurs de **saisie**, pas de réseau ; le
  seul correctif est l'éditeur de connexion. Réessayer cinq fois ne ferait que
  retarder de 107 s le message qui dit la vérité.
- **Codes 0–3** : la déconnexion était voulue (§6.4).
- **`SSL_ERR_*` (catégorie `Authentication`)** : verrouillage de compte AD garanti.
- **Sécurité, licence, mémoire, erreur interne** : une boucle de tentatives ne
  répare ni un certificat, ni une licence, ni un client à court de mémoire.

Un code absent de la table documentée n'est **jamais** reconnecté : `Describe`
le rend `Unknown` et `ShouldReconnect` répond `false`.

Le reste du contrat est tenu par `RdpSession` : `Attempt` (0 à 5) remis à zéro par
toute connexion réussie ; `NextRetryIn` alimente le compte à rebours visible et
l'action *Cancel* ; **chaque tentative — automatique ou manuelle — rejoue à
l'identique le délégué `supplyAndConnect` du shell**, qui repasse par
`ICredentialVault.UseSecret` et repositionne `ClearTextPassword`. La session ne
porte que la `Connection` ; ni le secret ni rien qui en dérive ne survit à une
tentative.

### 6.4 Erreurs explicites

À `OnDisconnected(discReason)`, on lit `ExtendedDisconnectReason` puis
`GetErrorDescription(discReason, extended)` pour obtenir le texte Windows localisé.

L'utilisateur voit d'abord **notre** message court, puis le détail :

| Code | Constante Microsoft | Message |
|---|---|---|
| 260 / 1288 / 1540 / 520 | `DNSLookupFailed`, `DNSLookupFailed2`, `GetHostByNameFailed`, `HostNotFound` | Nom d'hôte introuvable |
| 264 | `ConnectionTimedOut` | Délai de connexion dépassé |
| 2308 | `AtClientWinsockFDCLOSE` | Connexion fermée par le réseau |
| 2052 | `InvalidIP` | Adresse IP invalide |
| 1 | `LocalNotError` | Déconnexion locale — **pas une erreur** (voir l'avertissement ci-dessous) |
| 2 | `RemoteByUser` | Déconnexion par l'utilisateur distant — **pas une erreur** |
| 3 | `ByServer` | Déconnexion demandée par le serveur — **pas une erreur** |
| 1032 | `InternalError` | Erreur interne du client RDP |
| 1286 / 1542 / 2310 / 2566 / 2822 / 3078 | `InvalidEncryption`, `InvalidServerSecurityInfo`, `InternalSecurityError`, `InternalSecurityError2`, `EncryptionError`, `DecryptionError` | Échec de négociation de sécurité |
| 7175 / 8711 | `SSL_ERR_SMARTCARD_WRONG_PIN`, `SSL_ERR_SMARTCARD_CARD_BLOCKED` | Carte à puce : code incorrect / carte bloquée |

La table est reprise **intégralement** depuis la page Microsoft
`IMsTscAxEvents::OnDisconnected` au moment de l'implémentation, avec l'URL en
commentaire du fichier `DisconnectReason.cs`. Le tableau ci-dessus est le sous-ensemble
déjà vérifié ; **aucune valeur ne sera devinée**. Les codes inconnus tombent sur un
message générique affichant le code brut, jamais sur une interprétation inventée.

> **Le texte de `GetErrorDescription` n'est pas toujours fiable — vérifié au lot 0.**
> Le code **1** est documenté par Microsoft comme
> `disconnectReasonLocalNotError` (1 (0x1)) — « Local disconnection. This is not an
> error code. » C'est le code que le contrôle lève après **notre propre**
> `RequestClose` (§6.5). Or `GetErrorDescription(1, 0)` retourne
> « Une erreur interne s'est produite. » : un texte d'erreur pour un non-événement.
> Conséquence normative : les codes **0, 1, 2 et 3 ne sont jamais présentés comme des
> erreurs**, et pour ces codes le texte de `GetErrorDescription` **n'est pas affiché**.
> Il n'est appelé que pour les codes traités comme des échecs.
> Source : <https://learn.microsoft.com/windows/win32/termserv/imstscaxevents-ondisconnected>

L'affichage se fait dans un bandeau `InfoBar` **à l'intérieur de l'onglet** — jamais
une `MessageBox`, qui bloque l'application entière pour un incident local à une
session. Le bandeau contient : message court, code brut `discReason` / `extended`,
texte Windows, et actions contextuelles (*Reconnect*, *Change credential*,
*Copy diagnostics*).

**Fait au lot 4 (2026-08-30).** `RemoteDeck.Core/Diagnostics/DisconnectReason.cs` reprend
les **47 codes** de la page Microsoft (URL en tête de fichier, relevée le 2026-08-30) et
rend un `DisconnectDescription` = `(Reason, Category, Title, IsError)`. Les huit
catégories, qui décident du ton du message :

| Catégorie | Contenu | `IsError` | Sévérité `InfoBar` |
|---|---|---|---|
| `NotAnError` | 0, 1, 2, 3 | **non** | Informational |
| `Network` | 260, 264, 516, 520, 772, 776, 1028, 1288, 1540, 1796, 2052, 2308 | oui | Warning (la chute est réessayée) |
| `Authentication` | les 15 `SSL_ERR_*` (2055, 2567, 2823, 3079, 3335, 3591, 3847, 4615, 5639, 5895, 6151, 6919, 7175, 8455, 8711) | oui | Error |
| `Security` | 1030, 1286, 1542, 1798, 2310, 2566, 2822, 3078, 3080 | oui | Error |
| `Licensing` | 2056, 2312 | oui | Error |
| `Resources` | 262, 518, 774 (mémoire épuisée) | oui | Error |
| `Internal` | 1032, 1544 | oui | Error |
| `Unknown` | tout code absent de la page | oui | Error, avec le code brut dans le titre |

Aucune valeur n'est devinée : `Describe` d'un code inconnu retourne
« Disconnected (code *n*) », jamais une interprétation inventée. Et le texte de
`GetErrorDescription` n'est joint qu'aux codes traités comme des échecs
(l'avertissement ci-dessus).

Actions livrées dans la barre de session : **Reconnect** (offerte exactement en
`Failed` et en `Idle`), **Cancel** (exactement en `Interrupted` et `Reconnecting` —
les deux ne sont jamais affichées ensemble), **Copy diagnostics** et **Disconnect**.
*Copy diagnostics* met dans le presse-papiers un bloc de texte brut : connexion,
hôte:port, mode d'affichage, version du contrôle, état, tentatives consommées sur
5, temps restant, code de déconnexion + catégorie + libellé, `extended`, texte
Windows, état du repli SmartSizing et horodatage UTC. *Change credential* n'est pas
livrée : l'éditeur de connexion (`F2`) est le seul endroit qui change un identifiant.

### 6.5 Fermeture propre (anti-zombie)

Mécanisme documenté, appliqué à chaque fermeture d'onglet **et** en boucle sur
toutes les sessions à la fermeture de l'application :

1. `var status = RequestClose();` — la méthode **retourne** un `ControlCloseStatus`,
   elle n'a pas de paramètre de sortie (signature vérifiée au lot 0).
2. Si `controlCloseWaitForEvents` (`0x0001`) : attendre `OnDisconnected` ou
   `OnConfirmClose`, timeout 5 s, puis détruire le contrôle.
3. Si `controlCloseCanProceed` (`0x0000`) : destruction immédiate.
4. En dernier recours après timeout : `Disconnect()` puis destruction, avec une
   entrée de log.

**Observé au lot 0** : le contrôle répond systématiquement `controlCloseWaitForEvents`
(`1`), et **`OnConfirmClose` n'est jamais levé** — c'est `OnDisconnected(reason = 1)` qui
clôt l'attente. Attendre `OnConfirmClose` seul produirait un timeout de 5 s à chaque
fermeture : les deux événements doivent bien rester dans la condition d'attente. Après
fermeture puis reconnexion, `query session` côté serveur ne montre qu'une seule session,
sans doublon ni zombie. Le code de déconnexion `1` renvoyé ici n'est **pas** une erreur
(§6.4).

**Fait au lot 4 (2026-08-30) — fermeture de l'application.** `SessionsViewModel.CloseAllAsync`
ferme les onglets **l'un après l'autre**, jamais en parallèle : §6.5 est un protocole
*par contrôle*, et deux `RequestClose` simultanés entrelaceraient leurs attentes.
Budget : **5 s par onglet, 15 s pour la passe entière**. Un onglet qui ne rentre pas
dans le reliquat global est fermé avec un délai de 0 s — `RequestClose` est quand même
émis, on n'attend simplement plus. Ce que cela donne à la fermeture de la fenêtre :
`OnClosing` **annule** la fermeture (`e.Cancel = true`), affiche « fermeture de *n*
session(s)… », attend `CloseAllAsync`, puis relance `Close()` par `BeginInvoke` — pas
d'appel direct, qui ré-entrerait dans le gestionnaire en cours. Une seconde passe
(`DisposeAll`) démonte de force ce qui aurait survécu. Un contrôle qui refuse de se
fermer ne prend en otage ni son onglet ni la passe globale : l'exception est journalisée
et la libération suit de toute façon. Un `Ctrl+W` répété, ou un clic sur la croix
pendant que la fermeture tourne, est ignoré — un ensemble `_closing` garde chaque
onglet d'une double fermeture.

### 6.6 Certificat serveur non fiable

Le contrôle ActiveX gère lui-même sa boîte de dialogue de certificat, et il
n'existe **pas d'API documentée** pour lire l'empreinte du certificat serveur
depuis le contrôle. **Confirmé au lot 0** (risque R5) : l'inventaire par réflexion de
tout l'interop remonte 343 membres candidats, qui se réduisent à **deux noms
distincts**, et aucun ne convient :

- `PublisherCertificateChain` (`IMsRdpClientNonScriptable4` à `8`) est la chaîne de
  certificats de l'**éditeur RemoteApp** — faux positif, sans rapport avec le certificat
  du serveur ;
- `OnAuthenticationWarningDisplayed` / `OnAuthenticationWarningDismissed` (tous deux
  observés en vrai pendant la sonde) signalent que le contrôle a **affiché** son
  avertissement ; ils n'exposent ni empreinte, ni sujet, ni émetteur.

**Aucune surface de certificat serveur n'existe.** Le négatif est acquis, il n'est plus
à explorer. La v1 promet donc ce qui est garanti :

- `AuthenticationLevel` mémorisé **par connexion** (comportement standard mstsc,
  mais persistant — on ne recoche pas la même case à chaque session).
- La capture d'empreinte et l'alerte au changement sont **abandonnées en v1** :
  la colonne `AcceptedCertThumbprint` reste dans le schéma, **inutilisée** (elle ne
  coûte rien et évite une migration ultérieure). Aucun écran, aucun code ne la lit ni ne
  l'écrit ; le lot 5 n'a plus de décision à prendre à ce sujet.

### 6.7 Compte web (Microsoft Entra ID) — expérimental

La case mstsc « Utiliser un compte web pour vous connecter à l'ordinateur
distant » correspond à la propriété RDP `enablerdsaadauth` : authentification
Entra ID par navigateur (MFA, sans mot de passe transmis au serveur), pour les
machines Entra-joined ou hybrid-joined. Contraintes documentées : **pas d'adresse
IP** (le nom doit correspondre au hostname Entra) ; le verrouillage d'écran distant
déconnecte la session.
Source : <https://learn.microsoft.com/windows-server/remote/remote-desktop-services/remotepc/remote-desktop-connection-single-sign-on>

**Vérifié** : la liste documentée des propriétés nommées du contrôle ActiveX
(`IMsRdpExtendedSettings::Property`) ne contient **aucune** propriété
`EnableRdsAadAuth`. mstsc l'implémente au-dessus du même moteur (`mstscax.dll`),
le mécanisme existe donc probablement en interne, mais rien n'est documenté ni
garanti pour un contrôle embarqué.
Source : <https://learn.microsoft.com/windows/win32/termserv/imsrdpextendedsettings-property>

Traitement :

1. Colonne `UseWebAccount` présente dès le schéma initial ; case « Use web
   account (Microsoft Entra ID) — experimental » dans l'éditeur de connexion.
   Lorsqu'elle est cochée, l'identifiant du coffre est ignoré et aucun
   `ClearTextPassword` n'est positionné.
2. **Sonde du lot 0 (risque R7) — résultat.** `set_Property("EnableRdsAadAuth", true)`
   sur `IMsRdpExtendedSettings` **est accepté par le contrôle** : l'appel retourne sans
   erreur, la propriété non documentée existe donc réellement dans `mstscax.dll`. Avec
   la case cochée et **aucun mot de passe** fourni, la session s'est ouverte.
3. **Question ouverte, à ne pas refermer trop vite.** L'essai ne prouve **pas** le flux
   web. L'utilisateur était le **même compte de domaine que la session Windows locale**,
   et aucun navigateur ne s'est ouvert : une SSO CredSSP explique l'observation aussi
   bien que l'authentification Entra. Ce qui est établi : la propriété est acceptée et
   ne casse pas la connexion. Ce qui ne l'est pas : qu'elle déclenche le flux Entra.
   **Essai discriminant à faire** : depuis un compte **non membre du domaine** de la
   cible, vers une machine **Entra-joined**. Tant qu'il n'est pas fait, la mention
   « experimental » du libellé reste.
4. **Décision v1** : la case reste **visible et cochable**, libellée « Use web account
   (Microsoft Entra ID) — experimental ». Le repli pour les postes gérés Intune/Entra
   reste l'authentification AD classique par le coffre.

---

## 7. Interface

### 7.1 Parti pris

L'étalon est explicitement mRemoteNG, et l'objectif est de s'en distinguer nettement.

| mRemoteNG | RemoteDeck |
|---|---|
| Chrome Windows Forms, menus classiques | `FluentWindow`, barre de titre intégrée, Mica sur Windows 11 |
| Gris fixe, pas de thème | Clair/sombre suivant le système, couleur d'accentuation Windows |
| Onglets rectangulaires ~20 px | Onglets style Windows Terminal : 34 px, coins arrondis, réordonnables au glisser, pastille d'état (vert connecté / ambre reconnexion / rouge échec) |
| `TreeView` gris, icônes PNG 16 px | Liste virtualisée, en-têtes de groupe collants, lignes 32 px, favoris épinglés en tête, **Segoe Fluent Icons** (police système, zéro dépendance) |
| Recherche basique | Correspondance floue avec surlignage des caractères correspondants |
| Aucune palette de commandes | Palette `Ctrl+K` flottante, ombre portée, correspondance floue, navigation aux flèches |
| `MessageBox` | `InfoBar` dans l'onglet, avec actions |
| — | Animations 150 ms (palette, repli, changement d'onglet), grille d'espacement 4 px, état vide soigné rappelant les raccourcis |

**Une exception assumée : le champ de mot de passe.** Le `PasswordBox` de WPF-UI
n'expose que la valeur en `string` managée — inutilisable ici, il annule la garantie du
D5 avant même que le secret n'atteigne le coffre. Tout écran saisissant un mot de passe
utilise donc le **`PasswordBox` natif WPF**, seul à offrir `SecurePassword`. C'est la
seule dérogation à WPF-UI, et elle a un coût visuel constaté au lot 0 : pas de texte
indicatif (*placeholder*), et un style qui ne suit pas Fluent. **Lot 3** : restyler le
`PasswordBox` natif par `ResourceDictionary` (mêmes bordure, rayon, couleurs et états de
focus que les `TextBox` WPF-UI) et lui ajouter un texte indicatif par `Adorner`. Le
contrôle reste natif ; seule son apparence est reprise. Autre correctif d'ergonomie du
lot 3, relevé au lot 0 : les champs de saisie ne s'étirent pas quand la fenêtre
s'élargit (`HorizontalAlignment` à corriger).

**Fait au lot 3 (2026-08-30).** `Resources/PasswordBox.xaml` rejoue l'apparence de
l'`ui:TextBox` sur le `PasswordBox` natif — bordure, rayon, `Padding` 10,6 et états
survol/focus/désactivé, toutes les couleurs prises au thème WPF-UI par
`DynamicResource`, donc le champ suit la bascule clair/sombre. Le texte indicatif est
dessiné par `Controls/PasswordPlaceholder`, un `Adorner` posé **au-dessus** du contrôle :
le contenu du `PasswordBox` n'est jamais touché et la vacuité est décidée sur
`SecurePassword.Length`, jamais sur la propriété `Password` managée (D5 intact). Le
contrôle reste natif. Les champs de saisie s'étirent désormais avec la fenêtre.

### 7.2 Disposition

`Grid` à deux colonnes, `GridSplitter`, panneau gauche repliable (`Ctrl+B`,
largeur mémorisée). À droite, la zone d'onglets.

**Onglets (D12)** : pas de `TabControl` WPF pour héberger les sessions — son
`ContentPresenter` décharge le contenu des onglets inactifs, ce qui détruirait le
`WindowsFormsHost` et couperait la session à chaque changement d'onglet. À la
place : une barre d'onglets custom (`ItemsControl` stylé, pastilles d'état,
réordonnancement au glisser) au-dessus d'un `Grid` où **tous** les
`WindowsFormsHost` restent instanciés en permanence ; seul l'onglet actif est
`Visible`, les autres `Hidden` (pas `Collapsed` — le contrôle RDP doit conserver
sa surface pour ne pas renégocier l'affichage).

**Réglages d'interface** : persistés dans `%APPDATA%\RemoteDeck\settings.json`
(largeur du panneau, état replié, taille/position/état de la fenêtre). Les données
métier restent dans `connections.db` ; les préférences d'affichage n'ont rien à
faire dans un schéma migré. Livré au lot 3 : `AppSettings` / `SettingsStore`
(`RemoteDeck.Core/Settings/`), écriture atomique (fichier temporaire puis `Move`
remplaçant), lecture qui ne lève jamais — un fichier absent, illisible ou corrompu
retombe sur les valeurs par défaut. La géométrie n'est réappliquée que si le
rectangle entier tient encore sur le bureau courant, sinon la fenêtre revient au
centrage par défaut : une position enregistrée sur un écran depuis débranché ne doit
pas ouvrir la fenêtre hors de portée.

**En-têtes de groupe : non collants, décision assumée.** Le §7.1 annonçait des
en-têtes collants. Dans une `ListView` WPF ordinaire, le seul moyen de les rendre
réellement collants est `ScrollViewer.CanContentScroll="False"` — qui désactive la
virtualisation entière. Entre les deux, **la virtualisation gagne** : elle tient sur quelques
centaines de connexions, l'en-tête collant n'est qu'un confort. Le panneau du lot 3
virtualise donc (`IsVirtualizing`, `IsVirtualizingWhenGrouping`, mode `Recycling`,
`ScrollUnit=Pixel`) et ses en-têtes défilent avec le contenu.

### 7.3 Deux contraintes structurelles

**Airspace.** `WindowsFormsHost` se dessine toujours au-dessus du contenu WPF : un
overlay WPF par-dessus un onglet RDP serait invisible. Conséquence directe, et non
un choix esthétique : **la palette de commandes et les éditeurs sont des `Window`
WPF sans bordure**, `Owner` = fenêtre principale, centrées sur elle. Mica s'applique
à la barre de titre et au panneau gauche ; la zone RDP est opaque de toute façon.

**Capture clavier.** Le contrôle ActiveX avale les frappes lorsqu'il a le focus.
`Ctrl+K`, `Ctrl+Tab` et consorts doivent être interceptés en amont, avant que le
contrôle ne traite le message. Le lot 0 (risque R6) a départagé les mécanismes en
conditions réelles, et le résultat est net :

| Mécanisme | Armé | A intercepté |
|---|---|---|
| `ComponentDispatcher.ThreadFilterMessage` (WPF) | oui | **non** |
| `IMessageFilter` Windows Forms (`Application.AddMessageFilter`) | oui | **non** |
| `SetWindowsHookEx(WH_KEYBOARD)` local au thread | oui | **non** |
| `SetWindowsHookEx(WH_KEYBOARD_LL)` bas niveau, filtré sur « notre processus possède la fenêtre de premier plan » | oui | **oui** |

Les trois mécanismes prévus s'installent sans erreur et ne voient **jamais** les frappes :
le contrôle les consomme avant la boucle de messages du thread. **Le mécanisme retenu
pour les lots suivants est le hook bas niveau `WH_KEYBOARD_LL`**, et c'est **le mécanisme
par défaut** depuis le lot 0 : `ShortcutInterceptor` s'arme sur `LowLevelKeyboardHook` sans
qu'aucune variable d'environnement soit nécessaire. Les trois autres restent dans
`ShortcutInterceptor.Mechanism` comme options de diagnostic (`REMOTEDECK_PROBE_SHORTCUTS`),
pas comme replis crédibles ; une valeur inconnue dans cette variable retombe sur le défaut
au lieu de faire échouer le démarrage. Corollaire de méthode : « le hook est armé » n'est **pas** une preuve,
seule la trace d'interception en est une.

Deux réserves à traiter, écrites ici pour ne pas être redécouvertes plus tard :

1. **Portée trop large.** Le hook est global au bureau et n'est filtré que sur la
   fenêtre de premier plan. Il avale donc `Ctrl+K` et `Ctrl+Tab` **partout dans
   l'application**, y compris dans les `TextBox`. **Lot 5** : ne pas intercepter quand
   le focus clavier est sur un contrôle de saisie WPF.
2. **Aucune E/S synchrone dans le callback — règle tenue.** Windows applique
   `LowLevelHooksTimeout` (300 ms par défaut) et **désinstalle silencieusement** un hook
   trop lent. `LowLevelHookCallback` ne fait plus que **décider** (quelques
   `GetAsyncKeyState`, aucun accès fichier) puis **poster** : l'écriture du journal et la
   notification `Triggered` partent sur le `Dispatcher` WPF, et le callback rend la main
   immédiatement. Les trois mécanismes de boucle de messages, eux, notifient de façon
   synchrone — sans hook à désinstaller, la contrainte ne s'y applique pas.

**Repli pour les environnements verrouillés.** Une politique de sécurité (EDR, GPO) peut
interdire l'installation d'un hook bas niveau. Dans ce cas, aucun raccourci applicatif
n'est interceptable pendant que le contrôle a le focus, mais l'utilisateur n'est pas
prisonnier : le contrôle lève lui-même l'événement natif **`OnFocusReleased`** sur
`Ctrl+Alt+Gauche` / `Ctrl+Alt+Droite`, ce qui rend le focus à l'application. C'est le
repli documenté, et il ne dépend d'aucun hook.

`KeyboardHookMode` est réglé à **`2`** — combinaisons Windows appliquées au poste distant
**en plein écran seulement**, donc conservées localement en mode fenêtré. Valeur lue dans
la documentation au lot 0, voir §2.

### 7.4 Raccourcis

| Raccourci | Action |
|---|---|
| `Ctrl+K` | Palette de commandes |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Onglet suivant / précédent |
| `Ctrl+W` | Fermer l'onglet |
| `Ctrl+B` | Replier le panneau |
| `Ctrl+F` | Focus sur la recherche |
| `F2` | Éditer la connexion sélectionnée |
| `Entrée` | Connecter la sélection |
| `Ctrl+N` | Nouvelle connexion |

### 7.5 Recherche

Filtrage en mémoire sur `Name`, `Host` et `GroupName`, insensible à la casse et aux
accents, favoris d'abord, debounce 120 ms. Quelques centaines de connexions ne
justifient aucune requête SQL par frappe. `ConnectionFilter` retourne les plages de
caractères correspondantes pour le surlignage — d'où sa présence dans `Core`, testable.

---

## 8. Import

`RdpFileImporter` lit deux sources :

1. Les fichiers `.rdp` d'un dossier choisi — format `clé:type:valeur`, clés
   `full address:s:`, `username:s:`, `server port:i:`, `screen mode id:i:`.
2. `HKCU\Software\Microsoft\Terminal Server Client\Servers` — hôtes déjà utilisés
   par `mstsc.exe`.

L'import est **non destructif** : prévisualisation, dédoublonnage sur
`(Host, Port)`, l'utilisateur coche ce qu'il conserve. Aucun mot de passe n'est
importé — les fichiers `.rdp` ne contiennent qu'un blob DPAPI lié à un autre
contexte, inexploitable et sans intérêt à transcrire.

---

## 9. Localisation

Toutes les chaînes d'interface passent par `Strings.resx` (anglais, culture neutre)
dès le premier écran. `Strings.fr.resx` est livré avec la v1. Le coût est nul si
c'est fait immédiatement, et pénible ensuite.

Les messages de log restent en anglais, non localisés : ils sont destinés au
diagnostic et aux rapports d'incident.

---

## 10. Tests

`RemoteDeck.Core.Tests`, xUnit. Ce qui est testé sans COM ni UI :

| Cible | Cas |
|---|---|
| `DpapiCredentialVault` | Aller-retour ; déchiffrement avec une entropie étrangère → échec ; deux secrets identiques → blobs différents ; **aucune surface `string`** (vérifié par réflexion sur l'interface) |
| Repositories | CRUD ; `ON DELETE SET NULL` ; unicité de `Credential.Label` ; migration depuis une base vide et depuis la version précédente ; refus d'une base plus récente |
| `ConnectionFilter` | Casse et accents ; correspondance sur nom/hôte/groupe ; favoris en tête ; plages de surlignage exactes |
| `ReconnectPolicy` | Suite exacte des délais ; arrêt à la 5ᵉ tentative ; **non-déclenchement sur le code 3** ; non-déclenchement sur échec d'authentification |
| `RdpFileImporter` | Parsing des clés attendues ; lignes malformées ignorées ; dédoublonnage `(Host, Port)` |
| `DisconnectReason` | Correspondance code → clé de message ; code inconnu → message générique conservant le code brut |

**Non testable automatiquement** : interop COM, rendu WPF, comportement du contrôle
ActiveX. Couverts par une check-list de vérification manuelle tenue dans
`docs/manual-checklist.md`, exécutée avant chaque release.

---

## 11. Distribution

- **Licence** MIT. `LICENSE`, `README.md`, `SECURITY.md`, `CONTRIBUTING.md` en anglais.
- **CI** (`ci.yml`) : build + tests sur push et pull request, `windows-latest`.
  Attention : la génération de l'interop (§6.1) exige **deux** choses sur le runner —
  `%SystemRoot%\System32\mstscax.dll` (présent sur `windows-latest`) **et**
  `TlbImp.exe` x64, fourni par le Windows SDK / le .NET Framework 4.8 Developer Pack.
  Prérequis à vérifier au premier push ; c'est un échec de build franc, pas un bug
  silencieux.
- **Release** (`release.yml`) : sur tag `v*`, publication d'un exécutable
  single-file self-contained x64 attaché à la GitHub Release.
- **winget** : manifeste soumis après la première release stable.
- **SmartScreen** : l'absence de signature est documentée dans le README, avec la
  marche à suivre. La réputation s'accumule avec les téléchargements.
- **Séquence du dépôt** : git local dès le lot 0 → dépôt GitHub **privé** pendant
  le développement → bascule en public à la v1, une fois `SECURITY.md` rédigé et
  l'autorisation de publication obtenue auprès de la direction (le code naît sur un
  poste de travail d'entreprise ; la titularité des droits ne relève pas de l'IT).

---

## 12. Lots de livraison

| Lot | Contenu | Fin de lot |
|---|---|---|
| **L0** | Squelette des 3 projets, interop `TlbImp`, `RdpAxHost`, `RdpEventSink`, `FluentWindow`, connexion codée en dur | **Fait** (2026-08-29). Session RDP affichée et fermée proprement. R1–R7 tranchés : voir §13 et `docs/superpowers/probes/l0-probe-results.md`. Seul R3 (DPI mixte) est *déplacé*, pas levé : machine de test mono-DPI, vérification portée sur `docs/manual-checklist.md`. |
| **L1** | SQLite, modèle, migrations, repositories, tests | **Fait** (2026-08-29). `SqliteDatabase` (base créée au premier lancement, `Database ready` au log), migrations V1 par `SchemaMigrator` avec refus d'un schéma plus récent (`SchemaTooNewException`), `ConnectionRepository` / `CredentialRepository`, 33 tests verts à la clôture du lot. |
| **L2** | `DpapiCredentialVault`, éditeur d'identifiants, chaîne du secret | **Fait** (2026-08-29). `Seal`/`UseSecret` sur DPAPI CurrentUser + entropie de 32 octets par identifiant, `CredentialsWindow` / `CredentialEditorWindow`, secret jamais matérialisé en `string` (test de réflexion sur `ICredentialVault`), fourniture du mot de passe au contrôle depuis le coffre. Modèle de menace du §5.4 publié dans `SECURITY.md`. La sonde humaine de fin de lot (connexion réelle avec un identifiant du coffre) reste à jouer par David. |
| **L3** | Panneau de connexions, groupes, recherche floue, favoris, éditeur de connexion, restylage du `PasswordBox` natif (§7.1) | **Fait** (2026-08-30). Coquille à deux colonnes (`Grid` + `GridSplitter`, panneau repliable) autour d'une zone de session unique. Panneau : liste virtualisée de 32 px, groupée (`★ Favorites` en tête, puis les groupes, puis `Ungrouped`), recherche floue insensible à la casse et aux accents avec surlignage des caractères touchés (`TextNormalizer` + `ConnectionFilter` dans `Core`, debounce 120 ms, filtrage de l'instantané mémoire — aucune requête SQL par frappe), étoile de favori, état vide rappelant les raccourcis, repli propre en « base indisponible ». Éditeur de connexion modal (`ConnectionEditorWindow`, validation par `ConnectionRules`) ; connexion depuis la liste avec l'identifiant du coffre, ou invite CredSSP du contrôle quand il n'y en a pas. Réglages d'interface dans `%APPDATA%\RemoteDeck\settings.json` (§7.2). `PasswordBox` natif restylé Fluent + texte indicatif par `Adorner`, champs qui s'étirent (§7.1) — les deux réserves d'ergonomie de R4 sont levées. Raccourcis : `Ctrl+B`, `Ctrl+F`, `F2`, `Entrée`, `Ctrl+N`, `Suppr` (suppression en deux temps dans l'`InfoBar`, désarmée après 5 s — jamais de `MessageBox`). 65 tests verts à la clôture. **Reporté** : résolution dynamique (D6) au lot 4, palette `Ctrl+K` au lot 5 ; en-têtes de groupe **non collants**, la virtualisation est conservée (§7.2). |
| **L4** | Onglets multi-sessions, `Ctrl+Tab`, `ReconnectPolicy`, fermeture propre, résolution dynamique (D6) | **Fait** (2026-08-30). Onglets multi-sessions selon D12 : barre custom (`SessionTabStrip`, onglets de 34 px, coins arrondis, pastille d'état verte/ambre/rouge, réordonnancement au glisser, fermeture au clic milieu, animation 150 ms) au-dessus d'un `Grid` où **tous** les `WindowsFormsHost` restent instanciés — seul l'actif est `Visible`, les autres `Hidden`, donc changer d'onglet ne coupe aucune session. Une connexion a au plus un onglet : la reconnecter ramène le sien au premier plan. `RdpSession` (une session = un onglet) porte la machine à états à 8 valeurs du §6.2, le compte à rebours de reconnexion et la boucle de résolution. Reconnexion automatique (§6.3) : `ReconnectPolicy` dans `Core`, six codes réseau seulement, backoff 2/5/10/30/60 s, 5 tentatives, compte à rebours visible et annulable, **secret re-fourni par le coffre à chaque tentative**. Diagnostic (§6.4) : `DisconnectReason` (47 codes documentés, 8 catégories) pilote le libellé et la sévérité de l'`InfoBar` ; actions *Reconnect*, *Cancel*, *Copy diagnostics*, *Disconnect*. Résolution dynamique (D6) livrée : `UpdateSessionDisplaySettings` après un anti-rebond de 300 ms, taille en pixels physiques (`VisualTreeHelper.GetDpi`), plancher 640×480, et **repli une-fois-pour-toutes sur `SmartSizing`** si le contrôle refuse le redimensionnement — un contrôle qui en refuse un les refuse tous, on ne réessaie plus pour cette session. Fermeture propre (§6.5) appliquée à chaque onglet et à la sortie (5 s par onglet, 15 s au total, séquentiel). Raccourcis : `Ctrl+Tab` / `Ctrl+Shift+Tab` (cyclique), `Ctrl+W`. 130 tests verts à la clôture. **Reste au lot 5** : palette `Ctrl+K`, import `.rdp`, filtrage du hook clavier sur les champs de saisie (`Ctrl+W` reste avalé dans un `TextBox`, §7.3), `.resx` fr. **Sonde humaine de fin de lot à jouer par David** — coupure réseau réelle, redimensionnement, `query session` : voir la section « Lot 4 » de `docs/manual-checklist.md`, non cochée à ce jour. |
| **L5** | Palette `Ctrl+K`, import `.rdp`, filtrage du hook clavier sur les champs de saisie (§7.3), `.resx` fr | Critères de succès du §1 tous vérifiés |

La politique de certificat n'est plus un contenu de lot : R5 a fermé le sujet
(§6.6, négatif confirmé).

L0 est délibérément le premier lot : il ne produit presque aucune fonctionnalité,
mais il lève les risques. Les découvrir au lot 4 coûterait une réécriture — et le lot 0
a effectivement invalidé trois hypothèses de conception (`COMReference`,
`IDispatch::Invoke`, interception clavier par filtre de thread).

---

## 13. Risques

Sondés au lot 0 le 2026-08-29 (Windows 11 10.0.26200, `mstscax.dll` 10.0.26100.8875,
contrôle version 12). Détail et traces : `docs/superpowers/probes/l0-probe-results.md`.

| # | Risque | Détection | Repli prévu | **Résultat (lot 0)** |
|---|---|---|---|---|
| **R1** | `ClearTextPassword` exposé uniquement en `string` par l'interop généré, ce qui casse la garantie « jamais de chaîne managée » | L0 | Affectation par `IDispatch.Invoke` avec un `BSTR` construit à la main | **Repli activé, mais corrigé** : le risque est réel (le setter généré est bien en `string`), et le repli prévu était **faux** — `IMsTscNonScriptable` n'est pas duale, elle n'a pas d'`IDispatch`. Retenu : appel direct **vtable slot 3** `put_ClearTextPassword(BSTR)` (§5.2). Session ouverte, `Logged on`. Garantie D5 tenue. |
| **R2** | Abonnement au dispinterface `IMsTscAxEvents` non fonctionnel via `COMReference` | L0 | `AxImp` + retargeting de la référence `System.Windows.Forms`, isolé dans `Interop/` | **Abonnement OK, mécanisme changé.** `OnConnecting/OnConnected/OnLoginComplete/OnDisconnected` tous reçus. Mais `COMReference` n'est pas compilable par `dotnet build` (MSB4803) : remplacé par une cible MSBuild `TlbImp.exe` (`/transform:DispRet` obligatoire, x64, `/silence:3015`) — D3 et §6.1. Le repli `AxImp` n'a pas été nécessaire. |
| **R3** | DPI mixte multi-écran mal géré par le contrôle | L0 | Manifeste PerMonitorV2 ; à défaut, DPI système et mise à l'échelle assumée | **Non observé — risque déplacé, pas levé.** Machine de test mono-DPI (`X=1,00 Y=1,00`). Manifeste PerMonitorV2 conservé ; la vérification DPI mixte passe sur `docs/manual-checklist.md`. |
| **R4** | `FluentWindow` (barre de titre custom) en conflit avec `WindowsFormsHost` | L0 | `ResourceDictionary` maison sur `Window` standard ; l'UI reste moderne, sans Mica | **Aucun conflit.** Mica, barre de titre intégrée, glisser, maximisation, redimensionnement : OK. WPF-UI conservé (D10). Deux points d'ergonomie renvoyés au lot 3 : `PasswordBox` natif à restyler, champs qui ne s'étirent pas (§7.1). |
| **R5** | Pas d'API documentée pour lire l'empreinte du certificat serveur (§6.6) | L0 (sonde) | Fonctionnalité rétrogradée : `AuthenticationLevel` par connexion seulement ; colonne `AcceptedCertThumbprint` inutilisée en v1 | **Négatif confirmé, repli entériné.** 343 membres inspectés, 2 noms distincts, aucun exploitable (`PublisherCertificateChain` = éditeur RemoteApp ; `OnAuthenticationWarning*` = simple notification d'affichage). `AcceptedCertThumbprint` **inutilisée en v1**, `AuthenticationLevel` par connexion seulement. Sujet clos, plus de décision au lot 5. |
| **R6** | Interception clavier (`Ctrl+K`, `Ctrl+Tab`) : `IMessageFilter` non garanti pour un contrôle hébergé dans WPF (§7.3) | L0 | `ComponentDispatcher.ThreadFilterMessage`, puis hook `WH_KEYBOARD` local au thread | **Les trois mécanismes prévus échouent** (armés, zéro interception ; les frappes partent dans la session). Quatrième mécanisme ajouté et retenu : **`WH_KEYBOARD_LL`** filtré sur la fenêtre de premier plan → `Ctrl+K` et `Ctrl+Tab` interceptés. Par défaut au lot 3. Réserves et repli `OnFocusReleased` : §7.3. |
| **R7** | Compte web Entra ID : propriété `EnableRdsAadAuth` non documentée pour le contrôle ActiveX (§6.7) | L0 (sonde) | Case masquée en v1, fonctionnalité reportée au §14 ; auth AD classique par le coffre | **Propriété acceptée par le contrôle**, session ouverte sans mot de passe. **Mais flux web non départagé** de la SSO CredSSP (même compte de domaine que la session locale, aucun navigateur ouvert). Case **conservée et visible** en v1, libellée « experimental » ; essai discriminant à faire depuis un compte hors domaine vers une cible Entra-joined (§6.7). |

---

## 14. Hors périmètre v1

Énuméré pour éviter la dérive, pas pour fermer la porte : RD Gateway, multi-écran
réel (span), enregistrement de session, synchronisation entre postes, SSH/VNC,
scripts avant/après connexion, import depuis mRemoteNG ou Royal TS, arborescence de
groupes à plusieurs niveaux, tags multiples.

**ARM64 (y compris en compilation depuis les sources : `PlatformTarget` x64)** — ajouté au
lot 0. L'interop est généré par `TlbImp.exe` avec `/machine:X64` (§6.1) et la publication
vise un single-file **x64** (§11) ; `RemoteDeck.App.csproj` fixe donc `PlatformTarget` à
**x64** plutôt que d'hériter d'AnyCPU, faute de quoi une compilation depuis les sources sur
un poste ARM64 produirait un hôte incapable de charger son propre interop. Une exécution ARM64 native
n'est ni produite ni testée en v1 ; sur un poste ARM64, le binaire x64 passe par
l'émulation. Rendre l'architecture paramétrable est faisable — c'est un commutateur de
plus dans la cible MSBuild et un second job de release — mais cela demande une machine
ARM64 pour dérouler `docs/manual-checklist.md`, qui n'existe pas ici.

**Capture d'empreinte du certificat serveur** — définitivement hors v1 : le lot 0 a
établi qu'aucune surface ne l'expose (§6.6, R5). Ce n'est pas un arbitrage de périmètre,
c'est une impossibilité constatée.

Chacun est une v2 crédible. Aucun n'est nécessaire pour remplacer Windows App au
quotidien, et chacun ajouté maintenant retarderait le seul objectif qui compte :
une v1 utilisable tous les jours.
