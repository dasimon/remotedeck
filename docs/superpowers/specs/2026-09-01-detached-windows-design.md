# RemoteDeck — Fenêtres de session détachées (conception)

**Date** : 2026-09-01
**Auteur** : David Simon (conception assistée)
**Statut** : validé, prêt pour plan d'implémentation
**Base** : `main` @ v0.1.0 (lots 0→5), spec produit `docs/superpowers/specs/2026-08-29-remotedeck-design.md`

> Ce document étend la spec produit ; il ne la remplace pas. Les décisions D1→D12, la
> chaîne du secret (§5) et le protocole de fermeture (§6.5) restent en vigueur tels quels.

---

## 1. Objectif

Permettre de sortir une session RDP de la fenêtre principale pour l'afficher dans sa
propre fenêtre, sans le panneau ni la barre d'onglets, et de la remettre ensuite. Cas
d'usage réel : deux écrans, deux bureaux distants en plein écran, la fenêtre de gestion
sur un troisième plan (ou réduite).

**Critère de fin** : deux sessions détachées, chacune en plein écran sur son écran,
restent connectées pendant que la fenêtre principale continue de fonctionner ; les
rattacher ne coupe rien ; fermer l'application ne laisse aucune session zombie.

---

## 2. Fait fondateur : le déplacement ne coupe pas la session

