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

## Nos correctifs, et où ils vivent

```
PONT-01..06   d95d0b0d   pong qui nomme son chemin · Roslyn au lieu du repli CodeDom · un client
                         ne coupe plus les autres · un Timeout cesse d'être une exécution fantôme ·
                         le timeout désigne le bon coupable · interruption au-delà de 20 s
PONT-07       a08d9606   un outil custom mal nommé faisait tomber TOUS les autres, en silence
              938220d6   son juge : Server/tests/test_custom_tool_service_registration_isolation.py
```
⭐ **Nos OUTILS, eux, ne sont PAS ici — et c'est délibéré.** `ui_find`, `di_state`, `domain_state`
vivent dans `energeia/Packages/com.energeia.pont/`, notre propre assembly, par le point
d'extension `[McpForUnityTool]`. ⇒ ***une montée de version amont ne peut pas les emporter.***
**Ce qui va dans CE dépôt est ce qui CORRIGE le pont ; ce qui répond à un besoin de jeu reste
chez le jeu.**

## Tirer l'amont, le jour où on le voudra

```sh
git fetch upstream
git -C . diff v9.7.3..upstream/beta -- MCPForUnity Server   # le coût réel, avant tout geste
git rebase v10.1.2                                          # nos 3 commits par-dessus
```
⚠ **Le `--check` d'un `git apply` de nos correctifs sur la cible est la seule mesure honnête de
notre dette de fork.** ⛔ Et la montée **n'achète PAS** l'issue amont #1299 (l'éditeur qui a lancé
le serveur le tue en quittant) : elle est toujours ouverte en v10.1.2.

## Le standard de l'amont qu'on GARDE, parce qu'il est le nôtre aussi

`CLAUDE.md` §*Code Philosophy* — **« Every new feature needs tests »**, et le cliquet
`Server/tests/test_tool_test_symmetry.py` le fait respecter. ⭐ **PONT-07 a d'abord été gravé sans
juge : c'est la seule dette qu'on se soit permise ici, et elle a été payée dans la journée.**
