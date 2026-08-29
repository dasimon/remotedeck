# Lot 0 — résultats des sondes de risque

**Date d'exécution** : 2026-08-29
**Machine** : Windows 11 10.0.26200, `mstscax.dll` 10.0.26100.8875, SDK .NET 10.0.400
**Hôte distant de test** : `TEST-VM` (VM du domaine, compte AD `testuser`)
**Journal** : `%APPDATA%\RemoteDeck\logs\probe-l0.log` (écrit par `Services/ProbeLog`)

**Contrôle retenu** : *version 12* — CLSID `1df7c823-b2d4-4b54-975a-f2ac5d7cf8b8`

**Contrôle écarté** : *version 13* — CLSID `3f859aa3-c2d4-4faa-b0e4-fd0c9c4e5e3a`. Il est
**enregistré** (`InprocServer32` complet dans la base de registre) mais **non instanciable** :

```
[R4] CLSID 3f859aa3-c2d4-4faa-b0e4-fd0c9c4e5e3a is registered but not creatable:
     CoGetClassObject returned 0x80040111
[R4] FluentWindow + WindowsFormsHost created; control version 12 (1df7c823-b2d4-4b54-975a-f2ac5d7cf8b8)
```

`0x80040111` = `CLASS_E_CLASSNOTAVAILABLE`. Conséquence directe et non anticipée par la
conception : **la présence en base de registre ne prouve rien**. `ClsidRegistry.IsUsable`
demande désormais la fabrique de classe (`CoGetClassObject`, `IID_IClassFactory`) et ne se
contente plus de la clé de registre. Le repli de version du catalogue (D2/§6.1) a donc été
exercé pour de vrai dès le premier lancement, et non simulé.

---

## Tableau des risques

