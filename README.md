# Dynamic NPC Relationships

A BepInEx plugin that allows previously unassigned Silverpine NPC pairs to
develop persistent relationships from their actual interactions.

**Author:** saelac

**Development assistance:** ChatGPT

**Current version:** 1.17.1

The plugin has a hard dependency on **Modding Tools Menu 1.9.1 or later**, using
the BepInEx identity `Saelac.Silverpine.ModdingTools`. It compiles against
Newtonsoft.Json but excludes the runtime asset, using the single shared
`Newtonsoft.Json.dll` installed by Modding Tools instead of placing another
copy in the plugin folder.

Runtime NPC Editor integration is optional. When a compatible editor build is
installed, it discovers this plugin at runtime and adds the **Dynamic** tab. The
editor continues to work without Dynamic NPC Relationships, and this plugin
continues to track relationships without the editor once a run has been
enabled.

## Installation

1. Install BepInEx 5 and Modding Tools Menu 1.9.1 or later for Silverpine.
2. Place `DynamicNPCRelationships.dll` in
   `BepInEx/plugins/DynamicNPCRelationships/`.
3. Start the game once to generate the BepInEx configuration.
4. Optionally install Runtime NPC Editor to manage save-specific dynamic
   relationships through its **Dynamic** tab.

Do not install a separate Newtonsoft.Json copy for this plugin. Modding Tools
provides the shared runtime assembly.

## Eligibility and compatibility

New runs begin with the save-wide system disabled. After the player enables it
from Runtime NPC Editor's **Dynamic** tab, the run initializes allowed
symmetric pairs from their actual native relationship:

- A pair absent from both NPC relationship dictionaries starts as Stranger at
  `StrangerThreshold`.
- A native Stranger starts as Stranger at `StrangerThreshold`.
- A native Acquaintance starts at `AcquaintanceThreshold`.
- A native Friend starts at the greater of `FriendReviewScore` and
  `FriendRetentionScore`, plus `LoadedFriendStartingScoreBonus` (five by
  default).

Initialization is retried from the plugin's managed update loop until the NPC
roster has remained stable across several checks. This covers new-game paths
that do not raise Silverpine's finished-loading-save callback. A run whose JSON
was previously left uninitialized with zero pairs is repaired automatically the
next time it is loaded; existing initialized run data is never rebuilt.

Each run also records the stable base or `custom:<final NPC name>` identities it
has processed. The plugin hooks the completion of Silverpine's
`CustomNPCManager.SpawnInNPCs`, after custom properties and authored
relationships have been applied, and schedules one reconciliation pass for
that load or new game. Newly seen custom identities receive all eligible
dynamic pairings exactly once, then roster reconciliation becomes idle until
another save is loaded or a new game begins. Removing/resetting one
relationship does not make the reconciler recreate it, and custom slot movement
remains irrelevant because the persisted identity is the NPC's final name
rather than its slot.

Asymmetric pairs and special authored tiers such as family relationships remain
outside dynamic control. A custom role title does not prevent an otherwise
eligible Stranger, Acquaintance, or Friend pair from being initialized. The
exact directional role titles remain active at Stranger, Acquaintance, and
Friend. Enemy, Lover, and Married temporarily replace them; leaving any of
those overriding stages, disabling the plugin, or resetting the pair restores
the original title.
If another plugin later changes a managed pair to an incompatible value, this
plugin suspends that pair instead of fighting the external override.

Base NPCs use their stable `NPCName`. Custom NPCs use their final custom name,
so custom slot reassignment does not move relationship data to the wrong NPC.

## Progression

- Managed Strangers become Enemies at score `-50` by default. An Enemy pair
  returns to Stranger as soon as its score rises above `EnemyThreshold`. Both
  transitions are deterministic and never query the model.
- Managed Acquaintances fall to Stranger at score `-20` by default.
- Managed Strangers rise to Acquaintance at score `0` by default. This is a
  deterministic transition and never queries the model.
