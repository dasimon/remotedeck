# RemoteDeck — conception v1

**Date** : 2026-08-29
**Auteur** : David Simon (conception assistée)
**Statut** : validé, prêt pour plan d'implémentation

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
| D2 | Contrôle ActiveX CLSID `{3F859AA3-C2D4-4FAA-B0E4-FD0C9C4E5E3A}` | Version la plus récente enregistrée sur le poste (libellé registre « Microsoft RDP Client Control - version 13 »). |
| D3 | Interop par `COMReference` + `AxHost` maison | `AxImp.exe` est un outil .NET Framework ; ses assemblies référencent `System.Windows.Forms 4.0.0.0`, que .NET 10 ne résout pas. `COMReference` est géré nativement par le SDK .NET et produit un interop compatible. Bénéfice annexe : le CLSID devient une donnée, donc repli de version possible. |
| D4 | Secrets : DPAPI `CurrentUser` + entropie par identifiant | Chiffrement lié à la session Windows, sans clé dans le binaire. Corrige le défaut historique de mRemoteNG (clé de chiffrement en dur). |
| D5 | `SecureString` → `BSTR` natif → effacement | Le mot de passe n'existe jamais comme chaîne managée : ni duplication par le GC, ni présence dans un dump managé. |
| D6 | Résolution dynamique par défaut | `UpdateSessionDisplaySettings` rend l'image nette au pixel près lors d'un redimensionnement, au lieu de l'étirement flou de `SmartSizing`. |
| D7 | Projet public, licence MIT | Aucune dépendance interne : base locale, aucun service distant. |
| D8 | Anglais dans le code et l'UI, localisation `.resx` | Condition d'adoption et de contribution sur un projet public. Le français est livré comme première traduction. |
| D9 | Nom `RemoteDeck` | Vérifié : 22 dépôts homonymes sur GitHub, le plus étoilé à 4★, aucun dans le domaine du bureau distant ; identifiant NuGet libre. (`RdpDeck` était occupé par un homonyme du même créneau.) |
| D10 | UI bâtie sur WPF-UI (lepoco) 4.3.0, MIT | Rendu WinUI 3 sans quitter WPF, donc sans renoncer à `WindowsFormsHost` — l'hébergement ActiveX en WinUI 3 est nettement plus coûteux. |
| D11 | Distribution GitHub Releases + winget, non signée | Coût nul. Le message SmartScreen est documenté dans le README ; la réputation s'accumule avec les téléchargements. Une signature EV (300–600 €/an) n'est pas justifiable avant d'avoir des utilisateurs. |

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
- **Non vérifié à ce stade** : les valeurs de `KeyboardHookMode`
  (`IMsRdpClientSecuredSettings`). À lire sur la page dédiée **avant** d'écrire la
  ligne concernée, au lot 0. Aucune valeur n'est écrite dans ce document.

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
│  │  ├─ Model/         Connection, Credential, DisplayMode, SessionState
│  │  ├─ Data/          SqliteFactory, SchemaMigrator,
│  │  │                 ConnectionRepository, CredentialRepository
│  │  ├─ Security/      ICredentialVault, DpapiCredentialVault
│  │  ├─ Search/        ConnectionFilter          (correspondance floue + plages de surlignage)
│  │  ├─ Sessions/      ReconnectPolicy           (backoff, décision de reconnexion)
│  │  ├─ Import/        RdpFileImporter           (.rdp + registre)
│  │  └─ Diagnostics/   DisconnectReason          (code → clé de message)
│  └─ RemoteDeck.App/                     net10.0-windows, UseWPF + UseWindowsForms
│     ├─ Interop/       RdpAxHost, RdpEventSink, ClsidCatalog
│     ├─ Rdp/           RdpSession (machine à états), SessionManager
│     ├─ ViewModels/    ShellViewModel, ConnectionListViewModel, SessionViewModel,
│     │                 ConnectionEditorViewModel, CommandPaletteViewModel
│     ├─ Views/         ShellWindow, ConnectionPane, SessionView,
│     │                 CommandPaletteWindow, ConnectionEditorWindow,
│     │                 CredentialEditorWindow
│     ├─ Resources/     Strings.resx (en) · Strings.fr.resx · Theme/
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
  AcceptedCertThumbprint TEXT    NULL,
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

```
DPAPI Unprotect → byte[] UTF-8 → SecureString → (byte[] effacé)
SecureString → Marshal.SecureStringToBSTR → ClearTextPassword → Marshal.ZeroFreeBSTR (finally)
```

Règles imposées par les signatures, non par la discipline :

- `ICredentialVault` **n'expose aucune méthode acceptant ou retournant un `string`**
  pour un secret. Uniquement `SecureString`.
- Tout `byte[]` intermédiaire est effacé par `CryptographicOperations.ZeroMemory`
  dans un `finally`.
- Le `BSTR` est libéré par `Marshal.ZeroFreeBSTR` dans un `finally`, y compris si
  l'affectation COM lève.
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

`ClsidCatalog` liste les CLSID connus, du plus récent au plus ancien, et
sélectionne le premier instanciable sur la machine hôte. Cela évite un échec brutal
sur un poste où la version 13 n'existe pas.

`RdpAxHost : System.Windows.Forms.AxHost` héberge le contrôle ; les interfaces
proviennent du `COMReference` `MSTSCLib`. `RdpEventSink` s'abonne au dispinterface
`IMsTscAxEvents` et retransmet les événements sur le thread UI.

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
| 3 | `ByServer` | Déconnexion demandée par le serveur — **pas une erreur** |
| 1032 | `InternalError` | Erreur interne du client RDP |
| 1286 / 1542 / 2310 / 2566 / 2822 / 3078 | `InvalidEncryption`, `InvalidServerSecurityInfo`, `InternalSecurityError`, `InternalSecurityError2`, `EncryptionError`, `DecryptionError` | Échec de négociation de sécurité |
| 7175 / 8711 | `SSL_ERR_SMARTCARD_WRONG_PIN`, `SSL_ERR_SMARTCARD_CARD_BLOCKED` | Carte à puce : code incorrect / carte bloquée |