| Risque | Question | Observé | Décision pour les lots suivants |
|---|---|---|---|
| **R1** | `ClearTextPassword` accepte-t-il un `BSTR` natif sans `string` managée ? | **Oui, mais pas par le chemin prévu.** La conception prévoyait `IDispatch::Invoke` avec un `VARIANT`. C'est **faux** : `IMsTscNonScriptable` est `IUnknown`-derived (`InterfaceIsIUnknown` dans l'interop généré, confirmé par la doc Microsoft), il n'y a donc **aucun `IDispatch`** sur cette interface. L'écriture se fait par **appel direct du slot 3 de la vtable** (`put_ClearTextPassword(BSTR)` ; slots 0–2 = `QueryInterface`/`AddRef`/`Release`), après `QueryInterface` sur l'IID `c1e6743a-41c1-4a74-832a-0dd06c1c7a0e`. Journal : `[R1] ClearTextPassword set through IMsTscNonScriptable vtable with a native BSTR`, puis `[session] Connecting… → Connected → Logged on` avec le mot de passe saisi dans l'application (case « compte web » décochée). | **Garder `ComSecretPut` (vtable slot 3)**. Amender §5.2 : supprimer toute mention d'`IDispatch::Invoke`/`VARIANT`. La garantie « aucune chaîne managée » du D5 tient. |
| **R2** | Les événements `IMsTscAxEvents_Event` arrivent-ils ? | **Oui** : `OnConnecting`, `OnConnected`, `OnLoginComplete`, `OnDisconnected` tous reçus. `[R2] Subscribed to IMsTscAxEvents_Event via TlbImp-generated interop`. **Mais le mécanisme d'interop a changé** : `<COMReference>` n'est **pas** compilable par `dotnet build` (MSB4803 — « the reference assemblies for … are not supported by the .NET SDK », SDK 10.0.400). L'interop est désormais généré **à la compilation** par une cible MSBuild qui appelle `TlbImp.exe`. | **Garder l'abonnement `IMsTscAxEvents_Event`** ; **abandonner `COMReference`** au profit de la cible `GenerateMstscInterop`. Amender D3 et §6.1. L'intention est inchangée : aucun binaire d'interop n'est commité. |
| **R3** | Rendu net en DPI mixte ? | **Non observé** : `[R3] Window DPI scale X=1,00 Y=1,00` — la machine de test est mono-DPI (100 %). Aucun deuxième écran à échelle différente n'était disponible. Le manifeste PerMonitorV2 est en place et le rendu 100 % est net. Défaut mineur relevé : la valeur DPI est journalisée dans la culture courante (virgule décimale) — reporté, sans impact fonctionnel. | **Garder le manifeste PerMonitorV2.** La vérification DPI mixte **reste à faire** et passe sur `docs/manual-checklist.md` (elle n'est pas levée, elle est déplacée). |
| **R4** | `FluentWindow` + `WindowsFormsHost` cohabitent-ils ? | **Oui.** Mica, barre de titre intégrée, glisser de la fenêtre, maximisation et redimensionnement : OK d'après le test humain ; rendu visuel conforme. Deux réserves d'ergonomie, pas de blocage : (a) le `PasswordBox` **natif WPF** — imposé par le besoin de `SecurePassword`, le `PasswordBox` de WPF-UI n'exposant qu'une chaîne managée — n'a **pas de texte indicatif** (*placeholder*) ; (b) les champs de saisie ne s'étirent pas quand la fenêtre s'élargit. | **Garder WPF-UI (D10).** Amender §7.1 : consigner l'exception du `PasswordBox` natif et son restylage au lot 3. |
| **R5** | Une surface expose-t-elle le certificat **serveur** ? | **Non — négatif confirmé.** La réflexion sur l'interop remonte **343 membres candidats**, qui se réduisent à **deux noms distincts** : `PublisherCertificateChain` (présent sur `IMsRdpClientNonScriptable4` à `8` et toutes les coclasses) et `OnAuthenticationWarningDisplayed`/`Dismissed`. `PublisherCertificateChain` est, d'après la documentation Microsoft, la chaîne de certificats de l'**éditeur RemoteApp** — un faux positif, sans rapport avec le certificat du serveur. Les deux événements ont d'ailleurs été observés en vrai (`[R5] OnAuthenticationWarningDisplayed fired (certificate warning shown by the control)`, puis `Dismissed`) : ils signalent que le contrôle a **affiché** son avertissement, ils n'en exposent **ni l'empreinte, ni le sujet, ni l'émetteur**. Aucune surface de certificat serveur, aucune empreinte. | **Confirme le repli déjà écrit en §6.6** : `AcceptedCertThumbprint` reste **inutilisée en v1** (colonne conservée pour éviter une migration) ; seul `AuthenticationLevel` par connexion est promis. §6.6 passe de « à déterminer » à « vérifié ». |
| **R6** | Quel mécanisme intercepte `Ctrl+K` / `Ctrl+Tab` sans fuite vers la session ? | **Les trois mécanismes prévus échouent.** `WpfThreadFilter` (`ComponentDispatcher.ThreadFilterMessage`), `WinFormsMessageFilter` (`Application.AddMessageFilter`) et `KeyboardHook` (`WH_KEYBOARD` local au thread) se sont tous **armés** sans erreur (`[R6] ShortcutInterceptor armed with …`) et **aucun n'a intercepté quoi que ce soit** : les frappes sont parties dans la session distante. Un **quatrième mécanisme** a été ajouté : `WH_KEYBOARD_LL` (hook clavier bas niveau, global), filtré sur « notre processus possède la fenêtre de premier plan ». Résultat : `[R6] Ctrl+K intercepted by LowLevelKeyboardHook`, `[R6] Ctrl+Tab intercepted by LowLevelKeyboardHook`. | **Mécanisme par défaut du lot 3 = `LowLevelKeyboardHook`.** Amender §7.3 : remplacer la liste des trois mécanismes par le résultat, avec les deux réserves ci-dessous. Ajouter le repli `OnFocusReleased` pour les environnements verrouillés. |
| **R7** | `set_Property("EnableRdsAadAuth", true)` déclenche-t-il le flux compte web ? | **La propriété est acceptée** : `[R7] set_Property("EnableRdsAadAuth", true) returned without error` — la propriété non documentée existe donc bien dans `mstscax.dll`. Avec la case cochée et **aucun mot de passe** fourni, la session s'est ouverte. **Mais l'observation ne prouve pas le flux web** : l'utilisateur était le **même compte de domaine que la session Windows locale**, ce qui rend une SSO CredSSP tout aussi plausible que le flux Entra par navigateur. Aucun navigateur ne s'est ouvert. **Non départagé.** | **Garder la case « Use web account (Microsoft Entra ID) — experimental » visible en v1**, avec son libellé expérimental. §6.7 consigne l'observation **et** la question ouverte : refaire l'essai depuis un compte **non membre du domaine** vers une cible **Entra-joined** avant de retirer la mention « expérimental ». |

