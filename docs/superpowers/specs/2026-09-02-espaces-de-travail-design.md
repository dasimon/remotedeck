# RemoteDeck — Espaces de travail (conception)

**Date** : 2026-09-02
**Auteur** : David Simon (conception assistée)
**Statut** : validé, prêt pour plan d'implémentation
**Base** : `main` @ v0.2.1 + branche `app-icon`, spec produit `docs/superpowers/specs/2026-08-29-remotedeck-design.md`, spec fenêtres détachées `docs/superpowers/specs/2026-09-01-detached-windows-design.md`

> Ce document étend les deux specs précédentes ; il ne les remplace pas. Le protocole de
> fermeture (§6.5 de la spec produit), la chaîne du secret (§5), la `ReconnectPolicy` et le
> re-parenting des fenêtres détachées restent en vigueur tels quels.

---

## 1. Objectif

Ouvrir d'un geste un ensemble de sessions déjà disposé : *« ouvrir l'espace PROD »* → quatre
connexions montées, chacune sur l'écran où on l'attend, celles qui doivent l'être en plein
écran. Aujourd'hui, refaire cette disposition coûte quatre connexions à la main puis quatre
détachements et placements — tous les matins.

Deux notions distinctes répondent à ce besoin et **ne doivent pas être confondues** :

- **Un espace nommé** : composé délibérément, nommé, ouvert à la demande. C'est du contenu.
- **La reprise de la dernière session** : photo automatique de ce qui était ouvert à la
  fermeture, remontée au démarrage suivant. C'est de l'état de fenêtrage.

Elles ont des stockages différents (§3) parce qu'elles n'ont ni la même valeur ni la même
durée de vie.

## 2. Ce que la fonctionnalité n'est pas

Un espace **ne ferme jamais rien**. L'ouvrir ajoute ses sessions à celles qui tournent ; une
connexion déjà ouverte est seulement replacée à l'endroit prévu. Deux espaces ouverts
d'affilée peuvent donc laisser à l'écran plus de sessions que l'un ou l'autre ne décrit.

C'est un choix, pas un oubli : fermer quatre sessions de production sur un clic est
irréversible, et la règle du projet est qu'aucune session ne disparaît sans une action
explicite de l'utilisateur sur cette session-là. La contrepartie assumée est qu'un espace
décrit un **minimum garanti** de ce qui sera à l'écran, jamais un état exact.

## 3. Modèle et stockage

| | Espace nommé | Reprise de la dernière session |
|---|---|---|
| Composition | explicite, par capture (§5) | automatique, à la fermeture |
| Stockage | `connections.db`, schéma **V2** | `settings.json` |
| Survit à la suppression de `settings.json` | oui | non |
| Intégrité avec `Connection` | clé étrangère | aucune (ids filtrés à la lecture) |

Un espace est du contenu composé par l'utilisateur : il mérite la base migrée et la même
protection que les connexions. La reprise de session est de l'état de fenêtrage, au même
titre que `DetachedWindows`, `PaneWidth` ou `WindowMaximized` — elle reste dans
`settings.json`, et la règle documentée « supprimer ce fichier ne coûte que la disposition »
**demeure exacte**.

### 3.1 Schéma V2

Nouveau script ajouté à `SchemaMigrator.Scripts` — jamais d'édition d'un script livré.

```sql
CREATE TABLE Workspace (
  Id          INTEGER PRIMARY KEY,
  Name        TEXT    NOT NULL UNIQUE,
  AutoConnect INTEGER NOT NULL DEFAULT 1,
  CreatedUtc  TEXT    NOT NULL);

CREATE TABLE WorkspaceItem (
  WorkspaceId  INTEGER NOT NULL REFERENCES Workspace(Id)  ON DELETE CASCADE,
  ConnectionId INTEGER NOT NULL REFERENCES Connection(Id) ON DELETE CASCADE,
  Ordinal      INTEGER NOT NULL,
  Detached     INTEGER NOT NULL DEFAULT 0,
  Left         REAL    NULL,
  Top          REAL    NULL,
  Width        REAL    NULL,
  Height       REAL    NULL,
  FullScreen   INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (WorkspaceId, ConnectionId));

CREATE INDEX IX_WorkspaceItem_Connection ON WorkspaceItem(ConnectionId);
```

`Name` est unique : deux espaces homonymes seraient indiscernables dans la palette, qui est
la seule façon de les ouvrir.

`ON DELETE CASCADE` sur `Connection` : supprimer une machine la retire de tous les espaces.
Aucun id ne pourrit, et aucun code n'a à gérer une connexion fantôme. Un espace peut donc
devenir **vide** ; il reste listé, l'ouvrir ne fait rien et le signale (§6.4).

