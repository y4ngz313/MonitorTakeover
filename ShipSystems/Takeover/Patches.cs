// Patches.cs — LGUMonitorTakeover
//
// Rollover detection (two legs, one handler, one dedup):
//   1a. QuotaRolloverFinalized subscriber → the host-authoritative leg. NOT a Harmony patch
//       since #612 task 1.4: the takeover used to postfix TimeOfDay.SetNewProfitQuota at
//       Priority.Normal, which made two independent host detectors on the one method that
//       QuotaRolloverPublisher already publishes from.
//   1b. TimeOfDay.SyncNewProfitQuotaClientRpc postfix → every peer. Deliberately still a
//       patch: the Core event is host-only and not networked, so this is the only thing
//       giving a client the takeover flag at all.
//
// Harmony patches:
//   2. StartOfRound.SetShipReadyToLand → trigger takeover if a flag is set
//   3. GameNetworkManager.Disconnect, StartOfRound.ShipLeave, ShipLeaveAutomatically
//      → emergency safety restore so HUD/lights are never permanently broken

using Y4NGZCore.Modules.Quota;
using HarmonyLib;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    internal static class Patches
    {
        // ── Leg 1a: Quota completion — host-authoritative, via the Core event ──
        //
        // #612 task 1.4 item (a): this used to be a second Harmony postfix on
        // TimeOfDay.SetNewProfitQuota at Priority.Normal, sitting on the same method as
        // QuotaRolloverPublisher's postfix at -10. Two independent detectors on one method,
        // one of which exists precisely to be *the* host-authoritative rollover signal, is a
        // duplicate the split must not carry forward. #629 moved the publisher into this
        // assembly, but this handler remains a subscriber rather than becoming another patch:
        // one plugin still owns exactly one detector on the vanilla method.
        //
        // The host leg is now a subscriber to that one event. What this buys and what it costs:
        //
        //   * Position. The event publishes below Priority.Last, i.e. after the quota-overhaul
        //     rewrite in MoonContractPatches. The old postfix ran at Normal, before it. Nothing
        //     downstream of here reads the quota target — the flags are consumed much later at
        //     SetShipReadyToLand — so the move is a strict improvement in what the arming
        //     decision can see, and changes nothing it did see.
        //
        //   * Gating. The publisher bails only on a listening non-server, so it runs on the
        //     server and on a process whose
        //     NetworkManager is absent or not listening. SetNewProfitQuota is itself
        //     server-only, so the set of machines this leg runs on is unchanged.
        //
        //   * Redundancy on the host. On any listening host the loopback of
        //     SyncNewProfitQuotaClientRpc (leg 1b) claims the dedup key while SetNewProfitQuota
        //     is still on the stack, so this leg was already a suppressed duplicate there
        //     before the convergence and still is.
        //
        //   * The one behavioural coupling introduced, stated correctly. The publisher latches
        //     on CompletedQuotaCount and refuses a count it has already published. If it ever
        //     suppresses, this leg does not run.
        //
        //     What covers that is leg 1b, and leg 1b covers every case that can actually occur:
        //     Lethal Company has no "playing but not networked" state — singleplayer is a
        //     listening host (StartHost), so the loopback ClientRpc fires there exactly as it
        //     does in a lobby. A genuinely non-listening process is the main menu or the
        //     off-engine harness, and neither rolls a quota over.
        //
        //     What does NOT cover it — and an earlier version of this comment claimed it did —
        //     is the latch reset. QuotaRolloverNotifications.ResetRunState() is called from one
        //     place, QuotaProgressionRegistry.cs:435, inside a Tick that hard-returns at
        //     QuotaProgressionRegistry.cs:413 when the NetworkManager is null, not listening,
        //     or has no CustomMessagingManager. The reset is therefore unreachable in precisely
        //     the non-listening state it was supposed to protect. Registering that tick
        //     unconditionally (Plugin.cs) is still right — it is what keeps a config-disabled
        //     module ticking as it did pre-split — but it buys nothing here.
        //
        // Leg 1b below is NOT converged and must not be: QuotaRolloverFinalized is host-only
        // and not networked, so it is the only thing that gives a non-host peer any of this
        // state. Deleting it would silently remove the takeover for every client in the lobby.
        internal static void OnQuotaRolloverFinalized(QuotaRolloverFinalized rollover)
        {
            HandleQuotaRollover("server");
        }

        private static bool _rolloverSubscribed;

        /// <summary>
        /// Subscribe the host leg to the single quota-rollover event. Idempotent, and called
        /// from <c>TakeoverBootstrap.Initialize</c> only when the takeover is actually enabled
        /// — matching the old patch, which was applied from the same place under the same
        /// condition.
        /// </summary>
        internal static void EnsureRolloverSubscription()
        {
            if (_rolloverSubscribed)
                return;

            _rolloverSubscribed = true;
            QuotaRolloverNotifications.QuotaRolloverFinalized += OnQuotaRolloverFinalized;
        }

        // ── Patch 1b: Quota sync — ALL clients ────────────────────────────────
        // SyncNewProfitQuotaClientRpc is a ClientRpc called by SetNewProfitQuota.
        // It arrives on every connected client BEFORE AllPlayersHaveRevivedClientRpc
        // (Unity Netcode preserves FIFO order for reliable RPCs). Setting the flag
        // here means all clients have it set by the time SetShipReadyToLand runs.
        // This provides full multiplayer support with no custom networking code.
        [HarmonyPatch(typeof(TimeOfDay), "SyncNewProfitQuotaClientRpc")]
        [HarmonyPostfix]
        static void OnSyncNewProfitQuotaClientRpc()
        {
            HandleQuotaRollover("client");
        }

        // ── Rollover side effects: exactly once per rollover per peer ──────────
        // Everything except the server-only registry call is routed through here; see
        // QuotaRolloverDedup for why one rollover fires two hooks on the host.
        private static readonly QuotaRolloverDedup RolloverDedup = new QuotaRolloverDedup();

        internal static void ResetQuotaRolloverGuard() => RolloverDedup.Reset();

        private static void HandleQuotaRollover(string source)
        {
            string key;
            try
            {
                key = QuotaRolloverDedup.BuildKey(
                    GameNetworkManager.Instance?.currentSaveFileName,
                    TimeOfDay.Instance != null ? TimeOfDay.Instance.timesFulfilledQuota : -1);
            }
            catch (System.Exception ex)
            {
                // Fail open: a rollover that cannot be keyed is processed rather than risking
                // a latched key that silences every later rollover.
                TakeoverBootstrap.Log.LogWarning($"[LGUMonitorTakeover] Quota rollover key failed: {ex.Message}");
                key = null;
            }

            if (!RolloverDedup.TryClaim(key))
            {
                TakeoverBootstrap.Log.LogInfo(
                    $"[LGUMonitorTakeover] Duplicate quota rollover hook ({source}) suppressed for '{key}'.");
                return;
            }

            QuotaTakeoverHandoff.QuotaJustCompleted = true;
            TakeoverBootstrap.Log.LogInfo($"[LGUMonitorTakeover] Quota fulfilled ({source}) — takeover queued.");
        }

        // ── Patch 2: Orbit return → launch takeover ───────────────────────────
        // SetShipReadyToLand is private but Harmony patches it by name fine.
        // It is called on ALL clients from AllPlayersHaveRevivedClientRpc's Execute
        // stage. Patch 1b ensures the flag is set on all clients before this fires,
        // so the takeover sequence runs for every player simultaneously.
        [HarmonyPatch(typeof(StartOfRound), "SetShipReadyToLand")]
        [HarmonyPostfix]
        static void OnSetShipReadyToLand(StartOfRound __instance)
        {
            if (TakeoverBootstrap.CfgDumpMonitorHierarchyOnOrbit?.Value == true)
            {
                MonitorDebugger.DumpAll();
            }

            bool debugForce = false;
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            debugForce = QuotaTakeoverDebugController.ConsumeQueuedSkipOrbitDelay();
#endif
            bool started = TryStartPendingTakeover(
                skipOrbitDelay: debugForce,
                debugForced: debugForce);
            if (debugForce && !started)
                QuotaUnlockAnnouncement.ClearDebugReplay();
        }

        internal static bool TryStartPendingTakeover(
            bool skipOrbitDelay = false,
            bool debugForced = false)
        {
            if (!QuotaTakeoverHandoff.QuotaJustCompleted)
                return false;
            QuotaTakeoverHandoff.QuotaJustCompleted = false;

            TakeoverManager.EnsureInstance();
            if (TakeoverManager.Instance == null)
                return false;

            TakeoverBootstrap.Log.LogInfo("[LGUMonitorTakeover] Starting takeover coroutine.");
            TakeoverManager.Instance.BeginTakeover(skipOrbitDelay);
            return true;
        }

        // ── Safety patches: restore state on disconnect / premature ship leave ─
        // These guarantee the HUD and lights are never permanently broken if the
        // takeover is interrupted mid-sequence (crash, host disconnect, fast-travel).

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        static void OnDisconnect()
        {
            ResetQuotaRolloverGuard();
            TakeoverManager.Instance?.ForceRestore("disconnect");
            // #610: ForceRestore releases the wall through the active flag, but only if a
            // takeover was running and only for leases whose handles this manager still holds.
            // A disconnect ends every presentation regardless, so sweep by owner too.
            TakeoverMonitorOwnership.ResetForNewSession("disconnect");
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            QuotaTakeoverDebugController.ResetPending();
            // #861: after ForceRestore released the takeover's captured clips. Aborts and
            // disposes every in-flight audio load and forgets this session's selection, whether
            // or not a takeover manager exists.
            TakeoverAudioOverrides.Reset("disconnect");
#endif
        }

        [HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
        [HarmonyPostfix]
        static void OnShipLeave()
        {
            TakeoverManager.Instance?.ForceRestore("ShipLeave");
        }

        [HarmonyPatch(typeof(StartOfRound), "ShipLeaveAutomatically")]
        [HarmonyPostfix]
        static void OnShipLeaveAutomatically()
        {
            TakeoverManager.Instance?.ForceRestore("ShipLeaveAutomatically");
        }

        [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.ResetSavedGameValues))]
        [HarmonyPostfix]
        static void OnResetSavedGameValues()
        {
            ResetQuotaRolloverGuard();
            // #610: clear the takeover's own state BEFORE sweeping the leases, the same order
            // OnDisconnect uses. The active flag is edge-triggered: sweeping first would drop
            // the wall lease while the flag stayed true, so the eventual `= false` would be a
            // no-op edge and nothing would ever re-claim. ForceRestore is a no-op when no
            // takeover is running.
            TakeoverManager.Instance?.ForceRestore("reset saved game values");
            TakeoverMonitorOwnership.ResetForNewSession("reset saved game values");
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            QuotaTakeoverDebugController.ResetPending();
#endif
        }
    }
}