---

## Signatures d'interop constatées (Task 3, Step 2)

Extraites de `.superpowers/sdd/2026-08-29-l0-skeleton-and-probes/interop-signatures.txt`
(réflexion sur `obj/Debug/net10.0-windows/Interop.MSTSCLib.dll`). Les cinq points dont
dépendait la conception, tels qu'ils sortent réellement :

```
### IMsTscNonScriptable.ClearTextPassword
setter : Void set_ClearTextPassword(System.String)
getter :

### IMsRdpExtendedSettings methods
Void set_Property(System.String, System.Object ByRef)
System.Object get_Property(System.String)

### IMsRdpClient10.RequestClose / GetErrorDescription
RequestClose        : MSTSCLib.ControlCloseStatus RequestClose()
GetErrorDescription : System.String GetErrorDescription(UInt32, UInt32)

### IMsTscAx.Connected
property type : System.Int16
getter        : Int16 get_Connected()

### IMsRdpClient.ExtendedDisconnectReason
property type : MSTSCLib.ExtendedDisconnectReasonCode
getter        : MSTSCLib.ExtendedDisconnectReasonCode get_ExtendedDisconnectReason()

### ControlCloseStatus enum values
controlCloseCanProceed = 0
controlCloseWaitForEvents = 1
```

Écarts par rapport aux signatures supposées, et ce qu'ils imposent :

- `RequestClose()` **retourne** un `ControlCloseStatus` ; il n'y a **pas** de paramètre de
  sortie. Le §6.5 écrivait `RequestClose(out status)` — c'est `var status = RequestClose()`.
- `Connected` est un `short` (`0`/`1`/`2`), pas un `bool`.
- `ExtendedDisconnectReason` est une **énumération** `ExtendedDisconnectReasonCode`, pas un
  `int` : toute comparaison passe par un cast explicite.
- `set_Property(string, ref object)` prend son `VARIANT` **par référence** ; l'appel exige une
  variable locale, pas un littéral.
- `GetErrorDescription(uint, uint)` prend deux **non signés**, alors que `discReason` arrive en
  `int` depuis `OnDisconnected(Int32)`.
- Le setter `set_ClearTextPassword` généré est bien en `System.String` : c'est exactement le
  risque R1, et la raison pour laquelle il n'est **jamais** appelé (§5.2, `ComSecretPut`).

Les événements sont générés par `TlbImp.exe` avec **`/transform:DispRet` obligatoire**. Sans
ce commutateur, trois gestionnaires changent de forme :

```
avec /transform:DispRet (= COMReference)         sans le commutateur
-----------------------------------------------  --------------------------------------------
OnConfirmClose         Boolean Invoke()          Void Invoke(Boolean ByRef)
OnReceivedTSPublicKey  Boolean Invoke(String)    Void Invoke(String, Boolean ByRef)
OnAutoReconnecting     AutoReconnectContinueState  Void Invoke(Int32, Int32,
                         Invoke(Int32, Int32)        AutoReconnectContinueState ByRef)
```

Ligne de commande réellement exécutée par la cible `GenerateMstscInterop` :

```
"…\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\x64\TlbImp.exe"
  "C:\WINDOWS\System32\mstscax.dll" /namespace:MSTSCLib /machine:X64
  /transform:DispRet /silence:3015 /out:"obj\Debug\net10.0-windows\Interop.MSTSCLib.dll"
```

