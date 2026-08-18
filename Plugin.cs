#nullable enable

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Silverpine.ModdingTools;
using UnityEngine;

namespace DynamicNPCRelationships;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(
    Silverpine.ModdingTools.Plugin.PluginGuid,
    "1.9.1")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid =
        "renegadex.silverpine.dynamicnpcrelationships";
    public const string PluginName = "Dynamic NPC Relationships";
    public const string PluginVersion = "1.17.1";

    internal static Plugin Instance = null!;
    internal static ManualLogSource Log = null!;

    public static event Action<NeuralNPC, string>?
        NpcToPlayerProposalPresented;
    public static event Action<NeuralNPC, string, string>?
        NpcToPlayerProposalResponseSubmitted;
    public static event Action<NeuralNPC, string, string, bool>?
        NpcToPlayerProposalResponseClassified;

    private static readonly FieldInfo RunIdField =
        AccessTools.Field(typeof(SaveUI), "runID");
    private static readonly MethodInfo FinalSexMethod =
        AccessTools.Method(typeof(NeuralNPC), "GetFinalSex");
    private static readonly MethodInfo GenerateMultiDialogMethod =
        AccessTools.Method(typeof(NeuralNPC), "GenerateMultiDialog");
    private static readonly MethodInfo SingleDialogInputMethod =
        AccessTools.Method(typeof(NeuralNPC), "OnInputCallback");
    private static readonly ConditionalWeakTable<
        RoutineArgument_PassOnRumor, RumorDeliveryMarker>
        DeliveredRumors = new();

    private readonly HashSet<string> promotionQueries = new();
    private readonly HashSet<string> conversationReadyPromotions = new();
    private readonly Queue<ConversationAssessmentRequest>
        conversationAssessments = new();
    private readonly HashSet<string> dirtyRunIds =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> legacyRunIds =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<NeuralNPC>>
        attemptedMarriageProposalSessions = new(StringComparer.Ordinal);
    private RelationshipDatabase database = new();
    private DefaultRelationshipSettings defaultSettings = new();
    private SaveRelationshipData? currentSave;
    private string currentRunId = "";
    private Harmony harmony = null!;
    private float nextManagedUpdate;
    private bool promotionQueryInProgress;
    private bool conversationAssessmentInProgress;
    private bool generatingWorldKnowledgeWordCloud;
    private bool bypassPendingMarriageProposal;
    private MarriageProposalDialogContext? activeMarriageProposalDialog;
    private string initializingRosterRunId = "";
    private int initializingRosterCount = -1;
    private int stableInitializingRosterChecks;
    private bool npcReconciliationPending = true;
    private bool legacyArchiveBlocked;
    private ConfigEntry<bool> pluginEnabledSetting = null!;
    private ConfigEntry<int> sharedConversationGainSetting = null!;
    private ConfigEntry<int> sharedConversationLossSetting = null!;
    private ConfigEntry<int> rumorGainSetting = null!;
    private ConfigEntry<int> dailyScoreDecreaseSetting = null!;
    private ConfigEntry<int> minimumScoreSetting = null!;
    private ConfigEntry<int> maximumScoreSetting = null!;
    private ConfigEntry<int> marriedNegativeLossCapSetting = null!;
    private ConfigEntry<int> enemyThresholdSetting = null!;
    private ConfigEntry<int> strangerThresholdSetting = null!;
    private ConfigEntry<int> acquaintanceThresholdSetting = null!;
    private ConfigEntry<int> loadedFriendStartingScoreBonusSetting = null!;
    private ConfigEntry<int> friendReviewScoreSetting = null!;
    private ConfigEntry<int> loverReviewScoreSetting = null!;
    private ConfigEntry<int> marriageReviewScoreSetting = null!;
    private ConfigEntry<int> deniedReviewAdditionalScoreSetting = null!;
    private ConfigEntry<int> approvedPromotionScoreBonusSetting = null!;
    private ConfigEntry<int> romanticRejectionsBeforeLockReviewSetting = null!;
    private ConfigEntry<int> friendRetentionScoreSetting = null!;
    private ConfigEntry<int> loverRetentionScoreSetting = null!;
    private ConfigEntry<int> marriageRetentionScoreSetting = null!;
    private ConfigEntry<bool> requirePromotionQueriesSetting = null!;
    private ConfigEntry<bool> allowLoversSetting = null!;
    private ConfigEntry<bool> allowMarriageSetting = null!;
    private ConfigEntry<bool> verboseLoggingSetting = null!;

    private bool globallyEnabled => pluginEnabledSetting.Value;
    private bool pluginEnabled =>
        globallyEnabled && currentSave?.enabled == true;
    private int sharedConversationGain =>
        Math.Max(0, sharedConversationGainSetting.Value);
    private int sharedConversationLoss =>
        Math.Max(0, sharedConversationLossSetting.Value);
    private int rumorGain => Math.Max(0, rumorGainSetting.Value);
    private int dailyScoreDecrease =>
        Math.Max(0, dailyScoreDecreaseSetting.Value);
    private int MinimumScore => Math.Min(
        minimumScoreSetting.Value, MaximumScore);
    private int MaximumScore => Math.Min(
        int.MaxValue - 1, maximumScoreSetting.Value);
    private int MarriedNegativeLossCap =>
        Math.Max(0, marriedNegativeLossCapSetting.Value);
    private int enemyThreshold => Math.Min(
        int.MaxValue - 1, enemyThresholdSetting.Value);
    private int enemyRecoveryScore => enemyThreshold + 1;
    private int strangerThreshold => Math.Min(
        int.MaxValue - 1, strangerThresholdSetting.Value);
    private int acquaintanceThreshold => acquaintanceThresholdSetting.Value;
    private int loadedFriendStartingScoreBonus =>
        Math.Max(0, loadedFriendStartingScoreBonusSetting.Value);
    private int friendReviewScore => friendReviewScoreSetting.Value;
    private int loverReviewScore => loverReviewScoreSetting.Value;
    private int marriageReviewScore => marriageReviewScoreSetting.Value;
    private int deniedReviewAdditionalScore =>
        deniedReviewAdditionalScoreSetting.Value;
    private int approvedPromotionScoreBonus =>
        Math.Max(0, approvedPromotionScoreBonusSetting.Value);
    private int romanticRejectionsBeforeLockReview => Math.Max(
        1, romanticRejectionsBeforeLockReviewSetting.Value);
    private int friendRetentionScore => friendRetentionScoreSetting.Value;
    private int loverRetentionScore => loverRetentionScoreSetting.Value;
    private int marriageRetentionScore => marriageRetentionScoreSetting.Value;
    private bool requirePromotionQueries =>
        requirePromotionQueriesSetting.Value;
    private bool allowLovers => allowLoversSetting.Value;
    private bool allowMarriage => allowMarriageSetting.Value;
    private bool verboseLogging => verboseLoggingSetting.Value;

    private static string LegacyDatabasePath => Path.Combine(
        Paths.ConfigPath,
        "DynamicNPCRelationships",
        "relationships.json");

    private static string SavesDirectory => Path.Combine(
        Paths.ConfigPath,
        "DynamicNPCRelationships",
        "saves");

    private static string DefaultSettingsPath => Path.Combine(
        Paths.ConfigPath,
        "DynamicNPCRelationships",
        "defaultRelationships.json");

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        BindConfiguration();
        LoadDefaultSettings();
        LoadDatabase();

        harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin).Assembly);
        SerializationManager.OnFinishedLoadingSave -= OnFinishedLoadingSave;
        SerializationManager.OnFinishedLoadingSave += OnFinishedLoadingSave;

        Logger.LogInfo(
            "Dynamic NPC Relationships initialized. Shared conversations "
            + "are independently checked for positive and negative effects, "
            + "with neither treated as neutral; model promotion reviews "
            + "remain limited to relationship-stage boundaries.");
    }

    private void BindConfiguration()
    {
        pluginEnabledSetting = Config.Bind(
            "General", "Enabled", true,
            "Master switch for dynamic NPC-to-NPC relationships. Each run "
            + "also has its own save-wide toggle and new runs begin disabled.");
        minimumScoreSetting = Config.Bind(
            "Scoring", "MinimumScore", -100,
            "Lowest score stored for a dynamic relationship.");
        maximumScoreSetting = Config.Bind(
            "Scoring", "MaximumScore", 150,
            "Highest score stored for a dynamic relationship.");
        sharedConversationGainSetting = Config.Bind(
            "Scoring", "SharedConversationGain", 3,
            "Score earned when the model classifies a completed shared "
            + "conversation as positive.");
        sharedConversationLossSetting = Config.Bind(
            "Scoring", "SharedConversationLoss", 3,
            "Score lost when the model classifies a completed shared "
            + "conversation as negative.");
        rumorGainSetting = Config.Bind(
            "Scoring", "RumorExchangeGain", 2,
            "Score earned when one eligible NPC successfully delivers a rumor "
            + "to another.");
        dailyScoreDecreaseSetting = Config.Bind(
            "Scoring", "DailyScoreDecrease", 1,
            "Score lost after an in-game day without a shared conversation. "
            + "Does not apply to Lover or Married pairs and cannot lower a "
            + "pair into another relationship stage.");
        marriedNegativeLossCapSetting = Config.Bind(
            "Scoring", "MarriedNegativeLossCap", 1,
            "Maximum points a Married pair can lose from one negative score "
            + "change. Set to zero to prevent individual negative losses.");
        enemyThresholdSetting = Config.Bind(
            "Progression", "EnemyThreshold", -50,
            "A managed Stranger pair becomes Enemies at or below this "
            + "score. Enemies return to Stranger when their score rises "
            + "above it.");
        strangerThresholdSetting = Config.Bind(
            "Progression", "StrangerThreshold", -20,
            "An Acquaintance pair falls to Stranger at or below this score.");
        acquaintanceThresholdSetting = Config.Bind(
            "Progression", "AcquaintanceThreshold", 0,
            "A managed Stranger pair rises to Acquaintance at or above this "
            + "score. This transition never uses a promotion query.");
        loadedFriendStartingScoreBonusSetting = Config.Bind(
            "Progression", "LoadedFriendStartingScoreBonus", 5,
            "Points added above the greater of FriendReviewScore and "
            + "FriendRetentionScore when a new run adopts a plain native "
            + "Friend relationship.");
        friendReviewScoreSetting = Config.Bind(
            "Promotions", "FriendReviewScore", 30,
            "Score at which an acquaintance pair is reviewed for friendship.");
        loverReviewScoreSetting = Config.Bind(
            "Promotions", "LoverReviewScore", 80,
            "Score at which a friend pair is reviewed for romance.");
        marriageReviewScoreSetting = Config.Bind(
            "Promotions", "MarriageReviewScore", 120,
            "Score at which a lover generates a marriage proposal.");
        deniedReviewAdditionalScoreSetting = Config.Bind(
            "Promotions", "DeniedReviewAdditionalScore", 15,
            "Additional score required before retrying a denied promotion or "
            + "rejected proposal.");
        approvedPromotionScoreBonusSetting = Config.Bind(
            "Promotions", "ApprovedPromotionScoreBonus", 5,
            "After a successful Friend, Lover, or Marriage promotion query, "
            + "raise the score to at least its promotion threshold plus this "
            + "buffer. Set to zero to disable the buffer.");
        romanticRejectionsBeforeLockReviewSetting = Config.Bind(
            "Promotions", "RomanticRejectionsBeforeLockReview", 2,
            "Directional Lover or Marriage rejections required before asking "
            + "whether the rejecting NPC could ever accept that relationship. "
            + "A no answer permanently sets the pair's current stage as its "
            + "promotion ceiling. The pair may demote and recover to that "
            + "ceiling without further promotion queries.");
        friendRetentionScoreSetting = Config.Bind(
            "Promotions", "FriendRetentionScore", 15,
            "A Friend falls back to Acquaintance below this score.");
        loverRetentionScoreSetting = Config.Bind(
            "Promotions", "LoverRetentionScore", 60,
            "A Lover falls back to Friend below this score.");
        marriageRetentionScoreSetting = Config.Bind(
            "Promotions", "MarriageRetentionScore", 90,
            "A Married pair divorces below this score, returning to "
            + "Acquaintance at the configured acquaintance baseline.");
        requirePromotionQueriesSetting = Config.Bind(
            "Promotions", "RequirePromotionQueries", true,
            "Ask the active Silverpine model to approve Friend and Lover "
            + "promotions. Marriage always uses a generated proposal and the "
            + "recipient's generated response.");
        allowLoversSetting = Config.Bind(
            "Promotions", "AllowLovers", true,
            "Allow established friends to become lovers.");
        allowMarriageSetting = Config.Bind(
            "Promotions", "AllowMarriage", true,
            "Allow established lovers to become married. Marriage is "
            + "pair-based and does not prevent multiple spouses.");
        verboseLoggingSetting = Config.Bind(
            "Debug", "VerboseLogging", false,
            "Log every relationship score change.");
        pluginEnabledSetting.SettingChanged -= OnPluginEnabledSettingChanged;
        pluginEnabledSetting.SettingChanged += OnPluginEnabledSettingChanged;
    }

    private void OnPluginEnabledSettingChanged(object sender, EventArgs args)
    {
        RefreshSaveIdentity();
        if (pluginEnabled)
            RefreshAndApplyRelationships();
        else
        {
            RestoreOriginalRelationshipsForDisable();
            FreezeDailyDecreaseClock();
        }
    }

    private static void OnFinishedLoadingSave()
    {
        if (ReferenceEquals(Instance, null))
            return;

        Instance.ReloadDatabaseAfterGameLoad();

        if (ActionQueue.Instance != null)
        {
            ActionQueue.Instance.DoAfterXFrames(
                8, Instance.RefreshAndApplyRelationships);
        }
        else
        {
            Instance.RefreshAndApplyRelationships();
        }
    }

    private void ReloadDatabaseAfterGameLoad()
    {
        LoadDatabase();
        dirtyRunIds.Clear();
        currentSave = null;
        currentRunId = "";
        promotionQueries.Clear();
        conversationReadyPromotions.Clear();
        conversationAssessments.Clear();
        attemptedMarriageProposalSessions.Clear();
        activeMarriageProposalDialog = null;
        bypassPendingMarriageProposal = false;
        initializingRosterRunId = "";
        initializingRosterCount = -1;
        stableInitializingRosterChecks = 0;
        npcReconciliationPending = true;
        promotionQueryInProgress = false;
    }

    internal void RunManagedUpdate()
    {
        if (activeMarriageProposalDialog != null
            && !IsProposalDialogActive(activeMarriageProposalDialog))
        {
            activeMarriageProposalDialog = null;
        }
        if (Time.unscaledTime < nextManagedUpdate)
            return;

        nextManagedUpdate = Time.unscaledTime + 1f;
        RefreshSaveIdentity();
        if (!pluginEnabled)
        {
            RestoreOriginalRelationshipsForDisable();
            FreezeDailyDecreaseClock();
            return;
        }
        if (currentSave != null
            && !currentSave.startingRelationshipsInitialized)
        {
            RefreshAndApplyRelationships();
        }
        if (npcReconciliationPending)
            ReconcileNewNpcRelationshipsIfNeeded();
        ApplyDailyScoreDecrease();
        _ = GenerateNextWorldKnowledgeWordCloudAsync();
    }

    internal void RefreshAndApplyRelationships()
    {
        RefreshSaveIdentity();
        if (currentSave == null || NeuralNPC.neuralNPCs == null)
            return;
        if (!pluginEnabled)
        {
            RestoreOriginalRelationshipsForDisable();
            FreezeDailyDecreaseClock();
            return;
        }

        InitializeStartingRelationshipsIfNeeded();
        bool saveChanged = false;
        foreach (PairRelationshipData pair in currentSave.pairs)
        {
            if (ApplyPair(pair, tolerateUnappliedBaseState: true)
                && TryResolvePair(
                    pair, out NeuralNPC managedFirst,
                    out NeuralNPC managedSecond)
                && ApplyEnemyBoundaryTransition(
                    pair, managedFirst, managedSecond))
            {
                saveChanged = true;
            }
            if (pair.stage == DynamicRelationshipStage.Married
                && pair.worldKnowledgeState
                    != WorldRelationshipKnowledgeState.Married
                && TryResolvePair(
                    pair, out NeuralNPC first, out NeuralNPC second))
            {
                SetWorldKnowledge(
                    pair, first, second,
                    WorldRelationshipKnowledgeState.Married);
                saveChanged = true;
            }
        }
        if (saveChanged)
            SaveDatabase();
    }

    private void InitializeStartingRelationshipsIfNeeded()
    {
        if (currentSave == null
            || currentSave.startingRelationshipsInitialized
            || NeuralNPC.neuralNPCs == null)
        {
            return;
        }

        int rosterCount = NeuralNPC.neuralNPCs.Values
            .Where(npc => npc != null)
            .Select(GetNpcStorageKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (!string.Equals(
                initializingRosterRunId,
                currentRunId,
                StringComparison.Ordinal)
            || rosterCount != initializingRosterCount)
        {
            initializingRosterRunId = currentRunId;
            initializingRosterCount = rosterCount;
            stableInitializingRosterChecks = 1;
            return;
        }
        stableInitializingRosterChecks++;
        if (stableInitializingRosterChecks < 3)
            return;

        NeuralNPC[] npcs = NeuralNPC.neuralNPCs.Values
            .Where(npc => npc != null)
            .GroupBy(GetNpcStorageKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(GetNpcStorageKey, StringComparer.Ordinal)
            .ToArray();
        int added = 0;
        for (int firstIndex = 0; firstIndex < npcs.Length - 1; firstIndex++)
        {
            for (int secondIndex = firstIndex + 1;
                 secondIndex < npcs.Length;
                 secondIndex++)
            {
                NeuralNPC first = npcs[firstIndex];
                NeuralNPC second = npcs[secondIndex];
                if (!IsPairEnabledForSave(currentSave, first, second)
                    || FindPair(first, second) != null
                    || !TryCreateLoadedStartingPair(
                        first, second, out PairRelationshipData pair))
                {
                    continue;
                }
                currentSave.pairs.Add(pair);
                ApplyManagedPairUnchecked(first, second, pair);
                added++;
            }
        }
        currentSave.startingRelationshipsInitialized = true;
        currentSave.initializedNpcIds = npcs
            .Select(GetNpcStorageKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        initializingRosterRunId = "";
        initializingRosterCount = -1;
        stableInitializingRosterChecks = 0;
        SaveDatabase();
        Log.LogInfo(
            $"Initialized {added} dynamic NPC relationship starting states "
            + $"for run {currentSave.runId} from its loaded native "
            + "relationships.");
    }

    private void ReconcileNewNpcRelationshipsIfNeeded()
    {
        if (currentSave == null
            || !currentSave.startingRelationshipsInitialized
            || NeuralNPC.neuralNPCs == null)
        {
            return;
        }

        bool changed = false;
        if (currentSave.initializedNpcIds.Count == 0
            && currentSave.pairs.Count > 0)
        {
            currentSave.initializedNpcIds = currentSave.pairs
                .SelectMany(pair => new[] { pair.firstNpc, pair.secondNpc })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            changed = true;
        }

        NeuralNPC[] allCurrentNpcs = NeuralNPC.neuralNPCs.Values
            .Where(npc => npc != null)
            .GroupBy(GetNpcStorageKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(GetNpcStorageKey, StringComparer.Ordinal)
            .ToArray();
        HashSet<string> initializedIds = new(
            currentSave.initializedNpcIds,
            StringComparer.Ordinal);
        HashSet<string> newlyAvailableIds = allCurrentNpcs
            .Select(GetNpcStorageKey)
            .Where(id => !initializedIds.Contains(id))
            .ToHashSet(StringComparer.Ordinal);
        if (newlyAvailableIds.Count == 0)
        {
            if (changed)
                SaveDatabase();
            npcReconciliationPending = false;
            return;
        }

        NeuralNPC[] currentNpcs = allCurrentNpcs;

        int addedPairs = 0;
        for (int firstIndex = 0;
             firstIndex < currentNpcs.Length - 1;
             firstIndex++)
        {
            for (int secondIndex = firstIndex + 1;
                 secondIndex < currentNpcs.Length;
                 secondIndex++)
            {
                NeuralNPC first = currentNpcs[firstIndex];
                NeuralNPC second = currentNpcs[secondIndex];
                if (!newlyAvailableIds.Contains(GetNpcStorageKey(first))
                    && !newlyAvailableIds.Contains(GetNpcStorageKey(second)))
                {
                    continue;
                }
                if (!IsPairEnabledForSave(currentSave, first, second)
                    || FindPair(first, second) != null
                    || !TryCreateLoadedStartingPair(
                        first, second, out PairRelationshipData pair))
                {
                    continue;
                }

                currentSave.pairs.Add(pair);
                ApplyManagedPairUnchecked(first, second, pair);
                addedPairs++;
            }
        }

        initializedIds.UnionWith(newlyAvailableIds);
        currentSave.initializedNpcIds = initializedIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        SaveDatabase();
        Log.LogInfo(
            $"Processed {newlyAvailableIds.Count} newly available NPC "
            + $"identities for run {currentSave.runId} and initialized "
            + $"{addedPairs} additional dynamic relationship pairs.");
        npcReconciliationPending = false;
    }

    internal void NotifyNpcRosterChanged()
    {
        npcReconciliationPending = true;
    }

    private bool TryCreateLoadedStartingPair(
        NeuralNPC first,
        NeuralNPC second,
        out PairRelationshipData pair)
    {
        pair = null!;
        bool firstHasRelationship = TryGetNativeRelationship(
            first, second, out NPCRelationship firstTier);
        bool secondHasRelationship = TryGetNativeRelationship(
            second, first, out NPCRelationship secondTier);
        string firstCustomName = GetCustomName(first, second);
        string secondCustomName = GetCustomName(second, first);

        bool relationshipMissing = !firstHasRelationship
            && !secondHasRelationship;
        NPCRelationship nativeTier;
        if (relationshipMissing)
        {
            nativeTier = NPCRelationship.Stranger;
        }
        else
        {
            if (!firstHasRelationship
                || !secondHasRelationship
                || firstTier != secondTier
                || (firstTier != NPCRelationship.Stranger
                    && firstTier != NPCRelationship.Acquaintance
                    && firstTier != NPCRelationship.Friend))
            {
                return false;
            }
            nativeTier = firstTier;
        }

        DynamicRelationshipStage stage = nativeTier switch
        {
            NPCRelationship.Stranger => DynamicRelationshipStage.Stranger,
            NPCRelationship.Friend => DynamicRelationshipStage.Friend,
            _ => DynamicRelationshipStage.Acquaintance
        };
        long requestedScore = stage switch
        {
            DynamicRelationshipStage.Stranger => strangerThreshold,
            DynamicRelationshipStage.Friend =>
                (long)Math.Max(friendReviewScore, friendRetentionScore)
                + loadedFriendStartingScoreBonus,
            _ => acquaintanceThreshold
        };
        int score = (int)Math.Max(
            MinimumScore, Math.Min(MaximumScore, requestedScore));
        (string firstKey, string secondKey) = CreateOrderedKeys(first, second);
        bool firstIsOrderedFirst = string.Equals(
            GetNpcStorageKey(first), firstKey, StringComparison.Ordinal);
        pair = new PairRelationshipData
        {
            firstNpc = firstKey,
            secondNpc = secondKey,
            originalNativeTier = (int)nativeTier,
            originalRelationshipMissing = relationshipMissing,
            originalFirstCustomName = firstIsOrderedFirst
                ? firstCustomName
                : secondCustomName,
            originalSecondCustomName = firstIsOrderedFirst
                ? secondCustomName
                : firstCustomName,
            stage = stage,
            score = score,
            nextReviewScore = stage switch
            {
                DynamicRelationshipStage.Stranger => acquaintanceThreshold,
                DynamicRelationshipStage.Friend => loverReviewScore,
                _ => friendReviewScore
            },
            lastReason = relationshipMissing
                ? "initialized from no loaded native relationship"
                : "initialized from loaded native " + nativeTier
                    + " relationship"
        };
        return true;
    }

    internal void AppendPublicWorldRelationshipKnowledge(
        NPCName npcName,
        string deepHaystack,
        string shallowHaystack,
        ref string lore)
    {
        if (!pluginEnabled)
            return;
        RefreshSaveIdentity();
        if (currentSave == null
            || NeuralNPC.neuralNPCs == null
            || !NeuralNPC.neuralNPCs.TryGetValue(
                npcName, out NeuralNPC receivingNpc)
            || receivingNpc == null
            || receivingNpc.faction != Faction.Silverpine)
            return;

        List<PairRelationshipData> available = currentSave.pairs
            .Where(pair => pair.worldKnowledgeState
                    != WorldRelationshipKnowledgeState.None
                && !pair.worldKnowledgeWordCloudDirty
                && HasWorldKnowledgeWordCloud(pair))
            .OrderBy(_ => Guid.NewGuid())
            .OrderByDescending(pair => GetWorldKnowledgeRelevance(
                pair, deepHaystack, shallowHaystack))
            .Take(2)
            .ToList();
        foreach (PairRelationshipData pair in available)
        {
            string entry = GetWorldKnowledgeEntry(pair);
            if (entry == "" || lore.Contains(
                    GetWorldKnowledgeEntryName(pair),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            lore += (string.IsNullOrWhiteSpace(lore) ? "" : "\n") + entry;
        }
    }

    private async Task GenerateNextWorldKnowledgeWordCloudAsync()
    {
        if (generatingWorldKnowledgeWordCloud || currentSave == null)
            return;
        PairRelationshipData? pair = currentSave.pairs.FirstOrDefault(
            candidate => candidate.worldKnowledgeState
                    != WorldRelationshipKnowledgeState.None
                && candidate.worldKnowledgeWordCloudDirty);
        if (pair == null || InferenceServerSetupHandler.Instance == null)
            return;

        string entry = GetWorldKnowledgeEntry(pair);
        if (entry == "")
            return;
        string generationRunId = currentRunId;
        WorldRelationshipKnowledgeState generationState =
            pair.worldKnowledgeState;
        generatingWorldKnowledgeWordCloud = true;
        try
        {
            string firstName = pair.firstDisplayName;
            string secondName = pair.secondDisplayName;
            CustomContentDefinition_NPC.WordCloud cloud =
                await CustomContentDefinition_NPC.WordCloud.Create(
                    entry,
                    word => !string.Equals(
                            word, firstName,
                            StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(
                            word, secondName,
                            StringComparison.OrdinalIgnoreCase));
            RefreshSaveIdentity();
            if (!pluginEnabled
                || !string.Equals(
                    currentRunId, generationRunId, StringComparison.Ordinal)
                || currentSave == null
                || !currentSave.pairs.Contains(pair)
                || pair.worldKnowledgeState != generationState
                || !pair.worldKnowledgeWordCloudDirty
                || !string.Equals(
                    GetWorldKnowledgeEntry(pair), entry,
                    StringComparison.Ordinal))
            {
                Log.LogInfo(
                    $"Discarded stale public knowledge word cloud for "
                    + $"{firstName} / {secondName}.");
                return;
            }
            pair.worldKnowledgeNouns = cloud.nouns.ToList();
            pair.worldKnowledgeVerbs = cloud.verbs.ToList();
            pair.worldKnowledgeAdjectives = cloud.adjectives.ToList();
            pair.worldKnowledgeKeywords = cloud.keywords.ToList();
            pair.worldKnowledgeWordCloudDirty = false;
            SaveDatabase();
            Log.LogInfo(
                $"Generated and cached public {pair.worldKnowledgeState} "
                + $"knowledge for {firstName} and {secondName}.");
        }
        catch (Exception exception)
        {
            Log.LogWarning(
                $"World knowledge word-cloud generation for "
                + $"{pair.firstDisplayName} / {pair.secondDisplayName} "
                + $"will retry: {exception.Message}");
        }
        finally
        {
            generatingWorldKnowledgeWordCloud = false;
        }
    }

    private static bool HasWorldKnowledgeWordCloud(
        PairRelationshipData pair) =>
        pair.worldKnowledgeNouns.Count > 0
        || pair.worldKnowledgeVerbs.Count > 0
        || pair.worldKnowledgeAdjectives.Count > 0
        || pair.worldKnowledgeKeywords.Count > 0;

    private static int GetWorldKnowledgeRelevance(
        PairRelationshipData pair,
        string deepHaystack,
        string shallowHaystack)
    {
        int score = 0;
        IEnumerable<string> words = pair.worldKnowledgeNouns
            .Concat(pair.worldKnowledgeVerbs)
            .Concat(pair.worldKnowledgeAdjectives)
            .Concat(pair.worldKnowledgeKeywords)
            .Where(word => !string.IsNullOrWhiteSpace(word));
        foreach (string word in words)
        {
            if (deepHaystack.ContainsAnyWordIgnoreCaseAdvanced(word))
            {
                score++;
                if (shallowHaystack.ContainsAnyWordIgnoreCaseAdvanced(word))
                    score += 6;
            }
        }
        return score;
    }

    private void LoadDefaultSettings()
    {
        try
        {
            if (File.Exists(DefaultSettingsPath))
            {
                defaultSettings =
                    JsonConvert.DeserializeObject<DefaultRelationshipSettings>(
                        File.ReadAllText(DefaultSettingsPath))
                    ?? new DefaultRelationshipSettings();
                defaultSettings.Normalize();
            }
            else
            {
                defaultSettings = new DefaultRelationshipSettings();
                SaveDefaultSettings();
            }
        }
        catch (Exception exception)
        {
            Logger.LogError(
                "Could not load default relationship settings: " + exception);
            string backupPath = DefaultSettingsPath + ".bak";
            try
            {
                if (!File.Exists(backupPath))
                {
                    defaultSettings = new DefaultRelationshipSettings();
                    return;
                }
                defaultSettings =
                    JsonConvert.DeserializeObject<DefaultRelationshipSettings>(
                        File.ReadAllText(backupPath))
                    ?? new DefaultRelationshipSettings();
                defaultSettings.Normalize();
                Logger.LogWarning(
                    "Recovered default relationship settings from the backup "
                    + "file.");
            }
            catch (Exception backupException)
            {
                defaultSettings = new DefaultRelationshipSettings();
                Logger.LogError(
                    "Could not recover the default relationship settings "
                    + "backup; using all-enabled defaults for this session: "
                    + backupException);
            }
        }
    }

    private void RefreshDefaultNpcSettings()
    {
        if (NeuralNPC.neuralNPCs == null)
            return;

        bool changed = false;
        foreach (NeuralNPC npc in NeuralNPC.neuralNPCs.Values
                     .Where(candidate => candidate != null)
                     .Distinct())
        {
            string id = GetNpcStorageKey(npc);
            if (string.IsNullOrWhiteSpace(id))
                continue;
            DefaultNpcRelationshipSettings? entry =
                defaultSettings.npcs.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.id, id, StringComparison.Ordinal));
            string displayName = npc.GetFinalName();
            if (entry == null)
            {
                defaultSettings.npcs.Add(
                    new DefaultNpcRelationshipSettings
                    {
                        id = id,
                        displayName = displayName
                    });
                changed = true;
            }
            else if (!string.Equals(
                         entry.displayName, displayName,
                         StringComparison.Ordinal))
            {
                entry.displayName = displayName;
                changed = true;
            }
        }

        if (!changed)
            return;
        defaultSettings.Normalize();
        SaveDefaultSettings();
    }

    private SaveRelationshipData CreateNewSaveScope(string runId)
    {
        SaveRelationshipData save = new()
        {
            runId = runId,
            playerName = GetCurrentPlayerName(),
            enabled = true
        };
        ApplyDefaultSettingsToNewSave(save);
        return save;
    }

    private static string GetCurrentPlayerName()
    {
        if (Player.Instance == null)
            return "";
        return (Player.Instance.playerName ?? "").Trim();
    }

    private bool UpdateActiveSavePlayerName()
    {
        if (currentSave == null)
            return false;

        string playerName = GetCurrentPlayerName();
        if (string.IsNullOrWhiteSpace(playerName)
            || string.Equals(
                currentSave.playerName, playerName,
                StringComparison.Ordinal))
        {
            return false;
        }

        currentSave.playerName = playerName;
        return true;
    }

    private static bool NeedsRecognizableRunPath(
        SaveRelationshipData save)
    {
        if (string.IsNullOrWhiteSpace(save.RuntimeSourcePath))
            return true;
        string expectedPath = GetRunDataPath(save.runId, save.playerName);
        return !string.Equals(
            Path.GetFullPath(save.RuntimeSourcePath),
            Path.GetFullPath(expectedPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyDefaultSettingsToNewSave(SaveRelationshipData save)
    {
        save.defaultPolicyApplied = true;
        save.disabledDynamicPairs.Clear();
        for (int first = 0; first < defaultSettings.npcs.Count - 1; first++)
        {
            DefaultNpcRelationshipSettings firstNpc =
                defaultSettings.npcs[first];
            for (int second = first + 1;
                 second < defaultSettings.npcs.Count;
                 second++)
            {
                DefaultNpcRelationshipSettings secondNpc =
                    defaultSettings.npcs[second];
                if (IsDefaultPairEnabled(firstNpc, secondNpc))
                    continue;
                save.disabledDynamicPairs.Add(
                    CreatePairId(firstNpc.id, secondNpc.id));
            }
        }
        save.disabledDynamicPairs = save.disabledDynamicPairs
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsDefaultPairEnabled(
        DefaultNpcRelationshipSettings first,
        DefaultNpcRelationshipSettings second) =>
        first.dynamicRelationshipsEnabled
        && second.dynamicRelationshipsEnabled
        && !first.disabledWith.Contains(
            second.id, StringComparer.OrdinalIgnoreCase)
        && !second.disabledWith.Contains(
            first.id, StringComparer.OrdinalIgnoreCase);

    private bool IsPairEnabledForCurrentSave(
        NeuralNPC first,
        NeuralNPC second)
    {
        if (currentSave == null)
            return true;
        return IsPairEnabledForSave(currentSave, first, second);
    }

    private static bool IsPairEnabledForSave(
        SaveRelationshipData save,
        NeuralNPC first,
        NeuralNPC second)
    {
        string pairId = CreatePairId(
            GetNpcStorageKey(first), GetNpcStorageKey(second));
        return !save.disabledDynamicPairs.Contains(
            pairId, StringComparer.Ordinal);
    }

    private void SaveDefaultSettings()
    {
        string temporaryPath = DefaultSettingsPath + ".tmp";
        try
        {
            string? directory = Path.GetDirectoryName(DefaultSettingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(
                temporaryPath,
                JsonConvert.SerializeObject(
                    defaultSettings, Formatting.Indented));
            if (File.Exists(DefaultSettingsPath))
            {
                File.Replace(
                    temporaryPath,
                    DefaultSettingsPath,
                    DefaultSettingsPath + ".bak",
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, DefaultSettingsPath);
            }
        }
        catch (Exception exception)
        {
            Logger.LogError(
                "Could not save default relationship settings: "
                + exception);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private void RefreshSaveIdentity()
    {
        if (MainMenuUI.Instance == null || MainMenuUI.Instance.IsOpen())
        {
            DeactivateSaveScopeForMainMenu();
            return;
        }

        RefreshDefaultNpcSettings();
        if (SaveUI.Instance == null
            || RunIdField == null
            || NeuralNPC.neuralNPCs == null
            || NeuralNPC.neuralNPCs.Count == 0)
            return;

        string runId = RunIdField.GetValue(SaveUI.Instance) as string ?? "";
        if (string.IsNullOrWhiteSpace(runId))
            return;

        if (string.Equals(runId, currentRunId, StringComparison.Ordinal))
        {
            currentSave = database.saves
                .FirstOrDefault(save => string.Equals(
                    save.runId, runId, StringComparison.Ordinal));
            if (currentSave != null)
            {
                bool metadataChanged = UpdateActiveSavePlayerName();
                if (metadataChanged
                    || NeedsRecognizableRunPath(currentSave))
                {
                    SaveDatabase();
                }
            }
            return;
        }

        currentRunId = runId;
        currentSave = database.saves.FirstOrDefault(
            save => string.Equals(save.runId, runId,
                StringComparison.Ordinal));
        if (currentSave != null)
        {
            bool metadataChanged = UpdateActiveSavePlayerName();
            if (metadataChanged || NeedsRecognizableRunPath(currentSave))
                SaveDatabase();
        }
        promotionQueries.Clear();
        conversationReadyPromotions.Clear();
        attemptedMarriageProposalSessions.Clear();
        npcReconciliationPending = true;
        activeMarriageProposalDialog = null;
        bypassPendingMarriageProposal = false;
        initializingRosterRunId = runId;
        initializingRosterCount = -1;
        stableInitializingRosterChecks = 0;
        promotionQueryInProgress = false;
    }

    private bool SetCurrentSaveEnabled(bool enabled)
    {
        RefreshSaveIdentity();
        if (string.IsNullOrWhiteSpace(currentRunId)
            || NeuralNPC.neuralNPCs == null
            || NeuralNPC.neuralNPCs.Count == 0)
        {
            return false;
        }

        if (enabled)
        {
            if (currentSave?.enabled == true)
            {
                if (UpdateActiveSavePlayerName())
                    SaveDatabase();
                return true;
            }
            if (currentSave == null)
            {
                currentSave = CreateNewSaveScope(currentRunId);
                database.saves.Add(currentSave);
            }
            currentSave.enabled = true;
            UpdateActiveSavePlayerName();
            promotionQueries.Clear();
            conversationReadyPromotions.Clear();
            attemptedMarriageProposalSessions.Clear();
            activeMarriageProposalDialog = null;
            bypassPendingMarriageProposal = false;
            initializingRosterRunId = currentRunId;
            initializingRosterCount = -1;
            stableInitializingRosterChecks = 0;
            npcReconciliationPending = true;
            SaveDatabase();
            if (globallyEnabled)
                RefreshAndApplyRelationships();
            Log.LogInfo(
                "Dynamic NPC Relationships enabled for run "
                + currentRunId + ".");
            return true;
        }

        if (currentSave == null)
            return true;
        if (!currentSave.enabled)
            return true;

        RestoreOriginalRelationshipsForDisable();
        FreezeDailyDecreaseClock();
        currentSave.enabled = false;
        foreach (PairRelationshipData pair in currentSave.pairs)
        {
            pair.RuntimeRevision = pair.RuntimeRevision == int.MaxValue
                ? 1
                : pair.RuntimeRevision + 1;
        }
        promotionQueries.Clear();
        conversationReadyPromotions.Clear();
        conversationAssessments.Clear();
        attemptedMarriageProposalSessions.Clear();
        activeMarriageProposalDialog = null;
        bypassPendingMarriageProposal = false;
        SaveDatabase();
        Log.LogInfo(
            "Dynamic NPC Relationships disabled for run "
            + currentRunId + "; saved progress was retained.");
        return true;
    }

    private void DeactivateSaveScopeForMainMenu()
    {
        if (currentSave == null && string.IsNullOrEmpty(currentRunId))
            return;

        currentSave = null;
        currentRunId = "";
        promotionQueries.Clear();
        conversationReadyPromotions.Clear();
        attemptedMarriageProposalSessions.Clear();
        activeMarriageProposalDialog = null;
        bypassPendingMarriageProposal = false;
        initializingRosterRunId = "";
        initializingRosterCount = -1;
        stableInitializingRosterChecks = 0;
        npcReconciliationPending = true;
    }

    private void ApplyDailyScoreDecrease()
    {
        if (currentSave == null || WorldInfoManager.Instance == null)
            return;

        int currentDay;
        try
        {
            currentDay = WorldInfoManager.Instance.GetCurrentDay();
        }
        catch (Exception exception)
        {
            Log.LogWarning(
                "Could not evaluate daily relationship score decreases: "
                + exception.Message);
            return;
        }

        int decreasePerDay = Math.Max(0, dailyScoreDecrease);
        bool changed = false;
        foreach (PairRelationshipData pair in currentSave.pairs)
        {
            if (pair.lastDailyDecreaseDay < 0
                || currentDay < pair.lastDailyDecreaseDay)
            {
                pair.lastDailyDecreaseDay = currentDay;
                changed = true;
                continue;
            }
            if (currentDay == pair.lastDailyDecreaseDay)
                continue;

            int firstCompletedDay = pair.lastDailyDecreaseDay;
            int completedDays = currentDay - firstCompletedDay;
            pair.lastDailyDecreaseDay = currentDay;
            changed = true;

            if (pair.suspended
                || pair.tierLocked
                || pair.stage == DynamicRelationshipStage.Lover
                || pair.stage == DynamicRelationshipStage.Married
                || decreasePerDay == 0)
            {
                continue;
            }

            int conversationExemptDays =
                pair.lastSharedConversationDay >= firstCompletedDay
                && pair.lastSharedConversationDay < currentDay
                    ? 1
                    : 0;
            int decreaseDays = Math.Max(
                0, completedDays - conversationExemptDays);
            if (decreaseDays == 0)
                continue;

            int stageFloor = pair.stage switch
            {
                DynamicRelationshipStage.Friend =>
                    friendRetentionScore,
                DynamicRelationshipStage.Acquaintance =>
                    strangerThreshold + 1,
                DynamicRelationshipStage.Stranger =>
                    enemyRecoveryScore,
                _ => MinimumScore
            };
            if (pair.score <= stageFloor)
                continue;

            int previousScore = pair.score;
            long requestedDecrease =
                (long)decreasePerDay * decreaseDays;
            pair.score = Math.Max(
                stageFloor,
                (int)Math.Max(
                    MinimumScore,
                    pair.score - requestedDecrease));
            if (pair.score == previousScore)
                continue;

            pair.lastReason = "daily inactivity";
            if (verboseLogging)
            {
                Log.LogInfo(
                    $"Daily inactivity changed {pair.firstNpc} / "
                    + $"{pair.secondNpc} from {previousScore} to "
                    + $"{pair.score} without crossing the {pair.stage} "
                    + "stage boundary.");
            }
        }

        if (changed)
            SaveDatabase();
    }

    private void FreezeDailyDecreaseClock()
    {
        if (currentSave == null || WorldInfoManager.Instance == null)
            return;

        int currentDay;
        try
        {
            currentDay = WorldInfoManager.Instance.GetCurrentDay();
        }
        catch
        {
            return;
        }

        bool changed = false;
        foreach (PairRelationshipData pair in currentSave.pairs)
        {
            if (pair.lastDailyDecreaseDay == currentDay)
                continue;
            pair.lastDailyDecreaseDay = currentDay;
            changed = true;
        }
        if (changed)
            SaveDatabase();
    }

    private void RestoreOriginalRelationshipsForDisable()
    {
        if (currentSave == null || NeuralNPC.neuralNPCs == null)
            return;

        foreach (PairRelationshipData pair in currentSave.pairs)
        {
            if (pair.suspended
                || !TryResolvePair(
                    pair, out NeuralNPC first, out NeuralNPC second)
                || !IsPairCompatibleWithManagedState(
                    first, second, pair,
                    tolerateUnappliedBaseState: false))
            {
                continue;
            }

            RestoreOriginalNativeRelationship(first, second, pair);
        }
    }

    internal void RecordSharedConversation(IReadOnlyList<NeuralNPC> participants)
    {
        if (!pluginEnabled || participants.Count < 2)
            return;

        NeuralNPC[] distinct = participants
            .Where(npc => npc != null)
            .Distinct()
            .ToArray();
        for (int first = 0; first < distinct.Length - 1; first++)
        {
            for (int second = first + 1; second < distinct.Length; second++)
            {
                if (PrepareSharedConversationAssessment(
                        distinct[first], distinct[second],
                        out PairRelationshipData pair,
                        out string conversationRunId))
                {
                    conversationAssessments.Enqueue(
                        new ConversationAssessmentRequest
                        {
                            First = distinct[first],
                            Second = distinct[second],
                            Pair = pair,
                            RunId = conversationRunId,
                            Revision = pair.RuntimeRevision,
                            DialogContext = new List<NeuralNPC.DialogElement>(
                                distinct[first].dialogElements)
                        });
                }
            }
        }
        TryStartNextConversationAssessment();
    }

    private void TryStartNextConversationAssessment()
    {
        if (conversationAssessmentInProgress)
            return;

        while (conversationAssessments.Count > 0)
        {
            ConversationAssessmentRequest request =
                conversationAssessments.Dequeue();
            RefreshSaveIdentity();
            if (!pluginEnabled
                || !string.Equals(
                    currentRunId, request.RunId, StringComparison.Ordinal)
                || currentSave == null
                || request.Pair.suspended
                || request.Pair.tierLocked
                || request.Pair.RuntimeRevision != request.Revision
                || !currentSave.pairs.Contains(request.Pair))
            {
                continue;
            }

            conversationAssessmentInProgress = true;
            _ = AssessSharedConversationAsync(request);
            return;
        }
    }

    private bool PrepareSharedConversationAssessment(
        NeuralNPC first,
        NeuralNPC second,
        out PairRelationshipData pair,
        out string conversationRunId)
    {
        pair = null!;
        conversationRunId = "";
        RefreshSaveIdentity();
        if (currentSave == null || string.IsNullOrWhiteSpace(currentRunId))
            return false;
        if (!IsPairEnabledForCurrentSave(first, second))
            return false;

        PairRelationshipData? existingPair = FindPair(first, second);
        if (existingPair == null)
        {
            if (!IsUnassignedAcquaintancePair(first, second))
                return false;

            RefreshSaveIdentity();
            if (currentSave == null)
                return false;
            (string firstKey, string secondKey) = CreateOrderedKeys(
                first, second);
            existingPair = new PairRelationshipData
            {
                firstNpc = firstKey,
                secondNpc = secondKey,
                originalNativeTier = (int)NPCRelationship.Acquaintance,
                stage = DynamicRelationshipStage.Acquaintance,
                nextReviewScore = Math.Max(1, friendReviewScore)
            };
            currentSave.pairs.Add(existingPair);
        }
        else if (existingPair.suspended
            || !IsPairCompatibleWithManagedState(
                first, second, existingPair,
                tolerateUnappliedBaseState: true))
        {
            if (!existingPair.suspended)
            {
                existingPair.suspended = true;
                SaveDatabase();
                Log.LogWarning(
                    "Skipping conversation review and suspending dynamic "
                    + $"relationship {existingPair.firstNpc} / "
                    + $"{existingPair.secondNpc}: "
                    + "another source changed the pair.");
            }
            return false;
        }

        pair = existingPair;
        pair.interactionCount++;
        pair.lastInteractionTurn = WorldInfoManager.Instance != null
            ? WorldInfoManager.Instance.TotalTurnCount
            : pair.lastInteractionTurn;
        pair.lastSharedConversationDay = WorldInfoManager.Instance != null
            ? WorldInfoManager.Instance.GetCurrentDay()
            : pair.lastSharedConversationDay;
        if (pair.tierLocked)
        {
            pair.lastReason =
                "shared conversation recorded without scoring because the "
                + "tier is locked";
            SaveDatabase();
            return false;
        }
        pair.lastReason = "shared conversation awaiting sentiment review";
        conversationRunId = currentRunId;
        SaveDatabase();
        return true;
    }

    private async Task AssessSharedConversationAsync(
        ConversationAssessmentRequest request)
    {
        NeuralNPC first = request.First;
        NeuralNPC second = request.Second;
        PairRelationshipData pair = request.Pair;
        string firstName = first.GetFinalName();
        string secondName = second.GetFinalName();
        try
        {
            if (InferenceServerSetupHandler.Instance == null)
            {
                RefreshSaveIdentity();
                if (string.Equals(
                        currentRunId, request.RunId,
                        StringComparison.Ordinal)
                    && currentSave != null
                    && pair.RuntimeRevision == request.Revision
                    && currentSave.pairs.Contains(pair))
                {
                    pair.lastReason =
                        "shared conversation sentiment review unavailable";
                    SaveDatabase();
                }
                Log.LogWarning(
                    "Could not classify the completed conversation between "
                    + $"{firstName} and {secondName}: no model is available. "
                    + "No relationship score was applied.");
                return;
            }

            string positiveQuestion =
                "Considering only the completed conversation above, did "
                + $"the interaction between {firstName} and {secondName} "
                + "have an overall positive effect on their relationship? "
                + "Warm, respectful, cooperative, supportive, or enjoyable "
                + "interaction can be positive.";
            string negativeQuestion =
                "Considering only the completed conversation above, did "
                + $"the interaction between {firstName} and {secondName} "
                + "have an overall negative effect on their relationship? "
                + "Hostile, insulting, threatening, rejecting, or harmful "
                + "interaction can be negative.";
            Task<bool> positiveTask = AskConversationYesNoAsync(
                first, positiveQuestion, request.DialogContext);
            Task<bool> negativeTask = AskConversationYesNoAsync(
                first, negativeQuestion, request.DialogContext);
            try
            {
                await Task.WhenAll(positiveTask, negativeTask);
            }
            catch (Exception queryException)
            {
                Log.LogWarning(
                    $"One sentiment check for {firstName} / {secondName} "
                    + "failed; any completed native-style check will still "
                    + "be honored: " + queryException.Message);
            }

            bool positiveAvailable =
                positiveTask.Status == TaskStatus.RanToCompletion;
            bool negativeAvailable =
                negativeTask.Status == TaskStatus.RanToCompletion;
            if (!positiveAvailable && !negativeAvailable)
            {
                throw new InvalidOperationException(
                    "Both conversation sentiment checks failed.");
            }

            bool positive = positiveAvailable && positiveTask.Result;
            bool negative = negativeAvailable && negativeTask.Result;
            string sentiment = positive
                ? "positive"
                : negative
                    ? "negative"
                    : "neutral";

            RefreshSaveIdentity();
            if (!pluginEnabled
                || !string.Equals(
                    currentRunId, request.RunId,
                    StringComparison.Ordinal)
                || currentSave == null
                || pair.suspended
                || pair.tierLocked
                || pair.RuntimeRevision != request.Revision
                || !currentSave.pairs.Contains(pair))
            {
                Log.LogInfo(
                    "Discarded stale conversation classification for "
                    + $"{firstName} / {secondName} because the active save "
                    + "changed.");
                return;
            }

            if (!positive && !negative)
            {
                pair.lastReason = "neutral shared conversation";
                SaveDatabase();
                Log.LogInfo(
                    $"Classified the completed conversation between "
                    + $"{firstName} and {secondName} as neutral. No "
                    + "relationship score was applied.");
                return;
            }

            int delta = positive
                ? sharedConversationGain
                : -sharedConversationLoss;
            int expectedAppliedDelta =
                pair.stage == DynamicRelationshipStage.Married
                && delta < -MarriedNegativeLossCap
                    ? -MarriedNegativeLossCap
                    : delta;
            bool scoreApplied = AddInteraction(
                first, second, delta,
                positive
                    ? "positive shared conversation"
                    : "negative shared conversation",
                hasFreshConversationContext: true,
                countInteraction: false,
                allowZeroDelta: true);
            Log.LogInfo(
                $"Classified the completed conversation between {firstName} "
                + $"and {secondName} as "
                + $"{sentiment}. "
                + (scoreApplied
                    ? $"Applied score change: "
                        + $"{expectedAppliedDelta:+#;-#;0}."
                    : "The pair was no longer eligible, so no score change "
                        + "was applied."));
        }
        catch (Exception exception)
        {
            RefreshSaveIdentity();
            if (string.Equals(
                    currentRunId, request.RunId, StringComparison.Ordinal)
                && currentSave != null
                && pair.RuntimeRevision == request.Revision
                && currentSave.pairs.Contains(pair))
            {
                pair.lastReason =
                    "shared conversation sentiment review failed";
                SaveDatabase();
            }
            Log.LogWarning(
                $"Conversation classification for {firstName} / {secondName} "
                + "failed; no relationship score was applied: "
                + exception);
        }
        finally
        {
            conversationAssessmentInProgress = false;
            TryStartNextConversationAssessment();
        }
    }

    private static async Task<bool> AskConversationYesNoAsync(
        NeuralNPC reviewer,
        string question,
        List<NeuralNPC.DialogElement> dialogContext)
    {
        string modelResult = await reviewer.AskQuestion(
            question + " Answer with just \"yes\" or \"no\".",
            deterministic: true,
            takes: -2,
            grammar: "root ::= [A-Za-z0-9 .,!?'-]*",
            targetDialogElements: dialogContext);
        return APIModelHandler.HandleNoGrammarYesNoInverse(modelResult);
    }

    internal void RecordRumorDelivery(NeuralNPC source, NeuralNPC target)
    {
        if (!pluginEnabled)
            return;
        AddInteraction(
            source, target, rumorGain, "rumor exchange",
            hasFreshConversationContext: false,
            countsAsDailyContact: true,
            allowZeroDelta: true);
    }

    /// <summary>
    /// Public integration point for other plugins. Positive or negative deltas
    /// alter the hidden score but can only promote at configured boundaries.
    /// </summary>
    public static bool AddRelationshipScore(
        NeuralNPC first,
        NeuralNPC second,
        int delta,
        string reason = "external interaction")
    {
        if (ReferenceEquals(Instance, null) || first == null || second == null)
            return false;
        return Instance.AddInteraction(
            first, second, delta, reason,
            hasFreshConversationContext: false,
            countInteraction: false);
    }

    /// <summary>
    /// Returns whether an external plugin can safely replace the current
    /// dialog turn with a proposal from this NPC to the player.
    /// </summary>
    public static bool CanPresentNpcToPlayerProposal(NeuralNPC proposer)
    {
        if (ReferenceEquals(Instance, null)
            || proposer == null
            || DialogBox.Instance == null
            || !DialogBox.Instance.isOpen
            || DialogBox.Instance.isAnimatingText
            || Instance.activeMarriageProposalDialog != null)
        {
            return false;
        }

        if (NeuralNPC.multiDialogParticipants != null)
            return NeuralNPC.multiDialogParticipants.Contains(proposer);
        return ReferenceEquals(
            NeuralNPC.currentActiveDialogNeuralNPC, proposer);
    }

    /// <summary>
    /// Uses the NPC's current Silverpine conversation context to write a
    /// proposal to the player. This does not display it or change any state.
    /// </summary>
    public static async Task<string> GenerateNpcToPlayerProposalAsync(
        NeuralNPC proposer,
        string additionalInstructions = "")
    {
        if (proposer == null)
            throw new ArgumentNullException(nameof(proposer));
        if (Player.Instance == null)
            throw new InvalidOperationException("No active player exists.");
        if (InferenceServerSetupHandler.Instance == null)
        {
            throw new InvalidOperationException(
                "No active Silverpine model is available.");
        }

        string proposerName = proposer.GetFinalName();
        string playerName = Player.Instance.playerName;
        string extra = string.IsNullOrWhiteSpace(additionalInstructions)
            ? ""
            : " Additional direction: " + additionalInstructions.Trim();
        string proposal = (await proposer.AskQuestion(
            $"{proposerName} has decided to ask {playerName} to marry "
            + $"{Player.Instance.pronouns.Him()}. Write {proposerName}'s "
            + "sincere, in-character marriage proposal addressed directly "
            + $"to {playerName}. Base it on their actual conversation, "
            + "shared history, relationship, and personalities. Output only "
            + "the words and actions of the proposal, with no analysis, "
            + "acceptance decision, or narration outside the character's "
            + "action." + extra,
            deterministic: false,
            takes: -1)).Trim();
        if (string.IsNullOrWhiteSpace(proposal))
        {
            throw new InvalidOperationException(
                $"The model returned an empty proposal for {proposerName}.");
        }
        return proposal;
    }

    /// <summary>
    /// Strictly classifies the player's exact response to a proposal. This
    /// does not change player or NPC relationship state.
    /// </summary>
    public static async Task<bool> ClassifyPlayerProposalResponseAsync(
        NeuralNPC proposer,
        string proposal,
        string playerResponse)
    {
        if (proposer == null)
            throw new ArgumentNullException(nameof(proposer));
        if (Player.Instance == null)
            throw new InvalidOperationException("No active player exists.");
        if (InferenceServerSetupHandler.Instance == null)
        {
            throw new InvalidOperationException(
                "No active Silverpine model is available.");
        }

        string proposerName = proposer.GetFinalName();
        string playerName = Player.Instance.playerName;
        bool accepted = await proposer.AskYesNoCotQuestion(
            $"{proposerName} proposed marriage to {playerName}. The exact "
            + $"proposal was:\n\n{proposal}\n\n{playerName}'s exact response "
            + $"was:\n\n{playerResponse}\n\nDoes {playerName}'s response "
            + "clearly and willingly accept the marriage proposal? Answer "
            + "no for rejection, ambiguity, postponement, hesitation without "
            + "acceptance, or a condition that does not yet commit to "
            + "marriage.",
            -2);
        InvokeProposalClassificationHooks(
            proposer, proposal, playerResponse, accepted);
        return accepted;
    }

    /// <summary>
    /// Displays a supplied proposal as the NPC's current dialog turn. The
    /// optional async handler receives the player's exact next input before
    /// Silverpine resumes its normal single- or multi-dialog flow.
    /// </summary>
    public static bool TryPresentNpcToPlayerProposal(
        NeuralNPC proposer,
        string proposal,
        Func<string, Task>? responseHandler = null)
    {
        if (!CanPresentNpcToPlayerProposal(proposer)
            || string.IsNullOrWhiteSpace(proposal))
        {
            return false;
        }
        return Instance.PresentNpcToPlayerProposal(
            proposer, proposal.Trim(), responseHandler);
    }

    /// <summary>
    /// Opt-in convenience hook that presents the proposal, classifies the
    /// player's exact response, and returns the final yes/no result to the
    /// requesting plugin before normal dialog resumes.
    /// </summary>
    public static bool TryPresentAndClassifyNpcToPlayerProposal(
        NeuralNPC proposer,
        string proposal,
        Func<bool, Task>? classificationHandler = null)
    {
        if (string.IsNullOrWhiteSpace(proposal))
            return false;
        string exactProposal = proposal.Trim();
        return TryPresentNpcToPlayerProposal(
            proposer,
            exactProposal,
            async playerResponse =>
            {
                bool accepted =
                    await ClassifyPlayerProposalResponseAsync(
                        proposer, exactProposal, playerResponse);
                if (classificationHandler != null)
                    await classificationHandler(accepted);
            });
    }

    private bool PresentNpcToPlayerProposal(
        NeuralNPC proposer,
        string proposal,
        Func<string, Task>? responseHandler)
    {
        List<NeuralNPC>? multiParticipants =
            NeuralNPC.multiDialogParticipants;
        List<NeuralNPC> historyTargets = multiParticipants != null
            ? multiParticipants.Where(npc => npc != null).Distinct().ToList()
            : new List<NeuralNPC> { proposer };
        string proposalTurn = GetNamedNpcTurn(
            proposer.GetFinalName(), proposal);
        foreach (NeuralNPC participant in historyTargets)
        {
            participant.dialogElements.AddToDialog(
                SpeakerType.NPC, proposalTurn);
        }

        NeuralNPC.currentActiveDialogNeuralNPC = proposer;
        proposer.DoStartNPCMode(DialogBox.SpriteSwitchMode.Normal);
        DialogBox.Instance.StopContinueOnlyMode();
        DialogBox.Instance.DisplayText(
            FormatNpcTurnForDialog(
                proposalTurn, proposer.GetFinalName()),
            response => _ = HandleNpcToPlayerProposalResponseAsync(
                proposer,
                proposal,
                response,
                multiParticipants,
                responseHandler),
            new List<UpperButtonOption>());
        InvokeProposalPresentedHooks(proposer, proposal);
        return true;
    }

    private async Task HandleNpcToPlayerProposalResponseAsync(
        NeuralNPC proposer,
        string proposal,
        string response,
        List<NeuralNPC>? multiParticipants,
        Func<string, Task>? responseHandler)
    {
        InvokeProposalResponseHooks(proposer, proposal, response);
        if (responseHandler != null)
        {
            try
            {
                if (DialogBox.Instance != null
                    && DialogBox.Instance.isOpen)
                {
                    DialogBox.Instance.DisplayLoading(
                        "Processing " + Player.Instance.playerName
                        + "'s response to " + proposer.GetFinalName()
                        + "'s proposal.",
                        accessedFromInsideDialogBox: true);
                }
                await responseHandler(response);
            }
            catch (Exception exception)
            {
                Log.LogWarning(
                    "An NPC-to-player proposal response handler failed: "
                    + exception);
            }
        }

        if (DialogBox.Instance == null || !DialogBox.Instance.isOpen)
            return;
        if (multiParticipants != null)
        {
            if (!ReferenceEquals(
                    NeuralNPC.multiDialogParticipants, multiParticipants)
                || !multiParticipants.Contains(proposer))
            {
                return;
            }
            NeuralNPC.OnMultiInputCallback(null, response);
            return;
        }
        if (NeuralNPC.multiDialogParticipants != null
            || !ReferenceEquals(
                NeuralNPC.currentActiveDialogNeuralNPC, proposer))
        {
            return;
        }
        try
        {
            SingleDialogInputMethod.Invoke(
                proposer, new object?[] { response });
        }
        catch (Exception exception)
        {
            Log.LogWarning(
                "Could not resume Silverpine's single-NPC dialog after "
                + "the player's proposal response: " + exception);
            DialogBox.Instance.QueueDialogEnd();
        }
    }

    private static void InvokeProposalPresentedHooks(
        NeuralNPC proposer,
        string proposal)
    {
        if (NpcToPlayerProposalPresented == null)
            return;
        foreach (Delegate hook in
                 NpcToPlayerProposalPresented.GetInvocationList())
        {
            try
            {
                ((Action<NeuralNPC, string>)hook)(proposer, proposal);
            }
            catch (Exception exception)
            {
                Log.LogWarning(
                    "An NPC-to-player proposal-presented hook failed: "
                    + exception);
            }
        }
    }

    private static void InvokeProposalResponseHooks(
        NeuralNPC proposer,
        string proposal,
        string response)
    {
        if (NpcToPlayerProposalResponseSubmitted == null)
            return;
        foreach (Delegate hook in
                 NpcToPlayerProposalResponseSubmitted.GetInvocationList())
        {
            try
            {
                ((Action<NeuralNPC, string, string>)hook)(
                    proposer, proposal, response);
            }
            catch (Exception exception)
            {
                Log.LogWarning(
                    "An NPC-to-player proposal-response hook failed: "
                    + exception);
            }
        }
    }

    private static void InvokeProposalClassificationHooks(
        NeuralNPC proposer,
        string proposal,
        string response,
        bool accepted)
    {
        if (NpcToPlayerProposalResponseClassified == null)
            return;
        foreach (Delegate hook in
                 NpcToPlayerProposalResponseClassified.GetInvocationList())
        {
            try
            {
                ((Action<NeuralNPC, string, string, bool>)hook)(
                    proposer, proposal, response, accepted);
            }
            catch (Exception exception)
            {
                Log.LogWarning(
                    "An NPC-to-player proposal-classification hook failed: "
                    + exception);
            }
        }
    }

    public static bool IsRuntimeAvailable() =>
        !ReferenceEquals(Instance, null);

    /// <summary>
    /// Returns the BepInEx master-switch state. A run may retain an enabled
    /// save-wide setting while this master switch temporarily disables it.
    /// </summary>
    public static bool IsDynamicSystemGloballyEnabled() =>
        !ReferenceEquals(Instance, null) && Instance.globallyEnabled;

    /// <summary>
    /// Returns true when the active run already has relationship save data.
    /// A new run has no data file until explicitly enabled.
    /// </summary>
    public static bool HasDynamicSystemSaveDataForCurrentSave()
    {
        if (ReferenceEquals(Instance, null))
            return false;
        Instance.RefreshSaveIdentity();
        return Instance.currentSave != null
            && !string.IsNullOrWhiteSpace(
                Instance.currentSave.RuntimeSourcePath)
            && File.Exists(Instance.currentSave.RuntimeSourcePath);
    }

    /// <summary>
    /// Returns the active run's saved toggle, independent of the BepInEx
    /// master switch. Runs without a relationship JSON are disabled.
    /// </summary>
    public static bool IsDynamicSystemEnabledForCurrentSave()
    {
        if (ReferenceEquals(Instance, null))
            return false;
        Instance.RefreshSaveIdentity();
        return Instance.currentSave?.enabled == true;
    }

    /// <summary>
    /// Enables or disables all Dynamic Relationships behavior for the active
    /// run. First-time enablement creates that run's relationship JSON;
    /// disabling preserves existing progress in the file.
    /// </summary>
    public static bool SetDynamicSystemEnabledForCurrentSave(bool enabled)
    {
        if (ReferenceEquals(Instance, null))
            return false;
        return Instance.SetCurrentSaveEnabled(enabled);
    }

    public static int GetConfiguredMaximumScore() =>
        ReferenceEquals(Instance, null) ? 150 : Instance.MaximumScore;

    public static int GetConfiguredMinimumScore() =>
        ReferenceEquals(Instance, null) ? -100 : Instance.MinimumScore;

    public static int GetConfiguredEnemyThreshold() =>
        ReferenceEquals(Instance, null) ? -50 : Instance.enemyThreshold;

    public static bool IsPairEnabledBySaveDefaults(
        NeuralNPC first,
        NeuralNPC second)
    {
        if (ReferenceEquals(Instance, null) || first == null || second == null)
            return false;
        Instance.RefreshSaveIdentity();
        return Instance.IsPairEnabledForCurrentSave(first, second);
    }

    public static bool TryGetPromotionLockInfo(
        NeuralNPC first,
        NeuralNPC second,
        out bool locked,
        out string ceilingStage,
        out string reason,
        out int rejectionThreshold)
    {
        locked = false;
        ceilingStage = "";
        reason = "";
        rejectionThreshold = 2;
        if (ReferenceEquals(Instance, null) || first == null || second == null)
            return false;

        Instance.RefreshSaveIdentity();
        PairRelationshipData? pair = Instance.FindPair(first, second);
        if (pair == null)
            return false;
        locked = pair.promotionCeilingLocked;
        ceilingStage = pair.promotionCeilingStage.ToString();
        reason = pair.promotionLockReason;
        rejectionThreshold = Instance.romanticRejectionsBeforeLockReview;
        return true;
    }

    public static bool TryGetTierLockInfo(
        NeuralNPC first,
        NeuralNPC second,
        out bool locked)
    {
        locked = false;
        if (ReferenceEquals(Instance, null) || first == null || second == null)
            return false;
        Instance.RefreshSaveIdentity();
        PairRelationshipData? pair = Instance.FindPair(first, second);
        if (pair == null)
            return false;
        locked = pair.tierLocked;
        return true;
    }

    /// <summary>
    /// Optional editor/inter-plugin bridge. Returns false for pairs this plugin
    /// has never adopted in the active save.
    /// </summary>
    public static bool TryGetRelationshipInfo(
        NeuralNPC first,
        NeuralNPC second,
        out int score,
        out string stage,
        out int nextReviewScore,
        out bool suspended,
        out int interactionCount,
        out int strangerStageThreshold,
        out int acquaintanceStageThreshold,
        out int friendPromotionThreshold,
        out int loverPromotionThreshold,
        out int marriagePromotionThreshold,
        out int friendRetentionThreshold,
        out int loverRetentionThreshold,
        out int marriageRetentionThreshold,
        out bool loversEnabled,
        out bool marriageEnabled,
        out bool promotionAwaitingConversation)
    {
        score = 0;
        stage = "";
        nextReviewScore = 0;
        suspended = false;
        interactionCount = 0;
        strangerStageThreshold = 0;
        acquaintanceStageThreshold = 0;
        friendPromotionThreshold = 0;
        loverPromotionThreshold = 0;
        marriagePromotionThreshold = 0;
        friendRetentionThreshold = 0;
        loverRetentionThreshold = 0;
        marriageRetentionThreshold = 0;
        loversEnabled = false;
        marriageEnabled = false;
        promotionAwaitingConversation = false;
        if (ReferenceEquals(Instance, null) || first == null || second == null)
            return false;

        Instance.RefreshSaveIdentity();
        PairRelationshipData? pair = Instance.FindPair(first, second);
        if (pair == null)
            return false;
        score = pair.score;
        stage = pair.stage.ToString();
        nextReviewScore = pair.tierLocked
            || (pair.promotionCeilingLocked
                && pair.stage >= pair.promotionCeilingStage)
                ? Instance.MaximumScore + 1
                : pair.stage switch
                {
                    DynamicRelationshipStage.Enemies =>
                        Instance.enemyRecoveryScore,
                    DynamicRelationshipStage.Stranger => Math.Max(
                        pair.nextReviewScore,
                        Instance.acquaintanceThreshold),
                    DynamicRelationshipStage.Acquaintance => Math.Max(
                        pair.nextReviewScore, Instance.friendReviewScore),
                    DynamicRelationshipStage.Friend => Math.Max(
                        pair.nextReviewScore, Instance.loverReviewScore),
                    DynamicRelationshipStage.Lover => Math.Max(
                        pair.nextReviewScore, Instance.marriageReviewScore),
                    _ => Instance.MaximumScore + 1
                };
        suspended = pair.suspended;
        interactionCount = pair.interactionCount;
        strangerStageThreshold = Instance.strangerThreshold;
        acquaintanceStageThreshold = Instance.acquaintanceThreshold;
        friendPromotionThreshold = Instance.friendReviewScore;
        loverPromotionThreshold = Instance.loverReviewScore;
        marriagePromotionThreshold = Instance.marriageReviewScore;
        friendRetentionThreshold = Instance.friendRetentionScore;
        loverRetentionThreshold = Instance.loverRetentionScore;
        marriageRetentionThreshold = Instance.marriageRetentionScore;
        loversEnabled = Instance.allowLovers;
        marriageEnabled = Instance.allowMarriage;
        promotionAwaitingConversation = pair.marriageProposalPending
            || ((Instance.requirePromotionQueries
                    || pair.stage == DynamicRelationshipStage.Lover)
                && Instance.IsPromotionDue(pair)
                && !Instance.conversationReadyPromotions.Contains(pair.Id)
                && !Instance.promotionQueries.Contains(pair.Id));
        return true;
    }

    /// <summary>
    /// Optional editor bridge. Replaces the save-scoped score, stage, and
    /// full tier lock without creating a Runtime NPC Editor dependency.
    /// </summary>
    public static bool SetDynamicRelationshipState(
        NeuralNPC first,
        NeuralNPC second,
        int score,
        string stageName,
        bool tierLocked)
    {
        if (ReferenceEquals(Instance, null) || first == null || second == null)
            return false;
        if (!Enum.TryParse(
                stageName, ignoreCase: true,
                out DynamicRelationshipStage requestedStage)
            || !Enum.IsDefined(
                typeof(DynamicRelationshipStage), requestedStage))
        {
            return false;
        }

        Instance.RefreshSaveIdentity();
        PairRelationshipData? pair = Instance.FindPair(first, second);
        if (pair == null || Instance.currentSave == null || pair.suspended)
            return false;
        if (Instance.pluginEnabled
            && !IsPairCompatibleWithManagedState(
                first, second, pair,
                tolerateUnappliedBaseState: true))
        {
            pair.suspended = true;
            Instance.SaveDatabase();
            Log.LogWarning(
                $"Could not edit {pair.firstNpc} / {pair.secondNpc}; "
                + "another source changed the native relationship, so "
                + "dynamic control was suspended.");
            return false;
        }

        DynamicRelationshipStage previousStage = pair.stage;
        bool stageChanged = previousStage != requestedStage;
        bool tierLockChanged = pair.tierLocked != tierLocked;

        pair.RuntimeRevision = pair.RuntimeRevision == int.MaxValue
            ? 1
            : pair.RuntimeRevision + 1;
        Instance.promotionQueries.Remove(pair.Id);
        Instance.conversationReadyPromotions.Remove(pair.Id);
        pair.marriageProposalPending = false;
        pair.score = Math.Max(
            Instance.MinimumScore,
            Math.Min(Instance.MaximumScore, score));
        pair.stage = requestedStage;
        pair.tierLocked = tierLocked;
        if (stageChanged)
        {
            pair.firstLoverRejections = 0;
            pair.secondLoverRejections = 0;
            pair.firstMarriageRejections = 0;
            pair.secondMarriageRejections = 0;
            if (pair.promotionCeilingLocked
                && requestedStage > pair.promotionCeilingStage)
            {
                pair.promotionCeilingLocked = false;
                pair.promotionCeilingStage = requestedStage;
                pair.promotionLockReason = "";
            }
        }
        pair.nextReviewScore = tierLocked
            || (pair.promotionCeilingLocked
                && requestedStage >= pair.promotionCeilingStage)
                ? Instance.MaximumScore + 1
                : requestedStage switch
                {
                    DynamicRelationshipStage.Enemies =>
                        Instance.enemyRecoveryScore,
                    DynamicRelationshipStage.Stranger =>
                        Instance.acquaintanceThreshold,
                    DynamicRelationshipStage.Acquaintance =>
                        Instance.friendReviewScore,
                    DynamicRelationshipStage.Friend =>
                        Instance.loverReviewScore,
                    DynamicRelationshipStage.Lover =>
                        Instance.marriageReviewScore,
                    _ => Instance.MaximumScore + 1
                };
        pair.lastReason = "score, tier, or tier lock edited in Runtime NPC "
            + "Editor";

        if (requestedStage == DynamicRelationshipStage.Married)
        {
            SetWorldKnowledge(
                pair, first, second,
                WorldRelationshipKnowledgeState.Married);
        }
        else if (previousStage == DynamicRelationshipStage.Married
            || pair.worldKnowledgeState
                == WorldRelationshipKnowledgeState.Married)
        {
            SetWorldKnowledge(
                pair, first, second,
                WorldRelationshipKnowledgeState.Divorced);
        }

        if (Instance.pluginEnabled && !pair.suspended)
            ApplyManagedPairUnchecked(first, second, pair);
        Instance.SaveDatabase();
        if (tierLockChanged)
        {
            Log.LogInfo(
                $"Tier lock for {pair.firstNpc} / {pair.secondNpc} "
                + $"was {(tierLocked ? "enabled" : "disabled")} at "
                + $"{requestedStage}.");
        }
        return true;
    }

    /// <summary>
    /// Called by Runtime NPC Editor before it writes an explicit relationship.
    /// The editor override takes priority and dynamic writes stop for this pair.
    /// </summary>
    public static bool NotifyExternalRelationshipOverride(
        NeuralNPC first,
        NeuralNPC second)
    {
        if (ReferenceEquals(Instance, null) || first == null || second == null)
            return false;
        if (!Instance.pluginEnabled)
            return false;
        Instance.RefreshSaveIdentity();
        PairRelationshipData? pair = Instance.FindPair(first, second);
        if (pair == null)
            return false;
        pair.suspended = true;
        pair.marriageProposalPending = false;
        Instance.SaveDatabase();
        Log.LogInfo(
            $"Runtime editor took control of {first.GetFinalName()} / "
            + $"{second.GetFinalName()}; dynamic progression suspended.");
        return true;
    }

    /// <summary>
    /// Returns true when an untracked pair can be explicitly enrolled by an
    /// editor. Authored labels and non-symmetric tiers are never eligible.
    /// </summary>
    public static bool CanBeginDynamicTracking(
        NeuralNPC first,
        NeuralNPC second)
    {
        if (ReferenceEquals(Instance, null) || first == null || second == null)
            return false;
        if (!Instance.pluginEnabled)
            return false;
        Instance.RefreshSaveIdentity();
        if (!Instance.IsPairEnabledForCurrentSave(first, second))
            return false;
        if (Instance.FindPair(first, second) != null)
            return false;
        return TryGetUnlabelledSymmetricTier(first, second, out _);
    }

    /// <summary>
    /// Explicitly enrolls an unlabelled symmetric Stranger or Acquaintance
    /// pair. This is the only path that adopts a pre-existing Stranger pair.
    /// </summary>
    public static bool BeginDynamicTracking(
        NeuralNPC first,
        NeuralNPC second)
    {
        if (ReferenceEquals(Instance, null) || first == null || second == null)
            return false;
        if (!Instance.pluginEnabled)
            return false;
        Instance.RefreshSaveIdentity();
        if (Instance.currentSave == null
            || !Instance.IsPairEnabledForCurrentSave(first, second)
            || Instance.FindPair(first, second) != null
            || !TryGetUnlabelledSymmetricTier(
                first, second, out NPCRelationship nativeTier))
        {
            return false;
        }

        Instance.RefreshSaveIdentity();
        if (Instance.currentSave == null)
            return false;

        (string firstKey, string secondKey) = CreateOrderedKeys(first, second);
        DynamicRelationshipStage initialStage = nativeTier
            == NPCRelationship.Stranger
                ? DynamicRelationshipStage.Stranger
                : DynamicRelationshipStage.Acquaintance;
        PairRelationshipData pair = new()
        {
            firstNpc = firstKey,
            secondNpc = secondKey,
            originalNativeTier = (int)nativeTier,
            stage = initialStage,
            score = initialStage == DynamicRelationshipStage.Stranger
                ? Instance.strangerThreshold
                : 0,
            nextReviewScore = initialStage
                == DynamicRelationshipStage.Stranger
                    ? Instance.acquaintanceThreshold
                    : Instance.friendReviewScore
        };
        Instance.currentSave.pairs.Add(pair);
        ApplyManagedPairUnchecked(first, second, pair);
        Instance.SaveDatabase();
        return true;
    }

    /// <summary>
    /// Called after Runtime NPC Editor removes its explicit override. Reapplies
    /// the saved dynamic state immediately.
    /// </summary>
    public static bool ResumeDynamicRelationship(
        NeuralNPC first,
        NeuralNPC second)
    {
        if (ReferenceEquals(Instance, null) || first == null || second == null)
            return false;
        if (!Instance.pluginEnabled)
            return false;
        Instance.RefreshSaveIdentity();
        PairRelationshipData? pair = Instance.FindPair(first, second);
        if (pair == null)
            return false;
        pair.suspended = false;
        ApplyManagedPairUnchecked(first, second, pair);
        Instance.SaveDatabase();
        return true;
    }

    /// <summary>
    /// Removes plugin-owned progress. Native fields are returned to the
    /// unassigned acquaintance state only when they still match this plugin's
    /// managed value; explicit editor/authored values are preserved.
    /// </summary>
    public static bool ResetDynamicRelationship(
        NeuralNPC first,
        NeuralNPC second)
    {
        if (ReferenceEquals(Instance, null) || first == null || second == null)
            return false;
        Instance.RefreshSaveIdentity();
        PairRelationshipData? pair = Instance.FindPair(first, second);
        if (pair == null || Instance.currentSave == null)
            return false;

        if (!pair.suspended && IsPairCompatibleWithManagedState(
                first, second, pair, tolerateUnappliedBaseState: false))
        {
            RestoreOriginalNativeRelationship(first, second, pair);
        }
        Instance.promotionQueries.Remove(pair.Id);
        Instance.conversationReadyPromotions.Remove(pair.Id);
        Instance.currentSave.pairs.Remove(pair);
        Instance.SaveDatabase();
        return true;
    }

    private bool AddInteraction(
        NeuralNPC first,
        NeuralNPC second,
        int delta,
        string reason,
        bool hasFreshConversationContext,
        bool countInteraction = true,
        bool countsAsDailyContact = false,
        bool allowZeroDelta = false)
    {
        if (!pluginEnabled || first == second
            || (delta == 0 && !allowZeroDelta))
            return false;

        RefreshSaveIdentity();
        if (currentSave == null)
            return false;
        if (!IsPairEnabledForCurrentSave(first, second))
            return false;

        PairRelationshipData? pair = FindPair(first, second);
        if (pair == null)
        {
            if (!IsUnassignedAcquaintancePair(first, second))
                return false;

            RefreshSaveIdentity();
            if (currentSave == null)
                return false;

            (string firstKey, string secondKey) = CreateOrderedKeys(
                first, second);
            pair = new PairRelationshipData
            {
                firstNpc = firstKey,
                secondNpc = secondKey,
                originalNativeTier = (int)NPCRelationship.Acquaintance,
                stage = DynamicRelationshipStage.Acquaintance,
                nextReviewScore = Math.Max(1, friendReviewScore)
            };
            currentSave.pairs.Add(pair);
        }
        else if (pair.suspended
            || !IsPairCompatibleWithManagedState(
                first, second, pair, tolerateUnappliedBaseState: true))
        {
            if (!pair.suspended)
            {
                pair.suspended = true;
                Log.LogWarning(
                    $"Suspending dynamic relationship {pair.firstNpc} / "
                    + $"{pair.secondNpc}: another source changed the pair.");
                SaveDatabase();
            }
            return false;
        }

        if (pair.tierLocked)
        {
            bool recordedContact = false;
            if (countInteraction)
            {
                pair.interactionCount++;
                recordedContact = true;
            }
            if ((countInteraction || countsAsDailyContact)
                && WorldInfoManager.Instance != null)
            {
                pair.lastInteractionTurn =
                    WorldInfoManager.Instance.TotalTurnCount;
            }
            if (countsAsDailyContact
                && WorldInfoManager.Instance != null)
            {
                pair.lastSharedConversationDay =
                    WorldInfoManager.Instance.GetCurrentDay();
                recordedContact = true;
            }
            if (recordedContact)
            {
                pair.lastReason = (reason ?? "interaction")
                    + " recorded without scoring because the tier is locked";
                SaveDatabase();
            }
            return recordedContact;
        }

        int appliedDelta = pair.stage == DynamicRelationshipStage.Married
            && delta < -MarriedNegativeLossCap
                ? -MarriedNegativeLossCap
                : delta;
        pair.score = Math.Max(
            MinimumScore,
            Math.Min(MaximumScore, pair.score + appliedDelta));
        if (countInteraction)
            pair.interactionCount++;
        pair.lastInteractionTurn = WorldInfoManager.Instance != null
            ? WorldInfoManager.Instance.TotalTurnCount
            : pair.lastInteractionTurn;
        if (countsAsDailyContact && WorldInfoManager.Instance != null)
        {
            pair.lastSharedConversationDay =
                WorldInfoManager.Instance.GetCurrentDay();
        }
        pair.lastReason = reason ?? "";

        ApplyDeterministicTransitions(pair, first, second);
        ApplyPair(pair, tolerateUnappliedBaseState: false);
        SaveDatabase();

        if (verboseLogging)
        {
            Log.LogInfo(
                $"{first.GetFinalName()} / {second.GetFinalName()}: "
                + $"{appliedDelta:+#;-#;0} ({reason}), score {pair.score}, "
                + $"stage {pair.stage}.");
        }

        bool marriageNeedsConversation =
            pair.stage == DynamicRelationshipStage.Lover;
        if (IsPromotionDue(pair)
            && (hasFreshConversationContext
                || (!marriageNeedsConversation
                    && !requirePromotionQueries)))
        {
            conversationReadyPromotions.Add(pair.Id);
            TryQueueNextPromotion();
        }
        return true;
    }

    private void ApplyDeterministicTransitions(
        PairRelationshipData pair,
        NeuralNPC first,
        NeuralNPC second)
    {
        if (pair.tierLocked)
            return;

        if (pair.stage == DynamicRelationshipStage.Married
            && pair.score < marriageRetentionScore)
        {
            SetWorldKnowledge(
                pair, first, second,
                WorldRelationshipKnowledgeState.Divorced);
            pair.stage = DynamicRelationshipStage.Acquaintance;
            pair.score = acquaintanceThreshold;
            pair.nextReviewScore = friendReviewScore;
            AddRelationshipMemory(
                first, second,
                "are divorced and now regard each other as acquaintances");
            ApplyManagedPairUnchecked(first, second, pair);
        }
        if (pair.stage == DynamicRelationshipStage.Lover
            && pair.score < loverRetentionScore)
        {
            pair.marriageProposalPending = false;
            pair.stage = DynamicRelationshipStage.Friend;
            pair.nextReviewScore = Math.Max(
                loverReviewScore, pair.score +
                Math.Max(1, deniedReviewAdditionalScore));
            AddRelationshipMemory(
                first, second,
                "are no longer lovers, but remain friends");
            ApplyManagedPairUnchecked(first, second, pair);
        }
        if (pair.stage == DynamicRelationshipStage.Friend
            && pair.score < friendRetentionScore)
        {
            pair.stage = DynamicRelationshipStage.Acquaintance;
            pair.nextReviewScore = Math.Max(
                friendReviewScore, pair.score +
                Math.Max(1, deniedReviewAdditionalScore));
            AddRelationshipMemory(
                first, second,
                "have grown apart and now regard each other as acquaintances");
            ApplyManagedPairUnchecked(first, second, pair);
        }
        if (pair.stage == DynamicRelationshipStage.Acquaintance
            && pair.score <= strangerThreshold)
        {
            pair.stage = DynamicRelationshipStage.Stranger;
            pair.nextReviewScore = acquaintanceThreshold;
            AddRelationshipMemory(
                first, second,
                "now regard each other as strangers");
            ApplyManagedPairUnchecked(first, second, pair);
        }

        ApplyEnemyBoundaryTransition(pair, first, second);

        if (pair.stage == DynamicRelationshipStage.Stranger
            && pair.score >= acquaintanceThreshold
            && (!pair.promotionCeilingLocked
                || pair.promotionCeilingStage
                    >= DynamicRelationshipStage.Acquaintance))
        {
            pair.stage = DynamicRelationshipStage.Acquaintance;
            pair.nextReviewScore = Math.Max(
                friendReviewScore, pair.score + 1);
            AddRelationshipMemory(
                first, second,
                "have become acquainted");
            ApplyManagedPairUnchecked(first, second, pair);
        }

        ApplyLockedCeilingRecovery(pair, first, second);
    }

    private bool ApplyEnemyBoundaryTransition(
        PairRelationshipData pair,
        NeuralNPC first,
        NeuralNPC second)
    {
        if (pair.tierLocked)
            return false;

        if (pair.stage == DynamicRelationshipStage.Stranger
            && pair.score <= enemyThreshold)
        {
            pair.stage = DynamicRelationshipStage.Enemies;
            pair.nextReviewScore = enemyRecoveryScore;
            AddRelationshipMemory(
                first, second,
                "now regard each other as enemies");
            ApplyManagedPairUnchecked(first, second, pair);
            return true;
        }
        if (pair.stage == DynamicRelationshipStage.Enemies
            && pair.score > enemyThreshold
            && (!pair.promotionCeilingLocked
                || pair.promotionCeilingStage
                    >= DynamicRelationshipStage.Stranger))
        {
            pair.stage = DynamicRelationshipStage.Stranger;
            pair.nextReviewScore = acquaintanceThreshold;
            AddRelationshipMemory(
                first, second,
                "are no longer enemies and now regard each other as strangers");
            ApplyManagedPairUnchecked(first, second, pair);
            return true;
        }
        return false;
    }

    private void ApplyLockedCeilingRecovery(
        PairRelationshipData pair,
        NeuralNPC first,
        NeuralNPC second)
    {
        if (pair.tierLocked || !pair.promotionCeilingLocked)
            return;

        if (pair.stage == DynamicRelationshipStage.Acquaintance
            && pair.promotionCeilingStage
                >= DynamicRelationshipStage.Friend
            && pair.score >= friendReviewScore)
        {
            pair.stage = DynamicRelationshipStage.Friend;
            pair.nextReviewScore = loverReviewScore;
            AddRelationshipMemory(
                first, second,
                "have rebuilt their friendship to its established level");
            ApplyManagedPairUnchecked(first, second, pair);
        }
        if (pair.stage == DynamicRelationshipStage.Friend
            && pair.promotionCeilingStage
                >= DynamicRelationshipStage.Lover
            && pair.score >= loverReviewScore)
        {
            pair.stage = DynamicRelationshipStage.Lover;
            pair.nextReviewScore = marriageReviewScore;
            AddRelationshipMemory(
                first, second,
                "have rebuilt their romantic relationship to its established level");
            ApplyManagedPairUnchecked(first, second, pair);
        }
    }

    private void TryQueueNextPromotion()
    {
        if (promotionQueryInProgress || currentSave == null
            || NeuralNPC.neuralNPCs == null)
        {
            return;
        }

        foreach (PairRelationshipData pair in currentSave.pairs)
        {
            if (pair.marriageProposalPending)
            {
                conversationReadyPromotions.Remove(pair.Id);
                continue;
            }
            if (!conversationReadyPromotions.Contains(pair.Id)
                || !IsPromotionDue(pair)
                || promotionQueries.Contains(pair.Id))
                continue;
            if (!TryResolvePair(pair, out NeuralNPC first, out NeuralNPC second))
                continue;

            if (pair.stage == DynamicRelationshipStage.Lover)
            {
                pair.marriageProposalPending = true;
                pair.lastReason = "marriage proposal pending the next "
                    + "multi-NPC conversation containing both NPCs";
                conversationReadyPromotions.Remove(pair.Id);
                SaveDatabase();
                Log.LogInfo(
                    $"{first.GetFinalName()} / {second.GetFinalName()} "
                    + "reached the marriage threshold. Their proposal will "
                    + "replace their turns the next time both are in a "
                    + "player-initiated multi-dialog.");
                continue;
            }

            promotionQueries.Add(pair.Id);
            promotionQueryInProgress = true;
            _ = ReviewPromotionAsync(
                pair, first, second, currentRunId);
            return;
        }
    }

    private bool IsPromotionDue(PairRelationshipData pair)
    {
        if (pair.suspended
            || pair.tierLocked
            || pair.promotionCeilingLocked
            || pair.score < pair.nextReviewScore)
            return false;
        if (pair.stage == DynamicRelationshipStage.Stranger)
            return false;
        if (pair.stage == DynamicRelationshipStage.Acquaintance)
            return pair.score >= friendReviewScore;
        if (pair.stage == DynamicRelationshipStage.Friend)
        {
            return allowLovers
                && pair.score >= loverReviewScore;
        }
        return pair.stage == DynamicRelationshipStage.Lover
            && allowMarriage
            && pair.score >= marriageReviewScore;
    }

    internal bool TryOverrideWithPendingMarriageProposal(
        NeuralNPC requestedSpeaker,
        Action? failureCallback,
        Action? successCallback)
    {
        if (bypassPendingMarriageProposal
            || !pluginEnabled
            || promotionQueryInProgress
            || requestedSpeaker == null
            || NeuralNPC.multiDialogParticipants == null)
        {
            return false;
        }

        RefreshSaveIdentity();
        if (currentSave == null || string.IsNullOrWhiteSpace(currentRunId))
            return false;

        List<NeuralNPC> participants =
            NeuralNPC.multiDialogParticipants;
        foreach (PairRelationshipData pair in currentSave.pairs)
        {
            if (!pair.marriageProposalPending)
                continue;
            if (pair.suspended
                || pair.tierLocked
                || pair.promotionCeilingLocked
                || pair.stage != DynamicRelationshipStage.Lover
                || !allowMarriage
                || pair.score < marriageReviewScore)
            {
                pair.marriageProposalPending = false;
                SaveDatabase();
                continue;
            }
            if (!TryResolvePair(
                    pair, out NeuralNPC first, out NeuralNPC second)
                || !participants.Contains(first)
                || !participants.Contains(second))
            {
                continue;
            }
            if (attemptedMarriageProposalSessions.TryGetValue(
                    pair.Id, out List<NeuralNPC> attemptedSession)
                && ReferenceEquals(attemptedSession, participants))
            {
                continue;
            }
            attemptedMarriageProposalSessions.Remove(pair.Id);

            SelectRomanticDirection(
                pair, first, second,
                out NeuralNPC proposer,
                out NeuralNPC recipient);
            MarriageProposalDialogContext dialogContext = new()
            {
                Pair = pair,
                RunId = currentRunId,
                RequestedSpeaker = requestedSpeaker,
                Proposer = proposer,
                Recipient = recipient,
                Participants = participants,
                FailureCallback = failureCallback,
                SuccessCallback = successCallback
            };

            try
            {
                promotionQueries.Add(pair.Id);
                promotionQueryInProgress = true;
                activeMarriageProposalDialog = dialogContext;
                NeuralNPC.currentActiveDialogNeuralNPC = proposer;
                proposer.DoStartNPCMode(DialogBox.SpriteSwitchMode.Normal);
                DialogBox.Instance.DisplayLoading(
                    "Generating " + proposer.GetFinalName()
                    + "'s marriage proposal to "
                    + recipient.GetFinalName() + ".",
                    accessedFromInsideDialogBox: true);
                _ = ReviewPromotionAsync(
                    pair, first, second, currentRunId, dialogContext);
                return true;
            }
            catch (Exception exception)
            {
                promotionQueries.Remove(pair.Id);
                promotionQueryInProgress = false;
                activeMarriageProposalDialog = null;
                Log.LogWarning(
                    "Could not start the pending marriage proposal for "
                    + $"{pair.firstNpc} / {pair.secondNpc}: {exception}");
                return false;
            }
        }
        return false;
    }

    internal bool TryShowPendingMarriageProposalResponse()
    {
        MarriageProposalDialogContext? dialogContext =
            activeMarriageProposalDialog;
        if (dialogContext == null)
        {
            return false;
        }
        if (!IsProposalDialogActive(dialogContext))
        {
            activeMarriageProposalDialog = null;
            return false;
        }
        if (!dialogContext.AwaitingResponse)
            return false;

        DisplayMarriageProposalResponse(dialogContext);
        return true;
    }

    private static bool IsProposalDialogActive(
        MarriageProposalDialogContext dialogContext) =>
        NeuralNPC.multiDialogParticipants != null
        && ReferenceEquals(
            NeuralNPC.multiDialogParticipants,
            dialogContext.Participants)
        && dialogContext.Participants.Contains(dialogContext.Proposer)
        && dialogContext.Participants.Contains(dialogContext.Recipient)
        && DialogBox.Instance != null
        && DialogBox.Instance.isOpen;

    private void ResumeNativeMultiDialog(
        MarriageProposalDialogContext dialogContext)
    {
        if (dialogContext.PresentationStarted
            || !IsProposalDialogActive(dialogContext))
        {
            if (ReferenceEquals(
                    activeMarriageProposalDialog, dialogContext))
            {
                activeMarriageProposalDialog = null;
            }
            return;
        }

        attemptedMarriageProposalSessions[dialogContext.Pair.Id] =
            dialogContext.Participants;
        activeMarriageProposalDialog = null;
        try
        {
            bypassPendingMarriageProposal = true;
            GenerateMultiDialogMethod.Invoke(
                null,
                new object?[]
                {
                    dialogContext.RequestedSpeaker,
                    dialogContext.FailureCallback,
                    dialogContext.SuccessCallback
                });
        }
        catch (Exception exception)
        {
            Log.LogWarning(
                "Could not resume Silverpine's normal multi-dialog turn "
                + "after a proposal generation failure: " + exception);
            dialogContext.FailureCallback?.Invoke();
        }
        finally
        {
            bypassPendingMarriageProposal = false;
        }
    }

    private async Task ReviewPromotionAsync(
        PairRelationshipData pair,
        NeuralNPC first,
        NeuralNPC second,
        string reviewRunId,
        MarriageProposalDialogContext? proposalDialog = null)
    {
        DynamicRelationshipStage reviewedStage = pair.stage;
        int reviewRevision = pair.RuntimeRevision;
        DynamicRelationshipStage proposed = pair.stage switch
        {
            DynamicRelationshipStage.Acquaintance =>
                DynamicRelationshipStage.Friend,
            DynamicRelationshipStage.Friend =>
                DynamicRelationshipStage.Lover,
            _ => DynamicRelationshipStage.Married
        };
        bool approved = proposed != DynamicRelationshipStage.Married
            && !requirePromotionQueries;
        bool promotionWasQueried = proposed == DynamicRelationshipStage.Married
            || requirePromotionQueries;
        MarriageProposalExchange? marriageProposal = null;
        NeuralNPC? rejectedPursuer = null;
        NeuralNPC? rejectingPartner = null;
        int rejectionCountAfterReview = 0;
        bool viabilityReviewCompleted = false;
        bool couldEverAccept = true;

        try
        {
            if (proposed == DynamicRelationshipStage.Married)
            {
                if (InferenceServerSetupHandler.Instance == null)
                {
                    promotionQueries.Remove(pair.Id);
                    return;
                }
                marriageProposal = await GenerateMarriageProposalAsync(
                    pair, first, second);
                approved = marriageProposal.Accepted;
                if (!approved)
                {
                    rejectedPursuer = marriageProposal.Proposer;
                    rejectingPartner = marriageProposal.Recipient;
                }
            }
            else if (requirePromotionQueries)
            {
                if (InferenceServerSetupHandler.Instance == null)
                {
                    promotionQueries.Remove(pair.Id);
                    return;
                }

                NeuralNPC reviewer = first;
                string firstName = first.GetFinalName();
                string secondName = second.GetFinalName();
                string question = proposed switch
                {
                    DynamicRelationshipStage.Friend =>
                        $"Based on their actual shared history, do {firstName} "
                        + $"and {secondName} now mutually regard one another as "
                        + "friends rather than mere acquaintances? Answer no if "
                        + "their history does not provide enough evidence.",
                    DynamicRelationshipStage.Lover =>
                        GetLoverPromotionQuestion(
                            pair, first, second,
                            out rejectedPursuer,
                            out rejectingPartner,
                            out reviewer),
                    _ => ""
                };

                Log.LogInfo(
                    $"Running one {pair.stage} -> {proposed} promotion "
                    + $"review for {firstName} and {secondName}.");
                approved = await reviewer.AskYesNoCotQuestion(question, -1);
            }

            if (!approved
                && (proposed == DynamicRelationshipStage.Lover
                    || proposed == DynamicRelationshipStage.Married)
                && rejectedPursuer != null
                && rejectingPartner != null)
            {
                rejectionCountAfterReview = (int)Math.Min(
                    int.MaxValue,
                    (long)GetRomanticRejectionCount(
                        pair, proposed, rejectedPursuer) + 1L);
                if (rejectionCountAfterReview
                    >= romanticRejectionsBeforeLockReview)
                {
                    RefreshSaveIdentity();
                    if (!pluginEnabled
                        || !string.Equals(
                            currentRunId, reviewRunId,
                            StringComparison.Ordinal)
                        || currentSave == null
                        || pair.suspended
                        || !currentSave.pairs.Contains(pair)
                        || pair.RuntimeRevision != reviewRevision
                        || pair.stage != reviewedStage
                        || !IsPromotionDue(pair))
                    {
                        Log.LogInfo(
                            "Skipped a stale permanent romantic viability "
                            + $"review for {pair.firstNpc} / "
                            + $"{pair.secondNpc}.");
                        return;
                    }
                    if (!IsPairCompatibleWithManagedState(
                            first, second, pair,
                            tolerateUnappliedBaseState: false))
                    {
                        pair.suspended = true;
                        SaveDatabase();
                        Log.LogWarning(
                            "Skipped the permanent romantic viability review "
                            + "because another source changed the pair; "
                            + "dynamic progression was suspended.");
                        return;
                    }
                    try
                    {
                        couldEverAccept =
                            await AskCouldEverAcceptRomanceAsync(
                                proposed,
                                rejectedPursuer,
                                rejectingPartner,
                                rejectionCountAfterReview);
                        viabilityReviewCompleted = true;
                    }
                    catch (Exception exception)
                    {
                        Log.LogWarning(
                            "The permanent romantic viability review for "
                            + $"{rejectedPursuer.GetFinalName()} / "
                            + $"{rejectingPartner.GetFinalName()} failed. "
                            + "The rejection will still be recorded and the "
                            + "viability review will retry after a later "
                            + $"rejection: {exception.Message}");
                    }
                }
            }

            RefreshSaveIdentity();
            if (!pluginEnabled
                || !string.Equals(
                    currentRunId, reviewRunId, StringComparison.Ordinal)
                || currentSave == null
                || pair.suspended
                || !currentSave.pairs.Contains(pair))
            {
                Log.LogInfo(
                    $"Discarded stale promotion result for {pair.firstNpc} / "
                    + $"{pair.secondNpc} because the active save changed.");
                return;
            }

            if (pair.RuntimeRevision != reviewRevision
                || pair.stage != reviewedStage
                || !IsPromotionDue(pair))
            {
                Log.LogInfo(
                    $"Discarded obsolete {reviewedStage} -> {proposed} "
                    + $"promotion result for {pair.firstNpc} / "
                    + $"{pair.secondNpc}; the relationship changed during "
                    + "review.");
                return;
            }

            if (proposalDialog != null
                && !IsProposalDialogActive(proposalDialog))
            {
                Log.LogInfo(
                    $"Deferred {first.GetFinalName()} / "
                    + $"{second.GetFinalName()}'s pending proposal because "
                    + "the qualifying multi-dialog ended while it was "
                    + "being generated.");
                return;
            }

            if (!IsPairCompatibleWithManagedState(
                    first, second, pair,
                    tolerateUnappliedBaseState: false))
            {
                pair.suspended = true;
                SaveDatabase();
                Log.LogWarning(
                    $"Promotion review for {first.GetFinalName()} and "
                    + $"{second.GetFinalName()} completed after another "
                    + "source changed the pair. Dynamic progression was "
                    + "suspended without applying or recording the result.");
                return;
            }

            if (approved)
            {
                ResetRomanticRejectionCounts(pair, proposed);
                if (marriageProposal != null)
                {
                    pair.marriageProposalPending = false;
                    RecordAndPresentMarriageProposal(
                        marriageProposal, proposalDialog);
                }
                if (promotionWasQueried)
                    ApplyApprovedPromotionScoreBuffer(pair, proposed);
                pair.stage = proposed;
                if (proposed == DynamicRelationshipStage.Married)
                {
                    SetWorldKnowledge(
                        pair, first, second,
                        WorldRelationshipKnowledgeState.Married);
                }
                pair.nextReviewScore = proposed switch
                {
                    DynamicRelationshipStage.Friend =>
                        Math.Max(loverReviewScore, pair.score + 1),
                    DynamicRelationshipStage.Lover =>
                        Math.Max(marriageReviewScore, pair.score + 1),
                    _ => MaximumScore + 1
                };
                AddRelationshipMemory(
                    first, second,
                    proposed switch
                    {
                        DynamicRelationshipStage.Friend =>
                            "now mutually regard each other as friends",
                        DynamicRelationshipStage.Lover =>
                            "have acknowledged that they are mutually romantic lovers",
                        _ => "have mutually chosen to marry one another"
                    });
                ApplyManagedPairUnchecked(first, second, pair);
                Log.LogInfo(
                    $"Promoted {first.GetFinalName()} and "
                    + $"{second.GetFinalName()} to {proposed}.");
            }
            else
            {
                if (marriageProposal != null)
                {
                    pair.marriageProposalPending = false;
                    RecordAndPresentMarriageProposal(
                        marriageProposal, proposalDialog);
                }
                if (rejectedPursuer != null && rejectingPartner != null)
                {
                    SetRomanticRejectionCount(
                        pair, proposed, rejectedPursuer,
                        viabilityReviewCompleted && couldEverAccept
                            ? 0
                            : rejectionCountAfterReview);
                }
                if (viabilityReviewCompleted && !couldEverAccept
                    && rejectedPursuer != null
                    && rejectingPartner != null)
                {
                    LockPromotionCeiling(
                        pair, rejectedPursuer, rejectingPartner, proposed);
                    Log.LogInfo(
                        $"Locked {first.GetFinalName()} / "
                        + $"{second.GetFinalName()}'s promotion ceiling at "
                        + $"{pair.promotionCeilingStage}; no further "
                        + "promotion queries will run for this pair.");
                }
                else
                {
                    pair.nextReviewScore = Math.Min(
                        MaximumScore,
                        pair.score + Math.Max(
                            1, deniedReviewAdditionalScore));
                    Log.LogInfo(
                        $"Promotion denied for {first.GetFinalName()} and "
                        + $"{second.GetFinalName()}; next review at score "
                        + $"{pair.nextReviewScore}.");
                }
            }
            SaveDatabase();
        }
        catch (Exception exception)
        {
            promotionQueries.Remove(pair.Id);
            Log.LogWarning(
                $"Promotion review for {pair.firstNpc} / {pair.secondNpc} "
                + $"failed and will be retried later: {exception}");
        }
        finally
        {
            RefreshSaveIdentity();
            promotionQueries.Remove(pair.Id);
            promotionQueryInProgress = false;
            if (string.Equals(
                    currentRunId, reviewRunId, StringComparison.Ordinal))
            {
                conversationReadyPromotions.Remove(pair.Id);
                TryQueueNextPromotion();
            }
            else if (pluginEnabled)
            {
                TryQueueNextPromotion();
            }
            if (proposalDialog != null
                && !proposalDialog.PresentationStarted)
            {
                ResumeNativeMultiDialog(proposalDialog);
            }
        }
    }

    private void ApplyApprovedPromotionScoreBuffer(
        PairRelationshipData pair,
        DynamicRelationshipStage proposed)
    {
        int threshold = proposed switch
        {
            DynamicRelationshipStage.Friend => friendReviewScore,
            DynamicRelationshipStage.Lover => loverReviewScore,
            _ => marriageReviewScore
        };
        long bufferedThreshold = (long)threshold
            + approvedPromotionScoreBonus;
        int requiredScore = (int)Math.Max(
            MinimumScore,
            Math.Min(MaximumScore, bufferedThreshold));
        pair.score = Math.Max(pair.score, requiredScore);
    }

    private static string GetLoverPromotionQuestion(
        PairRelationshipData pair,
        NeuralNPC first,
        NeuralNPC second,
        out NeuralNPC? pursuer,
        out NeuralNPC? potentialPartner,
        out NeuralNPC reviewer)
    {
        SelectRomanticDirection(
            pair, first, second,
            out NeuralNPC selectedPursuer,
            out NeuralNPC selectedPartner);
        pursuer = selectedPursuer;
        potentialPartner = selectedPartner;
        reviewer = selectedPartner;
        return $"Based on their actual shared history, would "
            + $"{selectedPartner.GetFinalName()} now willingly enter a "
            + $"mutual romantic lover relationship with "
            + $"{selectedPursuer.GetFinalName()}, rather than remaining "
            + "friends? Require clear reciprocal romantic evidence and "
            + "answer no if it is ambiguous.";
    }

    private static void SelectRomanticDirection(
        PairRelationshipData pair,
        NeuralNPC first,
        NeuralNPC second,
        out NeuralNPC pursuer,
        out NeuralNPC potentialPartner)
    {
        pursuer = pair.interactionCount % 2 == 0 ? first : second;
        potentialPartner = ReferenceEquals(pursuer, first) ? second : first;
    }

    private static int GetRomanticRejectionCount(
        PairRelationshipData pair,
        DynamicRelationshipStage proposed,
        NeuralNPC pursuer)
    {
        bool firstPursuer = string.Equals(
            GetNpcStorageKey(pursuer), pair.firstNpc,
            StringComparison.Ordinal);
        return proposed == DynamicRelationshipStage.Lover
            ? firstPursuer
                ? pair.firstLoverRejections
                : pair.secondLoverRejections
            : firstPursuer
                ? pair.firstMarriageRejections
                : pair.secondMarriageRejections;
    }

    private static void SetRomanticRejectionCount(
        PairRelationshipData pair,
        DynamicRelationshipStage proposed,
        NeuralNPC pursuer,
        int value)
    {
        value = Math.Max(0, value);
        bool firstPursuer = string.Equals(
            GetNpcStorageKey(pursuer), pair.firstNpc,
            StringComparison.Ordinal);
        if (proposed == DynamicRelationshipStage.Lover)
        {
            if (firstPursuer)
                pair.firstLoverRejections = value;
            else
                pair.secondLoverRejections = value;
        }
        else if (firstPursuer)
        {
            pair.firstMarriageRejections = value;
        }
        else
        {
            pair.secondMarriageRejections = value;
        }
    }

    private static void ResetRomanticRejectionCounts(
        PairRelationshipData pair,
        DynamicRelationshipStage proposed)
    {
        if (proposed == DynamicRelationshipStage.Lover)
        {
            pair.firstLoverRejections = 0;
            pair.secondLoverRejections = 0;
        }
        else if (proposed == DynamicRelationshipStage.Married)
        {
            pair.firstMarriageRejections = 0;
            pair.secondMarriageRejections = 0;
        }
    }

    private static async Task<bool> AskCouldEverAcceptRomanceAsync(
        DynamicRelationshipStage proposed,
        NeuralNPC pursuer,
        NeuralNPC potentialPartner,
        int rejectionCount)
    {
        string pursuerName = pursuer.GetFinalName();
        string partnerName = potentialPartner.GetFinalName();
        string relationship = proposed == DynamicRelationshipStage.Married
            ? "marry"
            : "enter a mutual romantic lover relationship with";
        string context = proposed == DynamicRelationshipStage.Married
            ? "They are currently lovers, and a marriage proposal from this "
                + $"person has now been rejected {rejectionCount} times."
            : "They are currently friends, and a romantic relationship with "
                + $"this person has now been rejected {rejectionCount} times.";
        return await potentialPartner.AskYesNoCotQuestion(
            $"{context} Based on {partnerName}'s personality, boundaries, "
            + $"feelings, and actual shared history with {pursuerName}, could "
            + $"{partnerName} ever plausibly and willingly {relationship} "
            + $"{pursuerName}? 'Ever' means at any plausible point in their "
            + "future, not necessarily now. Answer no only if this relationship "
            + "is something they would not willingly accept even after further "
            + "positive development. Answer yes if a future acceptance remains "
            + "plausible.",
            -2);
    }

    private void LockPromotionCeiling(
        PairRelationshipData pair,
        NeuralNPC rejectedPursuer,
        NeuralNPC rejectingPartner,
        DynamicRelationshipStage proposed)
    {
        pair.promotionCeilingLocked = true;
        pair.promotionCeilingStage = pair.stage;
        string desiredRelationship = proposed
            == DynamicRelationshipStage.Married
                ? "marriage"
                : "a lover relationship";
        pair.promotionLockReason = rejectingPartner.GetFinalName()
            + " determined that " + desiredRelationship + " with "
            + rejectedPursuer.GetFinalName()
            + " would never be willingly accepted";
        pair.lastReason = "promotion ceiling locked: "
            + pair.promotionLockReason;
        pair.nextReviewScore = MaximumScore + 1;
        AddRelationshipMemory(
            rejectedPursuer,
            rejectingPartner,
            "will not progress beyond " + pair.promotionCeilingStage
            + " because " + pair.promotionLockReason);
    }

    private async Task<MarriageProposalExchange> GenerateMarriageProposalAsync(
        PairRelationshipData pair,
        NeuralNPC first,
        NeuralNPC second)
    {
        SelectRomanticDirection(
            pair, first, second,
            out NeuralNPC proposer,
            out NeuralNPC recipient);
        string proposerName = proposer.GetFinalName();
        string recipientName = recipient.GetFinalName();

        Log.LogInfo(
            $"Generating an in-character marriage proposal from "
            + $"{proposerName} to {recipientName}.");
        string proposal = (await proposer.AskQuestion(
            $"{proposerName} has decided to ask {recipientName} to marry "
            + $"{proposer.Pronouns.Him()}. Write {proposerName}'s sincere, "
            + "in-character marriage proposal addressed directly to "
            + $"{recipientName}. Base it on their shared history and current "
            + "relationship. Output only the words and actions of the proposal, "
            + "with no analysis or narration outside the character's action.",
            deterministic: false,
            takes: -1)).Trim();
        if (string.IsNullOrWhiteSpace(proposal))
            throw new InvalidOperationException(
                $"The model returned an empty proposal for {proposerName}.");

        string response = (await recipient.AskQuestion(
            $"{proposerName} has just proposed marriage to {recipientName} "
            + $"with these exact words and actions:\n\n{proposal}\n\nWrite "
            + $"{recipientName}'s immediate, honest, in-character response. "
            + "The response may accept or reject the proposal according to "
            + "their personality, shared history, and feelings. Output only "
            + "the recipient's words and actions, with no analysis or "
            + "out-of-character explanation.",
            deterministic: false,
            takes: -1)).Trim();
        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidOperationException(
                $"The model returned an empty proposal response for "
                + recipientName + ".");

        bool accepted = await recipient.AskYesNoCotQuestion(
            $"{proposerName} proposed marriage to {recipientName}. The exact "
            + $"proposal was:\n\n{proposal}\n\n{recipientName}'s exact "
            + $"response was:\n\n{response}\n\nDoes {recipientName}'s "
            + "response clearly and willingly accept the marriage proposal? "
            + "Answer no for a rejection, hesitation without acceptance, "
            + "ambiguity, postponement, or a conditional answer that does not "
            + "yet commit to marriage.",
            -2);

        return new MarriageProposalExchange
        {
            Proposer = proposer,
            Recipient = recipient,
            Proposal = proposal,
            Response = response,
            Accepted = accepted
        };
    }

    private void RecordAndPresentMarriageProposal(
        MarriageProposalExchange exchange,
        MarriageProposalDialogContext? dialogContext)
    {
        string proposerName = exchange.Proposer.GetFinalName();
        string recipientName = exchange.Recipient.GetFinalName();
        string outcome = exchange.Accepted ? "accepted" : "did not accept";
        string proposalTurn = GetNamedNpcTurn(
            proposerName, exchange.Proposal);
        string responseTurn = GetNamedNpcTurn(
            recipientName, exchange.Response);
        string transcript = proposalTurn + "\n\n" + responseTurn;
        string memory = proposerName + " proposed marriage to "
            + recipientName + ". " + recipientName + " " + outcome
            + " the proposal. Their exchange was: " + transcript;

        exchange.Proposer.AddThingThatHappened(memory);
        exchange.Recipient.AddThingThatHappened(memory);
        if (dialogContext == null
            || !IsProposalDialogActive(dialogContext))
        {
            Log.LogWarning(
                $"Recorded {proposerName}'s proposal to {recipientName}, "
                + "but its multi-dialog was no longer active before the "
                + "spoken turns could be displayed.");
            return;
        }

        foreach (NeuralNPC participant in dialogContext.Participants
                     .Where(participant => participant != null)
                     .Distinct())
        {
            participant.dialogElements.AddToDialog(
                SpeakerType.NPC, proposalTurn);
        }

        dialogContext.ProposalTurn = proposalTurn;
        dialogContext.ResponseTurn = responseTurn;
        dialogContext.PresentationStarted = true;
        dialogContext.AwaitingResponse = true;
        activeMarriageProposalDialog = dialogContext;

        NeuralNPC.currentActiveDialogNeuralNPC = exchange.Proposer;
        exchange.Proposer.DoStartNPCMode(DialogBox.SpriteSwitchMode.Normal);
        DialogBox.Instance.DisplayText(
            FormatNpcTurnForDialog(proposalTurn, proposerName),
            _ => DisplayMarriageProposalResponse(dialogContext),
            new List<UpperButtonOption>(),
            () =>
            {
                if (dialogContext.AwaitingResponse
                    && IsProposalDialogActive(dialogContext))
                {
                    DialogBox.Instance.StartContinueOnlyMode();
                }
            });
        try
        {
            dialogContext.SuccessCallback?.Invoke();
            foreach (NeuralNPC participant in dialogContext.Participants)
            {
                participant
                    .GetComponent<IDialogMechanic_GenerateDialogSuccess>()?
                    .OnAfterGenerateDialogSuccessCallback();
            }
        }
        catch (Exception exception)
        {
            Log.LogWarning(
                "A native multi-dialog success hook failed after the "
                + "marriage proposal turn was displayed: " + exception);
        }
    }

    private void DisplayMarriageProposalResponse(
        MarriageProposalDialogContext dialogContext)
    {
        if (!dialogContext.AwaitingResponse
            || !IsProposalDialogActive(dialogContext))
        {
            return;
        }

        dialogContext.AwaitingResponse = false;
        foreach (NeuralNPC participant in dialogContext.Participants
                     .Where(participant => participant != null)
                     .Distinct())
        {
            participant.dialogElements.AddToDialog(
                SpeakerType.NPC, dialogContext.ResponseTurn);
        }
        NeuralNPC.currentActiveDialogNeuralNPC = dialogContext.Recipient;
        dialogContext.Recipient.DoStartNPCMode(
            DialogBox.SpriteSwitchMode.Normal);
        DialogBox.Instance.DisplayText(
            FormatNpcTurnForDialog(
                dialogContext.ResponseTurn,
                dialogContext.Recipient.GetFinalName()),
            text => NeuralNPC.OnMultiInputCallback(null, text),
            new List<UpperButtonOption>(),
            () =>
            {
                if (!IsProposalDialogActive(dialogContext))
                    return;
                if (ReferenceEquals(
                        activeMarriageProposalDialog, dialogContext))
                {
                    activeMarriageProposalDialog = null;
                }
                DialogBox.Instance.StopContinueOnlyMode();
            });
    }

    private static string GetNamedNpcTurn(
        string speakerName,
        string generatedText)
    {
        string body = generatedText.Trim();
        string plainPrefix = speakerName + ":";
        if (body.StartsWith(
                plainPrefix, StringComparison.OrdinalIgnoreCase))
        {
            body = body.Substring(plainPrefix.Length).TrimStart();
        }
        return plainPrefix + " " + body;
    }

    private static string FormatNpcTurnForDialog(
        string namedTurn,
        string speakerName)
    {
        string plainPrefix = speakerName + ":";
        string richPrefix = "<font=\"CinzelDecorative-Regular SDF\">"
            + plainPrefix + "</font>";
        string formatted = namedTurn.StartsWith(
                plainPrefix, StringComparison.OrdinalIgnoreCase)
            ? richPrefix + namedTurn.Substring(plainPrefix.Length)
            : richPrefix + " " + namedTurn;
        return NeuralNPC.MakeGoldIcon(formatted);
    }

    private static void AddRelationshipMemory(
        NeuralNPC first,
        NeuralNPC second,
        string relationshipChange)
    {
        string memory = first.GetFinalName() + " and "
            + second.GetFinalName() + " " + relationshipChange + ".";
        first.AddThingThatHappened(memory);
        second.AddThingThatHappened(memory);
    }

    private sealed class MarriageProposalExchange
    {
        internal NeuralNPC Proposer = null!;
        internal NeuralNPC Recipient = null!;
        internal string Proposal = "";
        internal string Response = "";
        internal bool Accepted;
    }

    private sealed class MarriageProposalDialogContext
    {
        internal PairRelationshipData Pair = null!;
        internal string RunId = "";
        internal NeuralNPC RequestedSpeaker = null!;
        internal NeuralNPC Proposer = null!;
        internal NeuralNPC Recipient = null!;
        internal List<NeuralNPC> Participants = null!;
        internal Action? FailureCallback;
        internal Action? SuccessCallback;
        internal string ProposalTurn = "";
        internal string ResponseTurn = "";
        internal bool PresentationStarted;
        internal bool AwaitingResponse;
    }

    private sealed class ConversationAssessmentRequest
    {
        internal NeuralNPC First = null!;
        internal NeuralNPC Second = null!;
        internal PairRelationshipData Pair = null!;
        internal string RunId = "";
        internal int Revision;
        internal List<NeuralNPC.DialogElement> DialogContext = new();
    }

    private static void SetWorldKnowledge(
        PairRelationshipData pair,
        NeuralNPC first,
        NeuralNPC second,
        WorldRelationshipKnowledgeState state)
    {
        string previousEntry = GetWorldKnowledgeEntry(pair);
        pair.firstDisplayName = first.GetFinalName();
        pair.secondDisplayName = second.GetFinalName();
        pair.firstMarriageTitle = GetMarriageTitle(first);
        pair.secondMarriageTitle = GetMarriageTitle(second);
        pair.worldKnowledgeState = state;
        pair.worldKnowledgeTurn = WorldInfoManager.Instance != null
            ? WorldInfoManager.Instance.TotalTurnCount
            : pair.worldKnowledgeTurn;
        string updatedEntry = GetWorldKnowledgeEntry(pair);
        if (!string.Equals(
                previousEntry, updatedEntry, StringComparison.Ordinal))
        {
            pair.worldKnowledgeNouns.Clear();
            pair.worldKnowledgeVerbs.Clear();
            pair.worldKnowledgeAdjectives.Clear();
            pair.worldKnowledgeKeywords.Clear();
            pair.worldKnowledgeWordCloudDirty = true;
        }
    }

    private static string GetWorldKnowledgeEntryName(
        PairRelationshipData pair)
    {
        string prefix = pair.worldKnowledgeState switch
        {
            WorldRelationshipKnowledgeState.Married => "Marriage",
            WorldRelationshipKnowledgeState.Divorced => "Divorce",
            _ => ""
        };
        return prefix == "" ? "" : prefix + ": "
            + pair.firstDisplayName + " and " + pair.secondDisplayName;
    }

    private static string GetWorldKnowledgeEntry(PairRelationshipData pair)
    {
        string entryName = GetWorldKnowledgeEntryName(pair);
        if (entryName == "" || pair.firstDisplayName == ""
            || pair.secondDisplayName == "")
        {
            return "";
        }

        string lore = pair.worldKnowledgeState switch
        {
            WorldRelationshipKnowledgeState.Married =>
                pair.firstDisplayName + " and " + pair.secondDisplayName
                + " are married. " + pair.firstDisplayName + " is the "
                + pair.firstMarriageTitle + " of " + pair.secondDisplayName
                + ", and " + pair.secondDisplayName + " is the "
                + pair.secondMarriageTitle + " of " + pair.firstDisplayName
                + ".",
            WorldRelationshipKnowledgeState.Divorced =>
                pair.firstDisplayName + " and " + pair.secondDisplayName
                + " are divorced and are no longer married to each other.",
            _ => ""
        };
        return lore == "" ? "" : entryName + ": " + lore;
    }

    private bool ApplyPair(
        PairRelationshipData pair,
        bool tolerateUnappliedBaseState)
    {
        if (!pluginEnabled
            || pair.suspended
            || !TryResolvePair(pair, out NeuralNPC first, out NeuralNPC second))
        {
            return false;
        }
        if (!IsPairCompatibleWithManagedState(
                first, second, pair, tolerateUnappliedBaseState))
        {
            pair.suspended = true;
            Log.LogWarning(
                $"Did not apply {pair.firstNpc} / {pair.secondNpc}; "
                + "an authored or externally managed relationship takes priority.");
            SaveDatabase();
            return false;
        }

        NPCRelationship nativeTier = GetNativeTier(pair.stage);
        SetNativeRelationship(
            first, second, nativeTier,
            GetManagedRelationshipLabel(first, second, pair));
        SetNativeRelationship(
            second, first, nativeTier,
            GetManagedRelationshipLabel(second, first, pair));
        return true;
    }

    private static void ApplyManagedPairUnchecked(
        NeuralNPC first,
        NeuralNPC second,
        PairRelationshipData pair)
    {
        NPCRelationship nativeTier = GetNativeTier(pair.stage);
        SetNativeRelationship(
            first, second, nativeTier,
            GetManagedRelationshipLabel(first, second, pair));
        SetNativeRelationship(
            second, first, nativeTier,
            GetManagedRelationshipLabel(second, first, pair));
    }

    private static NPCRelationship GetNativeTier(
        DynamicRelationshipStage stage) => stage switch
    {
        DynamicRelationshipStage.Enemies => NPCRelationship.Stranger,
        DynamicRelationshipStage.Stranger => NPCRelationship.Stranger,
        DynamicRelationshipStage.Acquaintance => NPCRelationship.Acquaintance,
        _ => NPCRelationship.Friend
    };

    private static void SetNativeRelationship(
        NeuralNPC owner,
        NeuralNPC target,
        NPCRelationship nativeTier,
        string label)
    {
        owner.relationships[target.npcName] = nativeTier;
        if (label == "")
            owner.customRelationshipNames.Remove(target.npcName);
        else
            owner.customRelationshipNames[target.npcName] = label;
    }

    private static string GetManagedRelationshipLabel(
        NeuralNPC owner,
        NeuralNPC target,
        PairRelationshipData pair)
    {
        if (pair.stage == DynamicRelationshipStage.Stranger
            || pair.stage == DynamicRelationshipStage.Acquaintance
            || pair.stage == DynamicRelationshipStage.Friend)
        {
            string originalLabel = GetOriginalCustomName(owner, pair);
            if (originalLabel != "")
                return originalLabel;
        }
        return GetStageLabel(target, pair.stage);
    }

    private static string GetOriginalCustomName(
        NeuralNPC owner,
        PairRelationshipData pair) =>
        string.Equals(
            GetNpcStorageKey(owner), pair.firstNpc,
            StringComparison.Ordinal)
                ? pair.originalFirstCustomName
                : pair.originalSecondCustomName;

    private static string GetStageLabel(
        NeuralNPC describedNpc,
        DynamicRelationshipStage stage)
    {
        return stage switch
        {
            DynamicRelationshipStage.Enemies => "enemy",
            DynamicRelationshipStage.Friend => "friend",
            DynamicRelationshipStage.Lover => "lover",
            DynamicRelationshipStage.Married =>
                GetMarriageTitle(describedNpc),
            _ => ""
        };
    }

    private static string GetMarriageTitle(NeuralNPC npc)
    {
        Sex resolvedSex = npc.sex;
        try
        {
            if (FinalSexMethod != null)
                resolvedSex = (Sex)FinalSexMethod.Invoke(npc, null);
        }
        catch (Exception exception)
        {
            Log.LogWarning(
                $"Could not resolve sex for {npc.GetFinalName()}; using "
                + $"the serialized value: {exception.Message}");
        }
        return resolvedSex switch
        {
            Sex.Female => "wife",
            Sex.Male => "husband",
            _ => "spouse"
        };
    }

    private static bool IsUnassignedAcquaintancePair(
        NeuralNPC first,
        NeuralNPC second)
    {
        return TryGetNativeRelationship(first, second, out NPCRelationship a)
            && TryGetNativeRelationship(second, first, out NPCRelationship b)
            && a == NPCRelationship.Acquaintance
            && b == NPCRelationship.Acquaintance
            && GetCustomName(first, second) == ""
            && GetCustomName(second, first) == "";
    }

    private static bool TryGetUnlabelledSymmetricTier(
        NeuralNPC first,
        NeuralNPC second,
        out NPCRelationship tier)
    {
        tier = NPCRelationship.Acquaintance;
        if (!TryGetNativeRelationship(first, second, out NPCRelationship a)
            || !TryGetNativeRelationship(second, first, out NPCRelationship b)
            || a != b
            || (a != NPCRelationship.Stranger
                && a != NPCRelationship.Acquaintance)
            || GetCustomName(first, second) != ""
            || GetCustomName(second, first) != "")
        {
            return false;
        }
        tier = a;
        return true;
    }

    private static bool IsPairCompatibleWithManagedState(
        NeuralNPC first,
        NeuralNPC second,
        PairRelationshipData pair,
        bool tolerateUnappliedBaseState)
    {
        bool hasFirst = TryGetNativeRelationship(
            first, second, out NPCRelationship a);
        bool hasSecond = TryGetNativeRelationship(
            second, first, out NPCRelationship b);
        string firstName = GetCustomName(first, second);
        string secondName = GetCustomName(second, first);
        if (!hasFirst || !hasSecond)
        {
            return tolerateUnappliedBaseState
                && pair.originalRelationshipMissing
                && !hasFirst
                && !hasSecond
                && string.Equals(
                    firstName, GetOriginalCustomName(first, pair),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    secondName, GetOriginalCustomName(second, pair),
                    StringComparison.OrdinalIgnoreCase);
        }

        NPCRelationship expected = GetNativeTier(pair.stage);
        string expectedFirstName = GetManagedRelationshipLabel(
            first, second, pair);
        string expectedSecondName = GetManagedRelationshipLabel(
            second, first, pair);

        bool nativeMatches = a == expected && b == expected;
        bool namesMatch = string.Equals(firstName, expectedFirstName,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(secondName, expectedSecondName,
                StringComparison.OrdinalIgnoreCase);
        if (nativeMatches && namesMatch)
            return true;

        NPCRelationship originalTier = GetOriginalNativeTier(pair);
        return tolerateUnappliedBaseState
            && a == originalTier
            && b == originalTier
            && string.Equals(
                firstName, GetOriginalCustomName(first, pair),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                secondName, GetOriginalCustomName(second, pair),
                StringComparison.OrdinalIgnoreCase);
    }

    private static NPCRelationship GetOriginalNativeTier(
        PairRelationshipData pair)
    {
        NPCRelationship tier = (NPCRelationship)pair.originalNativeTier;
        return tier == NPCRelationship.Stranger
                || tier == NPCRelationship.Acquaintance
                || tier == NPCRelationship.Friend
            ? tier
            : NPCRelationship.Acquaintance;
    }

    private static void RestoreOriginalNativeRelationship(
        NeuralNPC first,
        NeuralNPC second,
        PairRelationshipData pair)
    {
        if (pair.originalRelationshipMissing)
        {
            first.relationships.Remove(second.npcName);
            second.relationships.Remove(first.npcName);
        }
        else
        {
            NPCRelationship originalTier = GetOriginalNativeTier(pair);
            SetNativeRelationship(
                first, second, originalTier,
                GetOriginalCustomName(first, pair));
            SetNativeRelationship(
                second, first, originalTier,
                GetOriginalCustomName(second, pair));
            return;
        }
        SetCustomRelationshipLabel(
            first, second, GetOriginalCustomName(first, pair));
        SetCustomRelationshipLabel(
            second, first, GetOriginalCustomName(second, pair));
    }

    private static void SetCustomRelationshipLabel(
        NeuralNPC owner,
        NeuralNPC target,
        string label)
    {
        if (label == "")
            owner.customRelationshipNames.Remove(target.npcName);
        else
            owner.customRelationshipNames[target.npcName] = label;
    }

    private static bool TryGetNativeRelationship(
        NeuralNPC owner,
        NeuralNPC target,
        out NPCRelationship relationship)
    {
        relationship = NPCRelationship.Acquaintance;
        return owner.relationships != null
            && owner.relationships.TryGetValue(target.npcName, out relationship);
    }

    private static string GetCustomName(NeuralNPC owner, NeuralNPC target)
    {
        if (owner.customRelationshipNames != null
            && owner.customRelationshipNames.TryGetValue(
                target.npcName, out string value))
        {
            return value?.Trim() ?? "";
        }
        return "";
    }

    private PairRelationshipData? FindPair(
        NeuralNPC first,
        NeuralNPC second)
    {
        if (currentSave == null)
            return null;
        (string firstKey, string secondKey) = CreateOrderedKeys(first, second);
        return currentSave.pairs.FirstOrDefault(pair =>
            string.Equals(pair.firstNpc, firstKey, StringComparison.Ordinal)
            && string.Equals(pair.secondNpc, secondKey,
                StringComparison.Ordinal));
    }

    private static (string, string) CreateOrderedKeys(
        NeuralNPC first,
        NeuralNPC second)
    {
        string firstKey = GetNpcStorageKey(first);
        string secondKey = GetNpcStorageKey(second);
        return string.CompareOrdinal(firstKey, secondKey) <= 0
            ? (firstKey, secondKey)
            : (secondKey, firstKey);
    }

    private static string CreatePairId(string firstKey, string secondKey) =>
        string.CompareOrdinal(firstKey, secondKey) <= 0
            ? firstKey + "\n" + secondKey
            : secondKey + "\n" + firstKey;

    private static string GetNpcStorageKey(NeuralNPC npc) =>
        npc.IsCustomNPC()
            ? "custom:" + npc.GetFinalName()
            : npc.npcName.ToString();

    private static bool TryResolvePair(
        PairRelationshipData pair,
        out NeuralNPC first,
        out NeuralNPC second)
    {
        first = null!;
        second = null!;
        if (NeuralNPC.neuralNPCs == null)
            return false;

        foreach (NeuralNPC npc in NeuralNPC.neuralNPCs.Values)
        {
            if (npc == null)
                continue;
            string key = GetNpcStorageKey(npc);
            if (key == pair.firstNpc)
                first = npc;
            else if (key == pair.secondNpc)
                second = npc;
        }
        return first != null && second != null;
    }

    private void LoadDatabase()
    {
        database = new RelationshipDatabase();
        legacyRunIds.Clear();
        legacyArchiveBlocked = false;
        LoadPerRunFiles();
        MigrateLegacyDatabase();
        database.Normalize(MinimumScore, MaximumScore);
    }

    private void LoadPerRunFiles()
    {
        if (!Directory.Exists(SavesDirectory))
            return;

        foreach (string path in Directory.GetFiles(
                     SavesDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!TryLoadRunFile(path, out SaveRelationshipData save))
                continue;
            if (string.IsNullOrWhiteSpace(save.runId))
            {
                Logger.LogWarning(
                    "Ignored dynamic relationship run file without a run ID: "
                    + path);
                continue;
            }
            if (database.saves.Any(existing => string.Equals(
                    existing.runId, save.runId, StringComparison.Ordinal)))
            {
                Logger.LogWarning(
                    "Ignored duplicate dynamic relationship run file for "
                    + save.runId + ": " + path);
                continue;
            }
            database.saves.Add(save);
        }
    }

    private bool TryLoadRunFile(
        string path,
        out SaveRelationshipData save)
    {
        save = null!;
        try
        {
            save = JsonConvert.DeserializeObject<SaveRelationshipData>(
                    File.ReadAllText(path))
                ?? new SaveRelationshipData();
            save.Normalize(MinimumScore, MaximumScore);
            save.RuntimeSourcePath = path;
            return true;
        }
        catch (Exception exception)
        {
            Logger.LogError(
                "Could not load dynamic relationship run file " + path
                + ": " + exception);
            string backupPath = path + ".bak";
            try
            {
                if (!File.Exists(backupPath))
                    return false;
                save =
                    JsonConvert.DeserializeObject<SaveRelationshipData>(
                        File.ReadAllText(backupPath))
                    ?? new SaveRelationshipData();
                save.Normalize(MinimumScore, MaximumScore);
                save.RuntimeSourcePath = path;
                Logger.LogWarning(
                    "Recovered dynamic relationship run data from "
                    + backupPath + ".");
                return true;
            }
            catch (Exception backupException)
            {
                Logger.LogError(
                    "Could not recover dynamic relationship run backup "
                    + backupPath + ": "
                    + backupException);
                return false;
            }
        }
    }

    private void MigrateLegacyDatabase()
    {
        string legacyBackupPath = LegacyDatabasePath + ".bak";
        if (!File.Exists(LegacyDatabasePath)
            && !File.Exists(legacyBackupPath))
        {
            return;
        }

        RelationshipDatabase? legacy = null;
        try
        {
            if (File.Exists(LegacyDatabasePath))
            {
                legacy = JsonConvert.DeserializeObject<RelationshipDatabase>(
                    File.ReadAllText(LegacyDatabasePath));
            }
        }
        catch (Exception exception)
        {
            Logger.LogError(
                "Could not read the legacy combined relationship database: "
                + exception);
        }

        if (legacy == null && File.Exists(legacyBackupPath))
        {
            try
            {
                legacy = JsonConvert.DeserializeObject<RelationshipDatabase>(
                    File.ReadAllText(legacyBackupPath));
                Logger.LogWarning(
                    "Migrating legacy relationship data from its backup file.");
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    "Could not read the legacy relationship backup: "
                    + exception);
            }
        }
        if (legacy == null)
            return;

        legacy.Normalize(MinimumScore, MaximumScore);
        foreach (SaveRelationshipData save in legacy.saves)
        {
            if (string.IsNullOrWhiteSpace(save.runId))
            {
                legacyArchiveBlocked = true;
                Logger.LogWarning(
                    "Could not migrate a legacy relationship entry without "
                    + "a run ID.");
                continue;
            }
            legacyRunIds.Add(save.runId);
            if (database.saves.Any(existing => string.Equals(
                    existing.runId, save.runId, StringComparison.Ordinal)))
            {
                continue;
            }
            database.saves.Add(save);
            // Keep legacy-only data in memory until that run is loaded and
            // Silverpine performs a real game save. This preserves the same
            // save-only disk-write rule used by ordinary relationship data.
        }
    }

    private static void ArchiveLegacyDatabase()
    {
        try
        {
            List<string> archivedPaths = new();
            if (File.Exists(LegacyDatabasePath))
            {
                string primaryArchive = GetUniqueLegacyArchivePath(
                    LegacyDatabasePath + ".migrated.bak");
                File.Move(LegacyDatabasePath, primaryArchive);
                archivedPaths.Add(primaryArchive);
            }
            string legacyBackup = LegacyDatabasePath + ".bak";
            if (File.Exists(legacyBackup))
            {
                string backupArchive = GetUniqueLegacyArchivePath(
                    LegacyDatabasePath + ".backup.migrated.bak");
                File.Move(legacyBackup, backupArchive);
                archivedPaths.Add(backupArchive);
            }
            if (archivedPaths.Count > 0)
            {
                Log.LogInfo(
                    "Migrated the combined relationship database to per-run "
                    + "files. Legacy inputs were retained at: "
                    + string.Join(", ", archivedPaths));
            }
        }
        catch (Exception exception)
        {
            Log.LogWarning(
                "Per-run relationship migration succeeded, but the legacy "
                + "database could not be archived: " + exception);
        }
    }

    private static string GetUniqueLegacyArchivePath(string preferredPath)
    {
        if (!File.Exists(preferredPath))
            return preferredPath;
        return preferredPath + "."
            + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
    }

    private void SaveDatabase()
    {
        if (currentSave == null
            || !database.saves.Any(save => string.Equals(
                save.runId, currentSave.runId, StringComparison.Ordinal)))
        {
            return;
        }
        dirtyRunIds.Add(currentSave.runId);
    }

    internal void FlushCurrentRunForGameSave()
    {
        RefreshSaveIdentity();
        if (currentSave != null
            && dirtyRunIds.Contains(currentSave.runId)
            && SaveRunData(currentSave))
        {
            dirtyRunIds.Remove(currentSave.runId);
        }
        TryArchiveFullyMigratedLegacyDatabase();
    }

    private void TryArchiveFullyMigratedLegacyDatabase()
    {
        if (legacyArchiveBlocked || legacyRunIds.Count == 0)
            return;
        bool allPersisted = legacyRunIds.All(runId =>
        {
            SaveRelationshipData? save = database.saves.FirstOrDefault(
                candidate => string.Equals(
                    candidate.runId, runId, StringComparison.Ordinal));
            return save != null
                && !string.IsNullOrWhiteSpace(save.RuntimeSourcePath)
                && File.Exists(save.RuntimeSourcePath);
        });
        if (!allPersisted)
            return;
        ArchiveLegacyDatabase();
        legacyRunIds.Clear();
    }

    private bool SaveRunData(SaveRelationshipData save)
    {
        if (string.IsNullOrWhiteSpace(save.runId))
            return false;
        save.Normalize(MinimumScore, MaximumScore);
        string path = GetRunDataPath(save.runId, save.playerName);
        string temporaryPath = path + ".tmp";
        string previousPath = save.RuntimeSourcePath;
        try
        {
            Directory.CreateDirectory(SavesDirectory);
            File.WriteAllText(
                temporaryPath,
                JsonConvert.SerializeObject(save, Formatting.Indented));
            if (File.Exists(path))
            {
                File.Replace(
                    temporaryPath,
                    path,
                    path + ".bak",
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
            save.RuntimeSourcePath = path;
            ArchivePreviousRunPath(previousPath, path);
            return true;
        }
        catch (Exception exception)
        {
            Logger.LogError(
                "Could not save dynamic relationship run " + save.runId
                + ": " + exception);
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static void ArchivePreviousRunPath(
        string previousPath,
        string currentPath)
    {
        if (string.IsNullOrWhiteSpace(previousPath)
            || string.Equals(
                Path.GetFullPath(previousPath),
                Path.GetFullPath(currentPath),
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(previousPath))
        {
            return;
        }
        try
        {
            File.Move(
                previousPath,
                GetUniqueLegacyArchivePath(
                    previousPath + ".renamed.bak"));
        }
        catch (Exception exception)
        {
            Log.LogWarning(
                "Saved the recognizable dynamic relationship filename, but "
                + "could not archive its previous filename: " + exception);
        }
    }

    private static string GetRunDataPath(
        string runId,
        string playerName)
    {
        string safePlayerName = SanitizeFilePart(
            playerName, "UnknownPlayer", 40);
        string safeRunId = SanitizeFilePart(runId, "run", 48);

        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(runId));
        string shortHash = string.Concat(
            hash.Take(8).Select(value => value.ToString("x2")));
        return Path.Combine(
            SavesDirectory,
            safePlayerName + "--" + safeRunId + "-" + shortHash + ".json");
    }

    private static string SanitizeFilePart(
        string value,
        string fallback,
        int maximumLength)
    {
        HashSet<char> invalidCharacters = new(
            Path.GetInvalidFileNameChars());
        string safeValue = new((value ?? "")
            .Select(character => invalidCharacters.Contains(character)
                    || char.IsControl(character)
                ? '_'
                : character)
            .ToArray());
        safeValue = safeValue.Trim(' ', '.', '_');
        if (safeValue.Length > maximumLength)
            safeValue = safeValue.Substring(0, maximumLength);
        return safeValue == "" ? fallback : safeValue;
    }

    internal static bool MarkRumorDelivered(
        RoutineArgument_PassOnRumor argument)
    {
        if (DeliveredRumors.TryGetValue(argument, out _))
            return false;
        DeliveredRumors.Add(argument, new RumorDeliveryMarker());
        return true;
    }

    private sealed class RumorDeliveryMarker
    {
    }
}

public enum DynamicRelationshipStage
{
    Enemies = -2,
    Stranger = -1,
    Acquaintance = 0,
    Friend = 1,
    Lover = 2,
    Married = 3
}

public enum WorldRelationshipKnowledgeState
{
    None = 0,
    Married = 1,
    Divorced = 2
}

[Serializable]
public sealed class DefaultRelationshipSettings
{
    public int schemaVersion = 1;
    public List<DefaultNpcRelationshipSettings> npcs = new();

    public void Normalize()
    {
        schemaVersion = 1;
        npcs ??= new List<DefaultNpcRelationshipSettings>();
        foreach (DefaultNpcRelationshipSettings npc in npcs)
            npc.Normalize();
        npcs = npcs
            .Where(npc => !string.IsNullOrWhiteSpace(npc.id))
            .GroupBy(npc => npc.id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(npc => npc.id, StringComparer.Ordinal)
            .ToList();
    }
}

[Serializable]
public sealed class DefaultNpcRelationshipSettings
{
    public string id = "";
    public string displayName = "";
    public bool dynamicRelationshipsEnabled = true;
    public List<string> disabledWith = new();

    public void Normalize()
    {
        id = (id ?? "").Trim();
        displayName = (displayName ?? "").Trim();
        disabledWith ??= new List<string>();
        disabledWith = disabledWith
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

[Serializable]
public sealed class RelationshipDatabase
{
    public int schemaVersion = 4;
    public List<SaveRelationshipData> saves = new();

    public void Normalize(int minimumScore, int maximumScore)
    {
        schemaVersion = 4;
        saves ??= new List<SaveRelationshipData>();
        foreach (SaveRelationshipData save in saves)
            save.Normalize(minimumScore, maximumScore);
    }
}

[Serializable]
public sealed class SaveRelationshipData
{
    public int schemaVersion = 4;
    public string runId = "";
    public string playerName = "";
    public bool enabled = true;
    public bool defaultPolicyApplied;
    public bool startingRelationshipsInitialized;
    public List<string> initializedNpcIds = new();
    public List<string> disabledDynamicPairs = new();
    public List<PairRelationshipData> pairs = new();

    [JsonIgnore]
    internal string RuntimeSourcePath { get; set; } = "";

    public void Normalize(int minimumScore, int maximumScore)
    {
        schemaVersion = 4;
        runId ??= "";
        playerName = (playerName ?? "").Trim();
        initializedNpcIds ??= new List<string>();
        initializedNpcIds = initializedNpcIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        disabledDynamicPairs ??= new List<string>();
        disabledDynamicPairs = disabledDynamicPairs
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        pairs ??= new List<PairRelationshipData>();
        foreach (PairRelationshipData pair in pairs)
            pair.Normalize(minimumScore, maximumScore);
    }
}

[Serializable]
public sealed class PairRelationshipData
{
    public string firstNpc = "";
    public string secondNpc = "";
    public int originalNativeTier = (int)NPCRelationship.Acquaintance;
    public bool originalRelationshipMissing;
    public string originalFirstCustomName = "";
    public string originalSecondCustomName = "";
    public int score;
    public DynamicRelationshipStage stage;
    public int nextReviewScore = 30;
    public int interactionCount;
    public int lastInteractionTurn = -1;
    public int lastSharedConversationDay = -1;
    public int lastDailyDecreaseDay = -1;
    public string lastReason = "";
    public bool suspended;
    public bool tierLocked;
    public bool promotionCeilingLocked;
    public DynamicRelationshipStage promotionCeilingStage;
    public string promotionLockReason = "";
    public int firstLoverRejections;
    public int secondLoverRejections;
    public int firstMarriageRejections;
    public int secondMarriageRejections;
    public bool marriageProposalPending;
    public WorldRelationshipKnowledgeState worldKnowledgeState;
    public int worldKnowledgeTurn = -1;
    public string firstDisplayName = "";
    public string secondDisplayName = "";
    public string firstMarriageTitle = "spouse";
    public string secondMarriageTitle = "spouse";
    public bool worldKnowledgeWordCloudDirty;
    public List<string> worldKnowledgeNouns = new();
    public List<string> worldKnowledgeVerbs = new();
    public List<string> worldKnowledgeAdjectives = new();
    public List<string> worldKnowledgeKeywords = new();

    [JsonIgnore]
    public string Id => firstNpc + "\n" + secondNpc;

    [JsonIgnore]
    internal int RuntimeRevision { get; set; }

    public void Normalize(int minimumScore, int maximumScore)
    {
        firstNpc ??= "";
        secondNpc ??= "";
        originalFirstCustomName ??= "";
        originalSecondCustomName ??= "";
        lastReason ??= "";
        promotionLockReason ??= "";
        firstDisplayName ??= "";
        secondDisplayName ??= "";
        firstMarriageTitle ??= "spouse";
        secondMarriageTitle ??= "spouse";
        worldKnowledgeNouns ??= new List<string>();
        worldKnowledgeVerbs ??= new List<string>();
        worldKnowledgeAdjectives ??= new List<string>();
        worldKnowledgeKeywords ??= new List<string>();
        if (worldKnowledgeState != WorldRelationshipKnowledgeState.None
            && !HasCachedWorldKnowledgeCloud())
        {
            worldKnowledgeWordCloudDirty = true;
        }
        score = Math.Max(
            minimumScore, Math.Min(maximumScore, score));
        firstLoverRejections = Math.Max(0, firstLoverRejections);
        secondLoverRejections = Math.Max(0, secondLoverRejections);
        firstMarriageRejections = Math.Max(0, firstMarriageRejections);
        secondMarriageRejections = Math.Max(0, secondMarriageRejections);
        if (stage != DynamicRelationshipStage.Lover)
            marriageProposalPending = false;
        int normalizedReview = stage == DynamicRelationshipStage.Enemies
            || stage == DynamicRelationshipStage.Stranger
            ? Math.Max(minimumScore, nextReviewScore)
            : Math.Max(1, nextReviewScore);
        nextReviewScore = Math.Min(maximumScore + 1, normalizedReview);
    }

    private bool HasCachedWorldKnowledgeCloud() =>
        worldKnowledgeNouns.Count > 0
        || worldKnowledgeVerbs.Count > 0
        || worldKnowledgeAdjectives.Count > 0
        || worldKnowledgeKeywords.Count > 0;
}

internal sealed class RumorPatchState
{
    internal NeuralNPC Source = null!;
    internal NeuralNPC Target = null!;
}

[HarmonyPatch(typeof(CustomNPCManager), "SpawnInNPCs")]
internal static class CustomNpcRosterReadyPatch
{
    [HarmonyPostfix]
    private static void ReconcileAfterCustomNpcSetup()
    {
        if (!ReferenceEquals(Plugin.Instance, null))
            Plugin.Instance.NotifyNpcRosterChanged();
    }
}

[HarmonyPatch(typeof(NeuralNPC), "GenerateMultiDialog")]
internal static class PendingMarriageProposalMultiDialogPatch
{
    [HarmonyPrefix]
    private static bool ReplaceNextNpcTurnWithProposal(
        NeuralNPC speaker,
        Action? failureCallback,
        Action? successCallback)
    {
        return ReferenceEquals(Plugin.Instance, null)
            || !Plugin.Instance.TryOverrideWithPendingMarriageProposal(
                speaker, failureCallback, successCallback);
    }
}

[HarmonyPatch(typeof(DialogBox), nameof(DialogBox.StopContinueOnlyMode))]
internal static class PendingMarriageProposalInterruptPatch
{
    [HarmonyPrefix]
    private static bool KeepRecipientResponseInSequence()
    {
        return ReferenceEquals(Plugin.Instance, null)
            || !Plugin.Instance.TryShowPendingMarriageProposalResponse();
    }
}

[HarmonyPatch(typeof(NeuralNPC), nameof(NeuralNPC.EndOfDialogCleanup))]
internal static class MultiNpcConversationPatch
{
    [HarmonyPostfix]
    private static void RecordCompletedConversation(
        NeuralNPC __instance,
        bool playerInputAnything,
        List<NeuralNPC> participants)
    {
        if (!playerInputAnything || participants == null
            || participants.Count < 2
            || !ReferenceEquals(__instance, participants[0])
            || ReferenceEquals(Plugin.Instance, null))
        {
            return;
        }
        Plugin.Instance.RecordSharedConversation(participants);
    }
}

[HarmonyPatch(typeof(RoutineArgument_PassOnRumor), "Check")]
internal static class RumorDeliveryPatch
{
    [HarmonyPrefix]
    private static void CaptureDelivery(
        RoutineArgument_PassOnRumor __instance,
        NPCName ___targetNPCName,
        ref RumorPatchState? __state)
    {
        NPCRoutine? routine = __instance.routine;
        NeuralNPC? source = routine?.executor?.neuralNPC;
        if (source == null
            || !NeuralNPC.neuralNPCs.TryGetValue(
                ___targetNPCName, out NeuralNPC target)
            || target == null
            || target.sleeping
            || !routine!.executor.IsCloseForMeeting(target)
            || IFollowingPlayerNPCRoutineArgument
                .DoesNeuralNPCHaveIFollowingPlayerNPCRoutineArgument(target)
            || !source.transform.GetVector2IntPosition().IsAdjacentOrOnTop(
                target.transform.GetVector2IntPosition())
            || target.GetComponent<NPCRoutineExecutor>()
                .currentRoutine
                .GetRoutineArgument<RoutineArgument_EndOverrideIf_Overridden>()
                != null)
        {
            return;
        }

        __state = new RumorPatchState { Source = source, Target = target };
    }

    [HarmonyPostfix]
    private static void RecordDelivery(
        RoutineArgument_PassOnRumor __instance,
        RumorPatchState? __state)
    {
        if (__state == null || ReferenceEquals(Plugin.Instance, null)
            || !Plugin.MarkRumorDelivered(__instance))
        {
            return;
        }
        Plugin.Instance.RecordRumorDelivery(__state.Source, __state.Target);
    }
}

[HarmonyPatch(typeof(Player), "Update")]
internal static class ManagedUpdatePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void ContinueManagedPluginWork()
    {
        if (!ReferenceEquals(Plugin.Instance, null))
            Plugin.Instance.RunManagedUpdate();
    }
}

[HarmonyPatch(typeof(SerializationManager), nameof(SerializationManager.Save))]
internal static class GameSaveRelationshipPersistencePatch
{
    [HarmonyPostfix]
    private static void FlushRelationshipDataAfterGameSave()
    {
        if (!ReferenceEquals(Plugin.Instance, null))
            Plugin.Instance.FlushCurrentRunForGameSave();
    }
}

[HarmonyPatch(typeof(Lorebook), nameof(Lorebook.GetConditionalLore))]
internal static class PublicWorldRelationshipKnowledgePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void AddPublicRelationshipKnowledge(
        NPCName npcName,
        string deepHaystack,
        string shallowHaystack,
        ref string __result)
    {
        if (!ReferenceEquals(Plugin.Instance, null))
        {
            Plugin.Instance.AppendPublicWorldRelationshipKnowledge(
                npcName, deepHaystack, shallowHaystack, ref __result);
        }
    }
}
