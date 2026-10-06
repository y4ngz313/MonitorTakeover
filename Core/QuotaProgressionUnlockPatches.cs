using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
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
            if (TryGetLockedText(__instance, __result, out string lockedText))
                __result = BuildLockedNode(lockedText);
        }

        [HarmonyPatch(typeof(Terminal), "LoadNewNodeIfAffordable")]
        [HarmonyPrefix]
        static bool OnLoadNewNodeIfAffordable(Terminal __instance, TerminalNode node)
        {
            if (!TryGetLockedText(__instance, node, out string lockedText)) return true;
            __instance.useCreditsCooldown = false;
            __instance.LoadNewNode(BuildLockedNode(lockedText));
            return false;
        }

        /// <summary>
        /// #862: the host-authoritative half of the moon gate. The two terminal patches above
        /// stop a patched client before it ever sends the RPC; this stops everything else — an
        /// unpatched client, another terminal mod, a route change issued from code. Runs on
        /// the server only and refuses the route change outright; vanilla passes the debited
        /// credit total in the same RPC rather than debiting first, so nothing has to be
        /// refunded. Staying on the current moon is never refused, even if that moon is locked.
        /// </summary>
        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ChangeLevelServerRpc), new[] { typeof(int), typeof(int) })]
        [HarmonyPrefix]
        static bool OnChangeLevelServerRpc(StartOfRound __instance, int levelID)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer) return true;
            if (__instance != null && __instance.currentLevelID == levelID) return true;
            if (!TryGetLockedMoon(levelID, out string moonName, out int quota)) return true;

            Y4NGZCore.Diagnostics.ModuleLog.Takeover?.LogWarning(
                $"[QuotaProgression] Refused a route change to '{moonName}' (level {levelID}): "
                + (quota > 0 ? $"it unlocks at quota {quota}." : "it is quota-locked."));
            return false;
        }

        private static bool TryGetLockedText(Terminal terminal, TerminalNode node, out string lockedText)
        {
            lockedText = null;
            if (TryGetLockedItemName(terminal, node, out string itemName))
            {
                lockedText = $"\n\n{itemName} is not currently available.\nThe Company will provision it after a future quota.\n\n";
                return true;
            }

            // #862: a route node carries the destination's level index. -1 is "no route" and
            // -2 is vanilla's "moons" catalogue sentinel; neither names a moon.
            if (node == null || node.buyRerouteToMoon < 0) return false;
            if (!TryGetLockedMoon(node.buyRerouteToMoon, out string moonName, out int quota)) return false;
            lockedText = quota > 0
                ? $"\n\nRouting to {moonName} is not currently authorized.\nThe Company will open this route after quota {quota}.\n\n"
                : $"\n\nRouting to {moonName} is not currently authorized.\nThe Company will open this route after a future quota.\n\n";
            return true;
        }

        private static bool TryGetLockedMoon(int routeIndex, out string moonName, out int quota)
        {
            moonName = null;
            quota = 0;
            SelectableLevel[] levels = StartOfRound.Instance?.levels;
            if (levels == null || routeIndex < 0 || routeIndex >= levels.Length || levels[routeIndex] == null) return false;
            string planetName = levels[routeIndex].PlanetName;
            if (QuotaProgressionRegistry.IsMoonUnlocked(planetName)) return false;
            moonName = QuotaProgressionRegistry.MoonKey(planetName);
            QuotaProgressionRegistry.TryGetMoonUnlockQuota(planetName, out quota);
            return true;
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

        private static TerminalNode BuildLockedNode(string displayText)
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
            _lockedNode.displayText = displayText;
            return _lockedNode;
        }
    }
}