Trois contraintes non négociables tirées de là :

1. **TlbImp x64.** Un `TlbImp.exe` 32 bits est redirigé par WOW64 vers
   `SysWOW64\mstscax.dll` et produit un interop de la vue 32 bits.
2. **`/silence:3015`** supprime les 78 avertissements TI3015 de TlbImp
   (`IMsRdpClientNonScriptable*.get_UIParentWindowHandle` retourne `_RemotableHandle*`,
   non marshalable) — les mêmes que MSBuild remontait en MSB3305. Build à 0 avertissement.
3. **Prérequis** : Windows SDK ou .NET Framework 4.8 Developer Pack installé, documenté dans
   le README ; le chemin est surchargeable par `-p:TlbImpPath=…`.

Comparaison faite : à `/transform:DispRet` près, l'assembly produit par TlbImp est
**identique membre par membre** à celui que produisait `COMReference`. Seule la taille du
fichier diffère (848 896 octets par `COMReference` depuis la vue 32 bits, 878 592 octets par
TlbImp `/machine:X64` depuis `System32`).

---

## Séquence de fermeture observée (Task 6)

```
[close]   RequestClose → controlCloseWaitForEvents (1)
[session] OnDisconnected reason=1 extended=0 "Une erreur interne s’est produite."
[close]   Closed gracefully (OnDisconnected received)
```

- Le contrôle répond `controlCloseWaitForEvents` (`1`) : la branche « attendre l'événement »
  du §6.5 est la branche réelle, pas la branche théorique.
- **`OnConfirmClose` n'a jamais été levé.** Attendre uniquement cet événement aurait produit
  un délai d'attente systématique de 5 s. C'est `OnDisconnected` qui clôt l'attente.
- **`reason = 1`.** La documentation Microsoft de `IMsTscAxEvents::OnDisconnected` donne :
  **`disconnectReasonLocalNotError` (1 (0x1)) — « Local disconnection. This is not an error
  code. »**
  Source : <https://learn.microsoft.com/windows/win32/termserv/imstscaxevents-ondisconnected>
  **Piège** : `GetErrorDescription(1, 0)` retourne pourtant « Une erreur interne s'est
  produite. » Ce texte Windows est **trompeur** pour ce code. Le code 1 ne doit **jamais**
  être présenté comme une erreur (même traitement que le code 3), et son texte
  `GetErrorDescription` ne doit pas être affiché.
- Autre code observé pendant les essais : `reason=3 extended=5` — « Vous avez été déconnecté,
  car une autre connexion a été établie avec l'ordinateur distant. » Confirme que le code 3
  (`disconnectReasonByServer`) est bien une déconnexion normale, et que
  `ExtendedDisconnectReason` porte l'information utile.

**Côté serveur, après fermeture puis reconnexion** : `query session` sur `TEST-VM`
montre **une seule** ligne `rdp-tcp#0  testuser  ID 2  Actif`. **Aucune session
dupliquée ni zombie.** Le protocole du §6.5 est validé.

---

## Interception clavier — réserves attachées au mécanisme retenu

Le `WH_KEYBOARD_LL` fonctionne, mais il n'est pas gratuit. Deux limites à traiter, écrites
ici pour ne pas être redécouvertes au lot 5 :

1. **Portée trop large.** Le hook est global au bureau ; il n'est filtré que par « notre
   processus possède la fenêtre de premier plan ». Il avale donc `Ctrl+K` et `Ctrl+Tab`
   **partout dans l'application**, y compris dans les `TextBox` (recherche, éditeur de
   connexion). À filtrer au lot 5, quand la palette et la recherche existeront : ne pas
   intercepter quand le focus clavier est sur un contrôle de saisie WPF.
2. **Aucune E/S synchrone dans le callback.** Windows applique
   `LowLevelHooksTimeout` (300 ms par défaut) : un callback trop lent est **désinstallé
   silencieusement** par le système. Le callback ne doit faire que décider et poster ; tout
   travail réel part sur le `Dispatcher`.