La table est reprise **intégralement** depuis la page Microsoft
`IMsTscAxEvents::OnDisconnected` au moment de l'implémentation, avec l'URL en
commentaire du fichier `DisconnectReason.cs`. Le tableau ci-dessus est le sous-ensemble
déjà vérifié ; **aucune valeur ne sera devinée**. Les codes inconnus tombent sur un
message générique affichant le code brut, jamais sur une interprétation inventée.

L'affichage se fait dans un bandeau `InfoBar` **à l'intérieur de l'onglet** — jamais
une `MessageBox`, qui bloque l'application entière pour un incident local à une
session. Le bandeau contient : message court, code brut `discReason` / `extended`,
texte Windows, et actions contextuelles (*Reconnect*, *Change credential*,
*Copy diagnostics*).

### 6.5 Fermeture propre (anti-zombie)

Mécanisme documenté, appliqué à chaque fermeture d'onglet **et** en boucle sur
toutes les sessions à la fermeture de l'application :

1. `RequestClose(out status)`.
2. Si `controlCloseWaitForEvents` (`0x0001`) : attendre `OnDisconnected` ou
   `OnConfirmClose`, timeout 5 s, puis détruire le contrôle.
3. Si `controlCloseCanProceed` (`0x0000`) : destruction immédiate.
4. En dernier recours après timeout : `Disconnect()` puis destruction, avec une
   entrée de log.

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

### 7.2 Disposition

`Grid` à deux colonnes, `GridSplitter`, panneau gauche repliable (`Ctrl+B`,
largeur mémorisée). À droite, la zone d'onglets.

### 7.3 Deux contraintes structurelles

**Airspace.** `WindowsFormsHost` se dessine toujours au-dessus du contenu WPF : un
overlay WPF par-dessus un onglet RDP serait invisible. Conséquence directe, et non
un choix esthétique : **la palette de commandes et les éditeurs sont des `Window`
WPF sans bordure**, `Owner` = fenêtre principale, centrées sur elle. Mica s'applique
à la barre de titre et au panneau gauche ; la zone RDP est opaque de toute façon.

**Capture clavier.** Le contrôle ActiveX avale les frappes lorsqu'il a le focus.
`Ctrl+K`, `Ctrl+Tab` et consorts sont donc interceptés en amont par un
`IMessageFilter` Windows Forms enregistré sur le thread UI, avant que le contrôle ne
traite le message. `KeyboardHookMode` est réglé pour ne pas rediriger les touches
système en mode fenêtré — **valeur à lire dans la documentation au lot 0**.

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
| **L0** | Squelette des 3 projets, `COMReference`, `RdpAxHost`, `RdpEventSink`, `FluentWindow`, connexion codée en dur | Une session RDP s'affiche et se ferme proprement. **Lève R1, R2, R3, R4.** |
| **L1** | SQLite, modèle, migrations, repositories, tests | Tests verts, base créée au premier lancement |
| **L2** | `DpapiCredentialVault`, éditeur d'identifiants, chaîne du secret | Connexion réussie avec un identifiant du coffre |
| **L3** | Panneau de connexions, groupes, recherche floue, favoris, éditeur de connexion | Navigation clavier intégrale du panneau |
| **L4** | Onglets multi-sessions, `Ctrl+Tab`, `ReconnectPolicy`, fermeture propre | Coupure réseau simulée → reconnexion puis abandon propre |
| **L5** | Palette `Ctrl+K`, import `.rdp`, politique de certificat, `.resx` fr | Critères de succès du §1 tous vérifiés |

L0 est délibérément le premier lot : il ne produit presque aucune fonctionnalité,
mais il lève les quatre risques. Les découvrir au lot 4 coûterait une réécriture.

---

## 13. Risques

| # | Risque | Détection | Repli |
|---|---|---|---|
| **R1** | `ClearTextPassword` exposé uniquement en `string` par l'interop généré, ce qui casse la garantie « jamais de chaîne managée » | L0 | Affectation par `IDispatch.Invoke` avec un `BSTR` construit à la main |
| **R2** | Abonnement au dispinterface `IMsTscAxEvents` non fonctionnel via `COMReference` | L0 | `AxImp` + retargeting de la référence `System.Windows.Forms`, isolé dans `Interop/` |
| **R3** | DPI mixte multi-écran mal géré par le contrôle | L0 | Manifeste PerMonitorV2 ; à défaut, DPI système et mise à l'échelle assumée |
| **R4** | `FluentWindow` (barre de titre custom) en conflit avec `WindowsFormsHost` | L0 | `ResourceDictionary` maison sur `Window` standard ; l'UI reste moderne, sans Mica |

---

## 14. Hors périmètre v1

Énuméré pour éviter la dérive, pas pour fermer la porte : RD Gateway, multi-écran
réel (span), enregistrement de session, synchronisation entre postes, SSH/VNC,
scripts avant/après connexion, import depuis mRemoteNG ou Royal TS, arborescence de
groupes à plusieurs niveaux, tags multiples.

Chacun est une v2 crédible. Aucun n'est nécessaire pour remplacer Windows App au
quotidien, et chacun ajouté maintenant retarderait le seul objectif qui compte :
une v1 utilisable tous les jours.