- After a completed multi-NPC conversation, the Silverpine model receives the
  completed transcript and independently answers whether each eligible NPC
  pair's interaction was positive and whether it was negative, matching the
  base game's player-NPC relationship checks. Positive takes precedence if the
  model unusually answers yes to both. A positive result adds
  `SharedConversationGain`; a negative result subtracts
  `SharedConversationLoss`; two no answers classify the conversation as neutral
  and make no score or tier change. All completed conversations still count as
  daily contact and increment the direct interaction count. Classifications are
  queued and processed one at a time, preserving conversation order and
  preventing overlapping results from being applied out of order. A configured
  gain or loss of zero remains zero while the conversation still counts.
- A successfully delivered NPC rumor adds `RumorExchangeGain` and counts as
  daily contact even when that configured gain is zero.
- At each in-game day boundary, Enemy, Stranger, Acquaintance, and Friend pairs lose
  `DailyScoreDecrease` for each completed day in which they shared neither a
  conversation nor a successfully delivered rumor. This decrease stops at the
  lower boundary of the pair's
  current stage and therefore cannot cause a demotion. Lover and Married pairs
  are exempt.
- The Stranger-to-Acquaintance transition remains deterministic. The model
  classifies the preceding conversation's sentiment but does not approve that
  stage transition.
- When model reviews are enabled, a rumor may make a promotion eligible but
  cannot launch its query. The pair must complete a subsequent shared
  conversation so the review has fresh conversation context. The Dynamic tab
  displays this as a pending promotion.
- Crossing `FriendReviewScore` queues one Acquaintance-to-Friend review.
- Crossing `LoverReviewScore` queues one Friend-to-Lover review.
- Crossing `MarriageReviewScore` after a completed shared conversation creates
  a save-scoped pending proposal. The pair remains Lovers and no proposal query
  runs until the player later starts a native multi-NPC dialog containing both
  NPCs. At that point, one lover's next generated turn is replaced by an
  in-character proposal and the other's following turn is replaced by an
  honest in-character response. A final classification query evaluates that
  exact response. The pair marries only when the response clearly accepts;
  ambiguity, postponement, or rejection denies the promotion.
- When a Friend, Lover, or Marriage promotion query succeeds, the score is
  raised to at least that tier's review threshold plus
  `ApprovedPromotionScoreBonus` (five by default). A score already above that
  value is left unchanged. This prevents the newly approved tier from sitting
  directly on its threshold; automatic Friend/Lover promotions do not receive
  this query-success buffer.
- A denied review cannot repeat until the pair earns
  `DeniedReviewAdditionalScore` more points.
- Lover and Marriage rejections are counted separately for each direction. Two
  rejections of the same pursuing NPC (configured through
  `RomanticRejectionsBeforeLockReview`) cause the rejecting NPC to receive one
  additional model query asking whether that relationship could ever become
  willingly acceptable. A yes answer clears that directional rejection count
  and leaves future attempts possible. A no answer permanently sets the pair's
  current Friend or Lover stage as its promotion ceiling.
- A ceiling-locked pair never runs another promotion query and can never exceed
  that ceiling. Negative interactions can still demote it normally. If its score
  later rebuilds, previously achieved stages up to the ceiling are restored
  deterministically at their configured thresholds without an LLM query.
- Only one promotion review runs at a time.

Silverpine's native `NPCRelationship` enum has no Lover member. The plugin
therefore represents a lover as native `Friend` plus a symmetric `lover`
custom relationship label. This makes Silverpine's existing prompt builder
describe each NPC as the other's lover without assigning the story-specific
`Beloved_Sister` enum value.

Enemy is represented as native `Stranger` plus a symmetric `enemy` custom
relationship label. This causes the normal prompt builder to describe each NPC
as the other's enemy. If the pair had directional custom role titles, those
titles are preserved in the save and restored when the pair rises above the
Enemy threshold.

Silverpine also has no native marriage tier. A married pair remains native
`Friend`, with directional custom labels based on each described NPC's resolved
sex: `wife` for female, `husband` for male, and `spouse` for intersex. Thus
Aldric's prompt can describe Rosalyn as his wife while Rosalyn's prompt
describes Aldric as her husband. Marriage is tracked per pair and deliberately
has no exclusivity constraint, so an NPC may have multiple spouses.