**Repli documenté pour les environnements verrouillés** : certaines politiques de sécurité
(EDR, stratégies de groupe) bloquent l'installation d'un hook bas niveau. Repli natif du
contrôle : l'événement **`OnFocusReleased`**, levé quand l'utilisateur presse
`Ctrl+Alt+Gauche` / `Ctrl+Alt+Droite` — les combinaisons de sortie de focus prévues par le
contrôle lui-même. Elles n'ouvrent pas la palette mais rendent le focus à l'application,
ce qui suffit pour ne jamais rester prisonnier de la session.

---

## Écarts par rapport à la spec

| Section | Ce que la spec prévoyait | Ce qui a été constaté | Traitement |
|---|---|---|---|
| D2 | CLSID version 13 comme contrôle cible | v13 enregistrée mais **non instanciable** (`CLASS_E_CLASSNOTAVAILABLE`) ; c'est la v12 qui tourne | D2 et §6.1 amendés ; `IsUsable` teste la créabilité |
| D3, §6.1, §11 | Interop par `<COMReference>` | Non compilable par `dotnet build` (MSB4803) ; remplacé par une cible MSBuild `TlbImp.exe` | D3, §6.1 et §11 amendés |
| §5.2, §13 (repli R1) | `ClearTextPassword` via `IDispatch::Invoke` + `VARIANT` | `IMsTscNonScriptable` n'est **pas** duale ; appel direct vtable slot 3 | §5.2 amendé |
| §6.4 | Table des codes sans le code 1 | Code 1 = `disconnectReasonLocalNotError`, **pas une erreur**, mais `GetErrorDescription` en donne un texte d'erreur trompeur | §6.4 amendé |
| §6.5 | `RequestClose(out status)` | `ControlCloseStatus RequestClose()` — valeur de retour | Consigné ici ; §6.5 décrit le mécanisme, pas la signature |
| §6.5 | Attendre `OnDisconnected` **ou** `OnConfirmClose` | `OnConfirmClose` n'est jamais levé | Aucun changement : la spec prévoyait déjà les deux |
| §7.3 | Trois mécanismes d'interception, du plus simple au plus intrusif | Les trois échouent ; un quatrième (`WH_KEYBOARD_LL`) fonctionne | §7.3 amendé |
| §7.3 | `KeyboardHookMode` « valeur à lire au lot 0 » | Valeur **2** vérifiée et posée (`SecuredSettings2.KeyboardHookMode = 2`) | §2 « Vérifications effectuées » amendé |
| §7.1 | WPF-UI pour tous les contrôles | `PasswordBox` **natif WPF** obligatoire (`SecurePassword` ; celui de WPF-UI n'expose qu'une `string`) | §7.1 amendé |
| §6.7 | Sonde binaire réussite/échec | Propriété acceptée, mais flux web **non départagé** de la SSO CredSSP | §6.7 amendé avec la question ouverte |
| §11, §14 | x64 uniquement | Interop généré en `/machine:X64` : **ARM64 non couvert** | §14 amendé |
| §6.4 | — | Le rendu distant **ne se redimensionne pas** avec la fenêtre | **Attendu** : résolution dynamique = D6, lot 4. Aucun changement. |
| §7.1 | — | Les champs de saisie ne s'étirent pas ; pas de texte indicatif sur le `PasswordBox` | Ergonomie, lot 3 |
| — | — | Le DPI est journalisé dans la culture courante (`1,00`) | Défaut mineur, reporté |

---

## Critère de fin de lot (spec §12)

> Une session RDP s'affiche et se ferme proprement ; R1 à R7 ont chacun une décision écrite.

**Atteint.** Session ouverte sur `TEST-VM`, affichée dans la `FluentWindow`, fermée par
le protocole `RequestClose` sans session résiduelle côté serveur. Les sept risques ont une
décision ci-dessus, reportée dans la colonne « Résultat (lot 0) » du §13 de la spec.

**Réserve explicite** : R3 (DPI mixte) n'est pas *levé*, il est *déplacé* — la machine de test
était mono-DPI. Il figure dans `docs/manual-checklist.md`.
