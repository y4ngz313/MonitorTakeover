using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Y4NGZCompany.Core
{
    internal static class QuotaProgressionSuitGate
    {
        // Suits stay active (they are spawned NetworkObjects with a NetworkVariable;
        // deactivating them breaks NGO sync). We hide them by disabling renderers,
        // interact triggers, and colliders, and parking them far below the ship so
        // they neither render nor occupy a visible rack slot.
        private static readonly List<GameObject> HiddenSuits = new List<GameObject>();
        private static readonly Vector3 ParkedLocalPosition = new Vector3(0f, -500f, 0f);
        private static int _lastAppliedQuota = -1;
        private static bool _repositioning;

        internal static string SuitUnlockableName(UnlockableSuit suit)
        {
            var unlockables = StartOfRound.Instance?.unlockablesList?.unlockables;
            // syncedSuitID starts at -1 (or out of range) before the NetworkVariable
            // syncs; resolving to no unlockable means we skip gating for that suit.
            int id = suit != null ? suit.syncedSuitID.Value : -1;
            if (unlockables == null || id < 0 || id >= unlockables.Count) return null;
            return unlockables[id]?.unlockableName;
        }

        private static void SetSuitHidden(UnlockableSuit suit, bool hidden)
        {
            foreach (Renderer renderer in suit.gameObject.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = !hidden;
            foreach (InteractTrigger trigger in suit.gameObject.GetComponentsInChildren<InteractTrigger>(true))
                trigger.enabled = !hidden;
            foreach (Collider collider in suit.gameObject.GetComponentsInChildren<Collider>(true))
                collider.enabled = !hidden;
            if (hidden) suit.transform.localPosition = ParkedLocalPosition;
        }

        internal static void ApplyToRack()
        {
            HiddenSuits.RemoveAll(item => item == null);
            foreach (UnlockableSuit suit in UnityEngine.Object.FindObjectsOfType<UnlockableSuit>())
            {
                string name = SuitUnlockableName(suit);
                if (string.IsNullOrWhiteSpace(name) || QuotaProgressionRegistry.IsSuitUnlocked(name)) continue;
                if (HiddenSuits.Contains(suit.gameObject))
                {
                    // PositionSuitsOnRack may have moved this suit back onto the
                    // rack; re-park it without toggling components again.
                    suit.transform.localPosition = ParkedLocalPosition;
                    continue;
                }
                SetSuitHidden(suit, true);
                HiddenSuits.Add(suit.gameObject);
            }
        }

        internal static void OnUnlockStateMaybeChanged(int completedQuota)
        {
            if (completedQuota == _lastAppliedQuota) return;
            _lastAppliedQuota = completedQuota;
            RefreshRack();
        }

        internal static void RefreshRack()
        {
            StartOfRound round = StartOfRound.Instance;
            if (round == null || _repositioning) return;
            foreach (GameObject suitObject in HiddenSuits)
            {
                UnlockableSuit suit = suitObject != null ? suitObject.GetComponent<UnlockableSuit>() : null;
                if (suit != null) SetSuitHidden(suit, false);
            }
            HiddenSuits.Clear();
            // Re-run rack layout so restored suits get proper slots; still-locked
            // suits are re-hidden by the PositionSuitsOnRack postfix -> ApplyToRack.
            _repositioning = true;
            try { round.PositionSuitsOnRack(); }
            // #612 task 1.4 (R2): ModuleLog.Takeover, not Plugin.Log. Plugin.Log is
            // `internal static` on Y4NGZCompany.Plugin — Contracted's plugin type — and this
            // file ships with Monitor Takeover, so the reference would not have survived the
            // assembly boundary. Bound to the same source in the monolith (Plugin.Awake calls
            // ModuleLog.BindAll before anything can log), so the line is unchanged today.
            catch (Exception ex) { Y4NGZCore.Diagnostics.ModuleLog.Takeover.LogWarning($"Quota progression suit rack refresh failed: {ex.Message}"); }
            finally { _repositioning = false; }
            ApplyToRack();
        }
    }

    internal static class QuotaProgressionUnlockPatches
    {
        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.PositionSuitsOnRack))]
        [HarmonyPostfix]
        static void OnPositionSuitsOnRack()
        {
            QuotaProgressionSuitGate.ApplyToRack();
        }

        [HarmonyPatch(typeof(Terminal), "ParsePlayerSentence")]
        [HarmonyPostfix]
        static void OnParsePlayerSentence(Terminal __instance, ref TerminalNode __result)
        {
            if (TryGetLockedItemName(__instance, __result, out string itemName))
                __result = BuildLockedNode(itemName);
        }

        [HarmonyPatch(typeof(Terminal), "LoadNewNodeIfAffordable")]
        [HarmonyPrefix]
        static bool OnLoadNewNodeIfAffordable(Terminal __instance, TerminalNode node)
        {
            if (!TryGetLockedItemName(__instance, node, out string itemName)) return true;
            __instance.useCreditsCooldown = false;
            __instance.LoadNewNode(BuildLockedNode(itemName));
            return false;
        }

        private static bool TryGetLockedItemName(Terminal terminal, TerminalNode node, out string itemName)
        {
            itemName = null;
            if (terminal?.buyableItemsList == null || node == null) return false;
            if (node.buyItemIndex < 0 || node.buyItemIndex >= terminal.buyableItemsList.Length) return false;
            Item item = terminal.buyableItemsList[node.buyItemIndex];
            if (item == null || QuotaProgressionRegistry.IsStoreItemUnlocked(item.itemName)) return false;
            itemName = item.itemName;
            return true;
        }

        private static TerminalNode _lockedNode;

        private static TerminalNode BuildLockedNode(string itemName)
        {
            if (_lockedNode == null)
            {
                _lockedNode = ScriptableObject.CreateInstance<TerminalNode>();
                _lockedNode.name = "Y4NGZQuotaLockedItem";
                _lockedNode.clearPreviousText = true;
                _lockedNode.buyItemIndex = -1;
                _lockedNode.shipUnlockableID = -1;
                _lockedNode.buyRerouteToMoon = -1;
                _lockedNode.creatureFileID = -1;
                _lockedNode.storyLogFileID = -1;
                _lockedNode.playSyncedClip = -1;
            }
            _lockedNode.displayText = $"\n\n{itemName} is not currently available.\nThe Company will provision it after a future quota.\n\n";
            return _lockedNode;
        }
    }
}