Une sonde (branche jetable `spike/detach-window`, 2026-08-31, contrôle v12, VM de test,
mode d'affichage Dynamic) a mesuré les trois techniques envisageables. Résultat **mesuré,
non supposé** : aucune n'émet `OnDisconnected`, et le HWND de l'`AxHost` est identique
avant et après déplacement dans les trois cas.

| Technique | Résultat | Retenue |
|---|---|---|
| **A** — nouveau `WindowsFormsHost` dans la nouvelle fenêtre, même `AxHost` en `Child` | Rendu et saisie corrects, aller-retour possible. Mais `session.Host` reste dans `SessionsArea` : `RdpSession` mesure la mauvaise fenêtre, donc la résolution dynamique se trompe de cible. | non |
| **B** — déplacer le `WindowsFormsHost` existant d'un conteneur à l'autre | Même HWND **et même parent Win32** : la plus petite perturbation des trois. Rendu, saisie, aller-retour vérifiés ; c'est la seule où la résolution dynamique continue de fonctionner (fenêtre portée à 1400×900, bureau distant suivi, aucun échec `[display]`). | **oui** |
| **C** — `SetParent` Win32 sur le HWND du contrôle | Fonctionne tant que la fenêtre vit, mais la fermer **détruit le HWND du contrôle** (`axHandleCreated=False`) en laissant une session déclarée connectée sur une zone noire. Géométrie entièrement manuelle, résolution distante en boîte aux lettres. | non |

Conséquence de conception : **le détachement est un déplacement d'un `WindowsFormsHost`
dans l'arbre visuel WPF**, rien de plus. Aucune reconnexion, aucune recréation de contrôle,
aucun secret re-présenté.

Point mesuré à ne pas perdre : sur A et C, fermer la fenêtre détachée laissait la session
vivante mais **invisible et sans propriétaire**. La conception l'interdit explicitement
(§6).

---

## 3. Modèle

`SessionsViewModel` reste le propriétaire unique de toutes les sessions. Chaque onglet
gagne un emplacement :

```
SessionPlacement = Docked | Detached(SessionWindow)
```

Une session détachée **reste dans la collection** `Tabs` : elle quitte seulement la barre
d'onglets visible. Tout ce qui raisonne sur l'ensemble des sessions — palette de
commandes, compte de sessions, fermeture globale, politique de reconnexion — continue de
fonctionner sans connaître l'emplacement.

**`SessionWindow`** (nouvelle) : `FluentWindow` minimale portant, de haut en bas, une
barre de titre fine (pastille d'état + nom + hôte), l'`InfoBar` de cette session, puis la
zone qui accueille le `WindowsFormsHost`. Ni panneau, ni barre d'onglets, ni barre de
session complète : les actions (Reconnect, Cancel, Copy diagnostics) apparaissent dans
l'`InfoBar`, comme aujourd'hui.

---

## 4. Détacher et rattacher

**Détacher** — trois chemins, un seul mécanisme :

1. **Glisser** l'onglet hors de la barre : au-delà de **40 px vers le bas**, le glisser
   cesse d'être un réordonnancement et devient un détachement ; au lâcher, la fenêtre
   apparaît sous le curseur.
2. **Palette** (`Ctrl+K`) : *Detach current session*.
3. **`Ctrl+Shift+D`** sur la session active.

**Rattacher** — glisser la `SessionWindow` par sa barre de titre au-dessus de la barre
d'onglets de la fenêtre principale (zone de dépôt mise en évidence), ou *Reattach* dans la
palette, ou `Ctrl+Shift+D` depuis la fenêtre détachée.

**Mécanique (technique B)** : `SessionsArea.Children.Remove(host)` puis affectation du
même `host` au conteneur de la `SessionWindow` — et l'inverse au retour.
`RdpSession` doit être averti du changement de fenêtre : il **réabonne** son
`SizeChanged` et **relit le DPI** de la nouvelle fenêtre. Sans cela, la résolution
dynamique calcule sur l'ancienne fenêtre — c'est exactement le défaut constaté sur la
technique A.

---

## 5. Plein écran

`ContainerHandledFullScreen = true` sur le contrôle : `Ctrl+Alt+Pause` cesse d'agir seul
et lève `OnRequestGoFullScreen` / `OnRequestLeaveFullScreen`, que RemoteDeck traite en
basculant la `SessionWindow` (`WindowStyle=None` + `Maximized` sur l'écran courant).
`F11` fait la même chose. On conserve ainsi notre habillage et notre `InfoBar` au lieu de
la barre de connexion de Microsoft.

Justification du détour : `IMsRdpClient::FullScreen` est modifiable session connectée mais
**aucune API ne choisit l'écran** — un contrôle passe en plein écran sur le moniteur de sa
fenêtre. C'est précisément pourquoi « plusieurs bureaux en plein écran » exige une fenêtre
par écran, et non un réglage du contrôle.

La restriction documentée de `ContainerHandledFullScreen` (« sans effet ») ne vise que la
variante *safe for scripting* ; RemoteDeck utilise la coclasse *NotSafeForScripting*, donc
la propriété s'applique. **À confirmer par une sonde au premier lot d'implémentation**
(risque R3) ; repli documenté : plein écran WPF piloté par `F11` seul, sans passer par le
contrôle.

---

## 6. Cycle de vie et fermeture

- **Croix d'une `SessionWindow`** → déconnecte cette session par le protocole §6.5
  (`RequestClose`, attente, repli `Disconnect()`), puis ferme la fenêtre. Même
  comportement que fermer son onglet : c'est ce qu'attend quiconque voit une fenêtre de
  bureau distant.
- **Fermeture de la fenêtre principale** → ferme l'application :
  `ShutdownMode.OnMainWindowClose`. `CloseAllAsync` couvre les sessions détachées comme
  les autres ; budget inchangé par session (5 s), plafond global porté de 15 s à **30 s**
  puisque le nombre de sessions n'est plus borné par la lisibilité d'une barre d'onglets.
- **Aucune session sans propriétaire.** Si un rattachement échoue, la session reste dans
  sa fenêtre et l'`InfoBar` le dit. L'état « vivante mais invisible » observé sur les
  techniques A et C au cours de la sonde est un défaut, pas un état acceptable.

---

## 7. Clavier

Le hook bas niveau route désormais vers la **fenêtre active** de l'application, et non
plus vers la fenêtre principale. Dans une `SessionWindow` :

| Raccourci | Effet |
|---|---|
| `Ctrl+W` | Ferme cette session (donc la fenêtre) |
| `F11` / `Ctrl+Alt+Pause` | Bascule le plein écran de cette fenêtre |
| `Ctrl+K` | Ouvre la palette, centrée sur cette fenêtre |
| `Ctrl+Shift+D` | Rattache |
| `Ctrl+Tab`, `Ctrl+B` | Sans objet ici : non interceptés |

La règle du lot 5 — ne pas manger les frappes quand un champ de saisie a le focus, sur le
chemin du hook **et** sur celui des `KeyBinding` WPF — s'applique inchangée.

---

## 8. Mémorisation

`settings.json` gagne une entrée par connexion ayant été détachée au moins une fois :
écran, géométrie, état plein écran. Détacher à nouveau la même machine la replace où elle
était. Sans cette mémoire, la fonctionnalité oblige à repositionner deux fenêtres à chaque
session de travail, ce qui annule son intérêt.

Les données métier restent dans `connections.db` ; la géométrie est une préférence
d'affichage, donc `settings.json` (§7.2 de la spec produit).

---

## 9. Hors périmètre

- **Multi-écran RDP réel** (`UseMultimon`, une session qui s'étale sur plusieurs écrans) —
  autre sujet que « une session par écran », et déjà hors v1 (§14).
- Fenêtre détachée **sans** session, ou regroupant **plusieurs** sessions.
- Détachement vers un autre poste, synchronisation d'agencement entre machines.

---

## 10. Risques

| # | Risque | Détection | Repli |
|---|---|---|---|
| **R1** | DPI différent entre deux écrans → résolution demandée fausse | Premier lot ; vérification humaine sur deux écrans à échelles différentes | `RdpSession` relit le DPI à chaque changement de fenêtre ; à défaut, repli `SmartSizing` déjà en place |
| **R2** | Fermeture de l'application avec des fenêtres détachées → session zombie | `query session` côté serveur, vérification humaine | `ShutdownMode.OnMainWindowClose` + budget global 30 s |
| **R3** | `ContainerHandledFullScreen` ne se comporte pas comme documenté | Sonde au premier lot | Plein écran WPF piloté par `F11` seul |
| **R4** | Le glisser hors barre entre en conflit avec le réordonnancement existant | Vérification humaine | Seuil vertical de 40 px ; à défaut, détachement par commande seulement |

---

## 11. Tests

**Automatisables** (`RemoteDeck.Core.Tests` et logique de placement) : transitions
`Docked` ↔ `Detached`, calcul du budget de fermeture avec N sessions, sérialisation et
relecture de la géométrie mémorisée, choix de l'écran quand la géométrie enregistrée ne
correspond plus à aucun écran connecté.

**Vérification humaine** (`docs/manual-checklist.md`, nouvelle section) : glisser hors
barre, rattacher par glisser, deux sessions plein écran sur deux écrans, DPI mixte,
`Ctrl+W` dans une fenêtre détachée, fermeture de l'application avec deux fenêtres
détachées suivie d'un `query session`, réouverture au même emplacement.