While a pair is Married, every individual negative score change is capped at
`-1`, including a negatively classified conversation or a negative delta from
the public integration API. Positive gains are unaffected. Repeated negative
interactions can still eventually lower the score beneath the marriage
retention threshold and cause a divorce.

Both proposal lines are written into every participant's native multi-dialog
history and into the couple's memories. The proposer speaks first in the chat
box, Continue switches to the recipient's spoken response, and the dialog then
returns to ordinary player input and Silverpine's normal next-speaker flow; it
is not forced closed. A rejected proposal is retained as part of their shared
history. The proposal does not open a standalone dialog and cannot occur unless
both NPCs are already in the player-initiated multi-dialog. Proposal selection
alternates naturally with interaction-count parity, so either lover can become
the proposer; gender does not decide who proposes.

## Public marriage and divorce knowledge

Marriage creates one save-scoped public world knowledge fact for the pair. The
fact is exposed to Silverpine-town NPCs, states that they are married, and
includes both directional titles (for
example, Rosalyn is Aldric's wife and Aldric is Rosalyn's husband). The plugin
uses Silverpine's native `WordCloud.Create` model query once when the fact is
created, caches the resulting nouns, verbs, adjectives, and keywords, and ranks
the fact against conversation context using the same deep/shallow relevance
weighting used by ordinary knowledge. It does not regenerate the word cloud on
every prompt or load.

If a Married pair falls below `MarriageRetentionScore`, they divorce. The
marriage fact is removed, a public divorce fact replaces it, and a new word
cloud is generated and cached for that fact. Divorce resets the pair directly
to Acquaintance and resets its score to `AcquaintanceThreshold` (zero by
default), so they must rebuild Friend and Lover before marrying again. Only one
Marriage or Divorce fact can exist for a given pair at a time.

Negative deltas supplied through `Plugin.AddRelationshipScore`, together with
negatively classified conversations, can produce deterministic downgrades
using the configured retention thresholds. Rumor events remain positive.

## Default NPC relationship policy

On boot the plugin creates and maintains:

`BepInEx/config/DynamicNPCRelationships/defaultRelationships.json`

Every currently available base and custom NPC is added without overwriting
existing user choices. Base NPC IDs use their stable `NPCName`; custom NPC IDs
use `custom:<final NPC name>`, so custom slot movement does not change the
policy identity. Entries that are no longer currently available are retained.

```json
{
  "schemaVersion": 1,
  "npcs": [
    {
      "id": "Aldric",
      "displayName": "Aldric",
      "dynamicRelationshipsEnabled": true,
      "disabledWith": [
        "Rosalyn",
        "custom:Example Name"
      ]
    }
  ]
}
```

Set `dynamicRelationshipsEnabled` to `false` to disable every dynamic pairing
for that NPC. Add another NPC's exact generated `id` to `disabledWith` to
disable only that pairing; listing the pair on either NPC is sufficient.

When a run is first enabled, its default policy is snapshotted into that run's
save-scoped relationship record. Disabled pairs cannot be adopted by
conversation or rumor events, changed through the public scoring API, or
explicitly enrolled in Runtime NPC Editor. Later edits to the defaults JSON
apply to newly opened games and do not silently alter existing saves.

## Configuration and persistence

Configuration is generated through BepInEx at:

`BepInEx/config/renegadex.silverpine.dynamicnpcrelationships.cfg`

It exposes the global master enabled flag; minimum and maximum scores; positive and negative
conversation changes; rumor gain; daily decrease; Married negative-loss cap;
Enemy, Stranger, and Acquaintance thresholds; the loaded-Friend starting
buffer; all promotion and retention scores; the approved-promotion buffer; the
denied-review increment; the directional romantic-rejection count that
triggers a permanent viability review;
Friend/Lover query behavior; Lover and Marriage enablement; and verbose logging.
BepInEx owns parsing, persistence, and updates for these settings. Dynamic
Relationships does not use a separate settings JSON file; Newtonsoft.Json
remains reserved for the save-scoped relationship database below.

