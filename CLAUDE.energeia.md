# ⛔ TU N'ES PAS CHEZ CoplayDev — CE DÉPÔT EST NOTRE FORK

> Lis ce fichier AVANT `CLAUDE.md`. `CLAUDE.md` est **celui de l'amont**, conservé intact exprès :
> il reste vrai sur l'architecture, les patterns et les tests, et **faux sur une seule chose — le
> flux de contribution.** Ce fichier-ci ne le remplace pas, il le borne.

## L'ordre du PO, ses mots, `[2026-08-29 ~18:5x]`

> *« fork le repository chez nous du tiers puis faire que cela soit notre version du plugin puis
> fait tout les correctifs refontes , developpements necessaire a nos besoin dedans c'est ca mon
> idéee **pas : Remonter nos correctifs chez eux** … ca nous donne la liberté totale de faire ce
> qu l'on veut s'en devoir refaire ce qui est censé etre bien fait (a confirmer et evauluer bien
> sur) »*

⛔⛔ **LA CONTRIBUTION AMONT A ÉTÉ ÉCARTÉE PAR LUI, PAS PAR UN POSTE. NE LA RE-PROPOSE PAS.**
⚠ Et `CLAUDE.md` §*What Not To Do* dit *« Don't commit to `main` directly — branch off `beta` for
PRs »*. **Cette ligne appartient à l'amont et ne nous concerne pas** : notre branche est
`energeia`, et elle ne remonte nulle part. *Un fichier auto-chargé qui prescrit un flux abandonné
est un ordre contraire — c'est pour le neutraliser que ce document existe.*

## Ce que ce dépôt est, exactement

```
origin    github.com/leiha/unity-mcp        branche `energeia`   ⬅ le SSOT, et il est PUBLIÉ
upstream  github.com/CoplayDev/unity-mcp    branche par défaut : `beta` (⚠ pas `main`)
base      tag v9.7.3 — la version que l'atelier fait tourner ; l'amont est en v10.1.2
```
⭐ **Il porte les DEUX moitiés du pont** : `MCPForUnity/` (le plugin Unity) **et** `Server/` (le
serveur MCP Python, `mcpforunityserver`). Elles vivent au même sha : **elles ne peuvent pas
diverger**, et `scripts/doctor.sh` du dépôt `energeia` le vérifie (porte `MCP`).

## Comment il est CONSOMMÉ — et les deux pièges qui rendent le fork décoratif