La place détachée reprend exactement les cinq champs de `DetachedWindowPlacement` — même
sémantique, mêmes coordonnées de bureau virtuel. Une entrée `Detached = 0` laisse les cinq à
`NULL` / `0` : une session ancrée n'a pas de fenêtre à placer.

**PRAGMA `foreign_keys` — vérifié, rien à faire.** `SqliteDatabase.Open()` envoie déjà
`PRAGMA foreign_keys = ON` à chaque connexion, et le bundle natif `e_sqlite3` que le projet
embarque est compilé avec `SQLITE_DEFAULT_FOREIGN_KEYS` — c'est la raison pour laquelle EF
Core 3.0 a cessé d'envoyer le pragma lui-même
([doc](https://learn.microsoft.com/ef/core/what-is-new/ef-core-3.x/breaking-changes#low-impact-changes),
[connection strings](https://learn.microsoft.com/dotnet/standard/data/sqlite/connection-strings)).
La contrainte tient donc par deux chemins indépendants.

Ce qui change, c'est son importance : en V1 aucune clé étrangère n'était réellement exercée
(`Connection.CredentialId` est `ON DELETE SET NULL`, jamais éprouvé par un test). Le CASCADE
ci-dessus est le premier à porter du sens. Un test de régression est donc requis — non pour
ajouter la garantie, mais pour empêcher qu'une simplification future de la chaîne de
connexion la retire sans que rien ne le signale.

### 3.2 Reprise de la dernière session

`AppSettings` gagne deux membres :

```csharp
public bool RestoreLastSession { get; set; }              // false par défaut — voir §7
public List<LastSessionEntry> LastSession { get; set; } = [];
```

`LastSessionEntry` porte les mêmes champs qu'un `WorkspaceItem` moins le `WorkspaceId` :
`ConnectionId`, `Ordinal`, `Detached`, la place, `FullScreen`. La liste est réécrite à chaque
fermeture propre de l'application, et seulement là : une fermeture par crash laisse la
précédente, ce qui est le comportement utile.

À la lecture, tout id de connexion qui n'existe plus est **ignoré en silence**. Pas de clé
étrangère ici, donc pas d'autre garantie possible — et prévenir l'utilisateur au démarrage
qu'une machine supprimée il y a trois semaines n'a pas pu être rouverte n'apporte rien.

## 4. Monter un espace

### 4.1 `WorkspacePlan` — la décision, sans WPF

Le cœur de la fonctionnalité est une décision pure, donc elle vit dans `Core` et se teste,
au même titre que `ClosePlan`, `ScreenFit` et `ReconnectPolicy`.

**Entrées** : les items de l'espace ; les connexions qui existent encore ; les écrans
présents *maintenant* ; les sessions déjà ouvertes (id de connexion → ancrée ou détachée).

**Sortie** : une liste ordonnée d'actions, une par item :

| Action | Quand | Effet attendu |
|---|---|---|
| `Activate` | la connexion a déjà une session | rien n'est ouvert ni reconnecté ; la session existante est amenée à l'état que l'item décrit (§4.1.1) |
| `OpenDocked` | pas de session, item ancré | ouverture comme un `Connect` ordinaire |
| `OpenDetached` | pas de session, item détaché | ouverture puis détachement au rectangle calculé |

#### 4.1.1 Une session déjà ouverte

Le cas ambigu, tranché ici une fois pour toutes : la connexion tourne déjà, mais **pas dans
l'état que l'item décrit**.

| État actuel | Item | Décision |
|---|---|---|
| ancrée | ancré | activer l'onglet, rien d'autre |
| détachée | détaché | ramener la fenêtre au premier plan, **et la replacer** au rectangle de l'item |
| ancrée | détaché | **détacher** au rectangle de l'item |
| détachée | ancré | **rattacher** dans le ruban |

Déplacer une session vivante est légitime : c'est du re-parenting, l'opération que le lot 6 a
précisément rendue sûre, et non une fermeture. La règle « un espace ne ferme jamais rien »
(§2) n'est pas entamée — aucune session ne se termine, aucune ne se reconnecte, seule sa
place à l'écran change. C'est d'ailleurs le sens du geste : demander l'espace « PROD », c'est
demander cette disposition-là.

Le rectangle passe par `ScreenFit`, déjà écrit et déjà testé. La garantie est celle
d'aujourd'hui pour les fenêtres détachées, ni plus ni moins : **une fenêtre atteignable**, et
sur un bureau mélangeant des facteurs d'échelle, un placement approximatif — les coordonnées
sont converties avec l'échelle DPI de la fenêtre principale. Si l'écran d'origine a disparu,
la fenêtre atterrit sur un écran réellement connecté plutôt que hors champ.

`WorkspacePlan` ne connaît ni `RdpSession`, ni `SessionWindow`, ni WPF. Il rend des données.

### 4.2 Exécution

`ShellWindow.MountWorkspace(plan)` déroule la liste **en série**, pas en parallèle.

Six négociations RDP simultanées au démarrage d'un poste — VPN à peine monté, carte réseau
qui vient de s'associer — est précisément le cas où « tout connecter » se retourne contre
l'utilisateur : six échecs d'un coup, dont aucun n'est la faute d'une machine. En série, la
première qui répond confirme que le chemin réseau est là.

Chaque action réutilise le code existant sans le modifier : le `Connect` de la palette, et
`DetachTab(tab, placement)` pour les items détachés. Aucun nouveau chemin de connexion,
aucune duplication du protocole.

### 4.3 Échecs

Un échec est **isolé à sa session**. Deux connexions qui échouent sur six laissent quatre
sessions vivantes, et chacune des deux affiche sa propre raison dans son InfoBar, avec
*Reconnect* et *Copy diagnostics*, exactement comme une connexion lancée à la main.

Pas d'écran d'erreur agrégé, pas de reprise groupée, pas de « réessayer l'espace ». La
`ReconnectPolicy` s'applique inchangée : un échec réseau est rejoué cinq fois, et un mot de
passe refusé n'est **jamais** rejoué — la règle qui évite le verrouillage d'un compte Active
Directory compte six fois plus quand six sessions partent ensemble.

### 4.4 `AutoConnect`

Le drapeau est porté **par espace**, pas globalement. À `1` (défaut), monter l'espace connecte
tout. À `0`, le plan est exécuté jusqu'à la création des onglets et des fenêtres, mais aucune
session n'est démarrée : chacune se connecte quand l'utilisateur la sélectionne.

Ce mode existe pour les espaces qu'on ouvre pour *regarder* la disposition, ou depuis un
poste dont on n'est pas sûr du réseau. Il se règle au moment de la capture (§5) et nulle part
ailleurs — il n'y a pas d'éditeur d'espace (§8).

## 5. Créer un espace : la capture

Un espace se crée en **capturant l'état courant**, jamais en le décrivant à froid :

1. L'utilisateur ouvre et place ses sessions à la main, comme aujourd'hui.
2. Palette (`Ctrl+K`) → *Enregistrer la disposition sous…*
3. Une petite fenêtre demande un **nom** et porte la case **Connecter automatiquement**,
   cochée par défaut.
4. Chaque session ouverte devient un `WorkspaceItem` : son `Ordinal` est sa position dans le
   ruban, son `Detached` et sa place sont lus sur la fenêtre réelle.

Deux conséquences qui justifient à elles seules ce choix : il n'y a **aucune fenêtre
d'édition à concevoir, traduire et maintenir**, et il est **impossible de décrire un espace
qui ne se monte pas** — ce qui est enregistré a été vu à l'écran.

Enregistrer sous un nom qui existe déjà **remplace** l'espace, après confirmation explicite.
Le remplacement est la manière normale de faire évoluer un espace, puisqu'il n'y a pas
d'éditeur.

## 6. Interface

Tout passe par la palette : c'est déjà l'entrée unique des commandes, et cela évite un
nouveau pan d'interface.

### 6.1 Commandes

| Commande | Effet |
|---|---|
| *Enregistrer la disposition sous…* | §5. Absente quand aucune session n'est ouverte |
| *Ouvrir l'espace `<nom>`* | une entrée par espace, avec son nombre de connexions en sous-titre |
| *Supprimer l'espace `<nom>`* | suppression, avec la même confirmation à deux temps que les connexions |

Les espaces sont cherchables comme le reste : la recherche floue de `PaletteFilter`
s'applique sans modification.

### 6.2 Fenêtre de nommage

Le seul élément d'interface neuf. Un champ *Nom*, une case *Connecter automatiquement*, deux
boutons. Il suit les conventions déjà posées par `ConnectionEditorWindow` et
`CredentialEditorWindow` : `FluentWindow`, jetons du thème, `Escape` annule, `Entrée` valide,
validation du nom vide et du doublon avant fermeture.

### 6.3 Localisation

Toutes les chaînes passent par `Strings.resx` et `Strings.fr.resx`, comme le reste de
`RemoteDeck.App`. Aucune chaîne d'interface en dur.

### 6.4 Espace vide

Un espace dont toutes les connexions ont été supprimées reste listé, son sous-titre indiquant
zéro connexion. L'ouvrir n'ouvre rien et affiche un InfoBar disant que l'espace ne référence
plus aucune connexion existante. Il n'est pas supprimé automatiquement : c'est le nom que
l'utilisateur a choisi, et sa disparition silencieuse serait plus déroutante que sa présence.

## 7. Décisions tranchées

- **La reprise de la dernière session est désactivée par défaut.** Lancer RemoteDeck ne doit
  se connecter à rien tant que l'utilisateur ne l'a pas demandé. Un réglage l'active.
- **Un espace ne mémorise pas la session active**, ni le plein écran d'une session ancrée —
  seulement les fenêtres détachées, exactement comme la mémorisation par connexion
  d'aujourd'hui. Le plein écran ancré n'existe pas dans le produit.
- **La place d'un espace prime sur la mémorisation par connexion.** Un item détaché porte sa
  propre place ; la mémorisation par connexion de `settings.json` ne sert que de repli quand
  l'item n'en a pas. C'est ce qui permet à la même machine d'être à gauche dans un espace et
  en plein écran à droite dans un autre.
- **Monter un espace n'écrit pas dans la mémorisation par connexion.** `RememberPlacement`
  n'est déclenché que par une fin de glisser de caption, un rattachement et la fermeture de
  l'application ; un placement programmatique n'en fait pas partie. Ouvrir « INCIDENT »
  n'écrase donc pas la place que « PROD » utilise comme repli.

  **Nuance à assumer** : à la *fermeture* de l'application, les fenêtres encore ouvertes sont
  mémorisées là où elles se trouvent — y compris là où un espace les a mises. C'est le
  comportement actuel et il n'est pas modifié : le repli par connexion reflète « là où cette
  machine était la dernière fois », ce qui reste vrai. La place d'un espace, elle, est dans la
  base et ne bouge pas.

## 8. Hors périmètre

Délibérément absents de ce lot, à revoir seulement si l'usage les réclame :

- Éditeur d'espace (composer ou modifier sans ouvrir les sessions).
- Dossiers ou groupes d'espaces.
- Export, import, partage d'un espace entre postes.
- Un espace ouvert automatiquement au lancement (distinct de la reprise de session).
- Raccourci clavier dédié par espace.

## 9. Risques

| Risque | Portée | Traitement |
|---|---|---|
| `PRAGMA foreign_keys` retiré par une refonte future | le CASCADE deviendrait décoratif, des items pointeraient des connexions mortes | déjà actif par deux chemins (§3.1) ; verrouillé par un test de régression |
| Six connexions au démarrage sur un réseau pas prêt | six échecs simultanés, illisibles | ouverture en série (§4.2) ; reprise désactivée par défaut (§7) |
| Écrans absents ou échelles mixtes | fenêtres hors champ | `ScreenFit`, déjà en place ; garantie « atteignable », pas « au pixel » |
| Accumulation de sessions après deux espaces | plus de sessions que voulu | assumé (§2) ; documenté dans le README |
| Migration V2 sur une base V1 existante | perte de données | script forward-only en transaction, `SchemaVersion` déjà en place, test de migration V1→V2 |
| Espace ouvert deux fois | sessions dupliquées | impossible : une connexion a au plus un onglet, l'action devient `Activate` |

## 10. Tests

Automatisables dans `RemoteDeck.Core.Tests`, et c'est la raison d'être du découpage :

- `WorkspacePlanTests` — chaque branche de la table §4.1 ; connexion supprimée ; écran
  disparu ; session déjà ouverte ancrée alors que l'item la veut détachée, et l'inverse ;
  espace vide ; `AutoConnect = 0`.
- `WorkspaceRepositoryTests` — CRUD, unicité du nom, remplacement, ordinal préservé,
  aller-retour de la place.
- `SchemaMigratorTests` — V1 → V2 sur une base peuplée ; `CurrentVersion` ; refus d'une base
  V3 (`SchemaTooNewException`, déjà couvert).
- Cascade — supprimer une `Connection` retire ses `WorkspaceItem`. Le test échouerait si
  l'application des clés étrangères venait à disparaître de la chaîne de connexion : c'est
  sa raison d'être (§3.1).
- `SettingsStoreTests` — aller-retour de `LastSession`, ids inconnus ignorés, défaut à vide.

Non automatisable, donc à ajouter à `docs/manual-checklist.md` : le montage réel sur
plusieurs écrans, l'ordre de connexion en série, l'apparence de la fenêtre de nommage dans
les deux thèmes et les deux langues.