Relationship data is scoped to Silverpine's internal run ID and stored as one
file per run under:

`BepInEx/config/DynamicNPCRelationships/saves/`

Each filename begins with the sanitized player name, followed by a sanitized
portion of the run ID and a stable hash, so files are recognizable at a glance.
The exact run ID and that run's player name are also stored inside the JSON.
Existing files without `playerName` are updated when their corresponding run is
next loaded. New runs start disabled and do not create a per-run JSON merely by
being started or loaded. The file is first created after the save-wide system is
explicitly enabled in Runtime NPC Editor and the player next saves the game.
The random placeholder run ID on the menu-side `SaveUI` is ignored while the
main menu is open or before its singleton is ready, and the active relationship
scope is cleared on return to the menu. Merely opening or browsing the main
menu therefore cannot create an empty relationship run file. Relationship
changes remain live in memory and mark the active run dirty, but the JSON is
created or replaced only after Silverpine successfully saves the game. Loading,
conversing, toggling settings, or editing a relationship without then saving the
game cannot alter the on-disk run JSON. Loading a game reloads the database, so
unsaved relationship changes are discarded with the unsaved game state. Writes
use a temporary file and atomic replacement. Each preceding valid run file is
retained beside it with a `.bak` suffix and loaded automatically if its primary
file cannot be parsed.

If the previous combined `relationships.json` or its backup exists, its runs
are loaded into memory on boot. Existing per-run files take precedence. Each
legacy-only run is written to its recognizable per-run filename only when that
run is loaded and the player saves the game. After every legacy entry is safely
present in a run file, both legacy inputs are renamed to migrated backup files
during a game save rather than deleted.

This matches Runtime NPC Editor's save binding. Each new game/run begins
disabled and receives its own relationship scope only upon first enablement.
Manual saves, quicksaves, and autosaves made within the same run share that
run's relationship state because Silverpine serializes the same internal
`SaveUI.runID` into each of them. Loading another run immediately swaps the
active scope. Promotion results started under a different active run are
discarded rather than being applied after a save switch.

Custom NPC identities are stored as `custom:<final NPC name>`, never as their
movable `Custom1`, `Custom2`, and similar slots. When custom NPC slots are
reassigned, the relationship follows the named NPC and resolves its current
slot when native relationship fields are applied.

Turning `General.Enabled` off restores each managed pair's original native
Stranger or Acquaintance state, removes only the labels owned by this plugin,
suppresses scoring and public relationship lore, and freezes the inactivity
clock. Re-enabling reapplies the saved dynamic state without retroactive decay.
This CFG value is a global master switch. Each created run JSON also stores an
`enabled` value controlled by Runtime NPC Editor. Turning the save-wide toggle
off retains the JSON and all progress; a never-enabled-and-saved run has no JSON
at all. Toggle and relationship changes become durable on the next normal,
quick, or automatic game save.

Set `RequirePromotionQueries` to `false` to make Friend and Lover threshold
promotions automatic; in that mode a rumor may promote immediately because no
conversation context is consumed. Conversation sentiment classification still
uses two native-style yes/no model queries per eligible NPC pair because it
determines whether the score rises, falls, or remains neutral. Marriage always
requires its generated proposal,
recipient response, and response-classification query even when automatic
Friend/Lover promotions are enabled.

## Runtime NPC Editor integration