```
energeia/Packages/manifest.json     "com.coplaydev.unity-mcp": "file:../vendor/mcp-for-unity/MCPForUnity"
energeia/scripts/pont/…​.service      uv run --frozen --project …/vendor/mcp-for-unity/Server mcp-for-unity
```
⛔⛔ **NE REVIENS JAMAIS À `uvx --from <chemin>` — mesuré aux deux bords le 2026-08-29** : `uvx`
sert un **build EN CACHE**, et une modification de `Server/src/` reste **INVISIBLE à l'exécution**
(0 occurrence d'un marqueur inséré) ; `uv run --frozen --project` la prend en compte en 2,08 s.
⛔ **Ni à `uvx --from mcpforunityserver==X`** : c'est le paquet PUBLIC. `retour-stdio.sh` le faisait
et annulait le fork **au moment précis où l'on s'en sert** — quand tout va mal. Corrigé, et
`doctor.sh` rougit désormais dessus.

⚠⚠ **ET UN SKILL LIVRÉ ICI PEUT ANNULER LE FORK EN UNE COMMANDE** : `mcp-source`
*(`.claude/skills/`, auto-proposé dès qu'on touche ce dossier)* — *« Switch MCP for Unity package
source [main|beta|branch|local] »*. **Il réécrit le `manifest.json` du projet consommateur.**
⇒ ⛔ **ne l'invoque pas ici** ; notre montage est un chemin `file:` vers ce sous-module, et c'est
`doctor.sh` (porte `MCP`) qui s'en aperçoit — *après coup.*

## ⛔⛔ CE QUE COÛTE UN `.cs` ÉCRIT ICI — et rien ne te le dit au moment où tu l'écris

```
energeia/Packages/manifest.json   "com.coplaydev.unity-mcp": "file:../vendor/mcp-for-unity/MCPForUnity"
```
⇒ ***ce plugin est un package LOCAL du projet consommateur : tout `.cs` que tu touches sous
`MCPForUnity/` est importé, recompilé et RECHARGE LE DOMAINE — donc il SORT DU PLAYMODE, exactement
comme un `.cs` d'`Assets/`.*** Neuf sessions partagent cet éditeur, et un rechargement leur coûte de
2 à 9 minutes.
⛔ **Ton lot d'outil n'est donc pas moins cher que le leur : il se prépare hors du dossier, se pose
d'un bloc, et son import s'annonce au superviseur.** *Le chemin `file:` est écrit deux sections plus
haut depuis toujours ; sa conséquence ne l'était nulle part.*
⚠ **`Server/` (Python), lui, ne coûte RIEN à l'éditeur** — il vit dans le serveur MCP, pris en compte
en ~2 s par `uv run --frozen --project`. **Les deux moitiés du pont n'ont pas le même prix : sache
laquelle tu touches.**

## Nos correctifs, et où ils vivent

⛔ **Ne recopie aucune liste ici — elle a été périmée de treize correctifs.** Elle se re-dérive :
```sh
git -C vendor/mcp-for-unity log --format='%h %s' v9.7.3..HEAD
```
⭐ **Chaque correctif porte son numéro `PONT-NN` en tête de message, et son RÉCIT complet — ce qu'on
voulait faire, ce que l'outil a répondu, ce qui a été renoncé — vit dans une seule file :**
`energeia/Packages/com.energeia.pont/PROBLEMES-DE-L-OUTIL.md`. *Le `git log` dit ce qui a changé ;
la file dit POURQUOI, et c'est elle que le PO a demandée.*

⭐⭐ **LE DÉFAUT DE FOND QUE CES CORRECTIFS FERMENT UN À UN, et c'est le critère du PO — *« le plus
parfait pour l'utilisation d'un LLM »* :**
> ***Une capacité qu'un chemin de REFUS ne nomme pas n'existe pas.***
Un outil documente ses capacités **là où on les lit quand tout va bien**, et se tait **là où on les
cherche**. `TESTASM`, `NEW_TEST_OUTSIDE_ASSETS`, `IGNORE_STALE`, `init_timeout` : quatre capacités
réelles, quatre refus muets, quatre postes bloqués sur un geste faisable.
⇒ **Quand tu ajoutes un chemin d'échec ici, la question n'est pas *« mon message est-il exact ? »*
mais *« nomme-t-il ce qui débloque ? »***
⚠ **Et sa borne, payée le 2026-08-30** : un refus rapporte ce qu'il a **OBSERVÉ** et ne conclut rien
à la place de son lecteur. *Un premier jet de PONT-21 faisait dire au refus « ZERO test cases ran » —
c'était faux, les cas avaient tourné et étaient verts. Rendre un message « utile » en tranchant pour
son lecteur est la façon dont cet outil se remet à mentir.*
⭐ **Nos OUTILS, eux, ne sont PAS ici — et c'est délibéré.** `ui_find`, `di_state`, `domain_state`
vivent dans `energeia/Packages/com.energeia.pont/`, notre propre assembly, par le point
d'extension `[McpForUnityTool]`. ⇒ ***une montée de version amont ne peut pas les emporter.***
**Ce qui va dans CE dépôt est ce qui CORRIGE le pont ; ce qui répond à un besoin de jeu reste
chez le jeu.**

## Tirer l'amont, le jour où on le voudra

```sh
git fetch upstream
git -C . diff v9.7.3..upstream/beta -- MCPForUnity Server   # le coût réel, avant tout geste
git rebase v10.1.2                                          # nos correctifs par-dessus (compte : le git log ci-dessus)
```
⚠ **Le `--check` d'un `git apply` de nos correctifs sur la cible est la seule mesure honnête de
notre dette de fork.** ⛔ Et la montée **n'achète PAS** l'issue amont #1299 (l'éditeur qui a lancé
le serveur le tue en quittant) : elle est toujours ouverte en v10.1.2.

## Le standard de l'amont qu'on GARDE, parce qu'il est le nôtre aussi

`CLAUDE.md` §*Code Philosophy* — **« Every new feature needs tests »**, et le cliquet
`Server/tests/test_tool_test_symmetry.py` le fait respecter. ⭐ **PONT-07 a d'abord été gravé sans
juge : c'est la seule dette qu'on se soit permise ici, et elle a été payée dans la journée.**