The plugin exposes a small public bridge used by Runtime NPC Editor. When this
plugin is detected, the editor adds a dedicated **Dynamic** tab with a
save-wide on/off control. New runs show the system as off and create no
relationship JSON until this control is enabled. The remainder of the tab displays the
managed stage, score, next review threshold, direct interaction count,
configured Friend/Lover/Married promotion and retention thresholds, and suspension
state, plus the Enemy, Stranger, and Acquaintance stage thresholds. It also displays the
romantic-rejection review threshold and any permanent promotion ceiling and
reason. The page can directly edit the save-scoped score, current dynamic tier,
and full tier lock. Scores are clamped to the configured minimum and maximum.
A tier-locked pair records direct conversations and rumors but freezes both its
score and selected tier. Daily decay and external score deltas are also ignored.
Conversation-sentiment, promotion, proposal, and romantic-viability queries are
not started for it; a result already in flight when the lock is enabled is
discarded. Manual score and tier edits remain available on the editor page.
This tier lock is separate from the model-created romantic promotion ceiling,
which continues to allow demotion and recovery up to its ceiling. Tier edits
immediately reapply Enemy/Lover/Married title
overrides or restore the original directional title. Selecting Married creates
the public marriage entry, while changing a Married pair to another tier
creates the public divorce entry. In-flight conversation and promotion results
started before an editor change are discarded instead of overwriting it.
Suspended pairs remain read-only until dynamic control is resumed.

The tab is absent when this plugin is not installed. Saving an explicit
NPC relationship suspends dynamic control for that pair. Removing that editor
override resumes and reapplies the saved dynamic relationship. The Dynamic tab
can also resume or reset the selected pair.

## NPC-to-player proposal integration API

Dynamic Relationships does not track or create player marriages, but exposes
optional public hooks that another BepInEx plugin can use to implement them:

- `CanPresentNpcToPlayerProposal(NeuralNPC)` verifies that the NPC is in the
  active single- or multi-dialog and that replacing the current turn is safe.
- `GenerateNpcToPlayerProposalAsync(NeuralNPC, string)` asks that NPC's active
  model to write an in-character proposal using the current conversation and
  shared history. The optional string supplies additional direction.
- `TryPresentNpcToPlayerProposal(NeuralNPC, string, Func<string, Task>)`
  displays the proposal as the NPC's native dialog turn. The optional async
  handler receives the player's exact next chat input and is awaited before
  Silverpine resumes its normal single- or multi-dialog continuation.
- `ClassifyPlayerProposalResponseAsync(NeuralNPC, string, string)` gives the
  model the exact proposal and exact player reply and returns `true` only for
  clear, willing acceptance.
- `TryPresentAndClassifyNpcToPlayerProposal(NeuralNPC, string,
  Func<bool, Task>)` is the opt-in combined path. It presents the proposal,
  performs the strict response query, and returns `true` (yes) or `false` (no)
  to the requesting plugin before normal dialog resumes.
- `NpcToPlayerProposalPresented` and
  `NpcToPlayerProposalResponseSubmitted` are observational events for plugins
  that need to monitor use of the presentation API.
- `NpcToPlayerProposalResponseClassified` fires after a requested
  classification with the proposer, exact proposal, exact player response, and
  final acceptance boolean.

```csharp
string proposal = await DynamicNPCRelationships.Plugin
    .GenerateNpcToPlayerProposalAsync(npc);

DynamicNPCRelationships.Plugin.TryPresentAndClassifyNpcToPlayerProposal(
    npc,
    proposal,
    accepted =>
    {
        // The integrating plugin owns player-marriage state and consequences.
        ApplyPlayerProposalResult(npc, accepted);
        return Task.CompletedTask;
    });
```

Generating, presenting, and classifying through this API never changes an
NPC-to-NPC dynamic score, the player's relationship level, titles, memories,
or world knowledge on its own.

## Build

Release builds are optimized and emit neither debug symbols nor a PDB. From the
larger Silverpine development workspace, the sibling Modding Tools project and
game installation are detected automatically:

```powershell
dotnet build "Plugin Development/DynamicNPCRelationships/DynamicNPCRelationships.csproj" --configuration Release
```

From a standalone clone, provide the Silverpine installation directory. The
project will use the installed Modding Tools DLL by default:

```powershell
dotnet build DynamicNPCRelationships.csproj --configuration Release `
  -p:SilverpineGameDir="C:\Path\To\Silverpine"
```

Alternatively, set `ModdingToolsProject` or `ModdingToolsAssembly` explicitly.
The build fails with a clear error when required game references are missing.

Copy `bin/Release/netstandard2.1/DynamicNPCRelationships.dll` into a folder
under `BepInEx/plugins`.

## Credits

Created by **Saelac and ChatGPT**.
