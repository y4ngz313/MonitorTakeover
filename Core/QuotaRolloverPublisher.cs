// QuotaRolloverPublisher.cs — #611 task 1.3; ownership corrected by #629.
//
// The one place a quota rollover becomes an event.
//
// Before this, three independent things hooked TimeOfDay.SetNewProfitQuota and each decided for
// itself what "the quota rolled over" meant and when it had happened: the ship-systems fuel
// refill (Priority.First), the quota milestone grants (Priority.Normal), and the quota-overhaul
// rewrite of the next target (Priority.Last). A fourth, the contract payout, mutated profitQuota
// from a different patch entirely. Nothing published, so every consumer re-derived the state by
// reading TimeOfDay itself — and a consumer that reads timesFulfilledQuota races the rollover it
// is reacting to, which is how the same milestone was evaluated twice.
//
// This publishes QuotaRolloverFinalized exactly once per rollover, host-only, after everything
// above has run, and every consumer reads the payload instead of the game.
//
// ── Assembly ownership: MONITOR TAKEOVER (#629) ─────────────────────────────────────────────
//
// This file ships with Y4NGZMonitorTakeover, is patched under
// ModuleHarmonyIds.MonitorTakeover from MonitorTakeoverPlugin.Initialize, and logs on
// ModuleLog.Takeover. Its namespace stays frozen even though its assembly owner changed.
//
// The publisher is part of progression, not presentation. MonitorTakeoverPlugin patches it next
// to the unconditional QuotaProgressionRegistry initialization, outside TakeoverBootstrap. A
// disabled presentation or missing bundle therefore cannot silence milestone rewards. Because
// the source exists in only this assembly and only this plugin patches it, a full-stack install
// still has exactly one publisher; because it travels with the registry, Core + Monitor Takeover
// now has one too.
//
// Contracted can still stage its objective delta through Y4NGZCore before the rollover. This
// publisher consumes that neutral slot when Contracted is present and naturally publishes a zero
// delta when it is absent; no feature-assembly reference is introduced in either direction.

using HarmonyLib;
using Unity.Netcode;
using Y4NGZCore.Diagnostics;
using Y4NGZCore.Modules.Quota;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    /// <summary>
    /// Captures the before-values of a rollover and publishes the finalized payload.
    /// </summary>
    [HarmonyPatch(typeof(TimeOfDay), nameof(TimeOfDay.SetNewProfitQuota))]
    internal static class QuotaRolloverPublisher
    {
        /// <summary>
        /// The publish postfix's Harmony priority: strictly below every other Y4NGZ postfix on
        /// this method, so it is provably last.
        ///
        /// <para><b>It is a bare integer, and it is deliberately not <c>Priority.Last - 1</c>.</b>
        /// <c>Priority.Last</c> is <c>0</c>, so <c>Priority.Last - 1</c> is <c>-1</c> — and
        /// <c>-1</c> is HarmonyX's <em>unset</em> sentinel: it is what
        /// <c>new HarmonyMethod().priority</c> defaults to, and <c>Patch</c>'s constructor maps
        /// it back to <see cref="Priority.Normal"/> (400). Writing <c>Priority.Last - 1</c> here
        /// therefore does the opposite of what it reads like: it puts this postfix at Normal,
        /// tied with the takeover hook and <em>ahead</em> of the quota-overhaul rewrite at 0, so
        /// <see cref="QuotaRolloverFinalized.ProfitQuotaAfter"/> would carry vanilla's next
        /// target rather than the one the crew actually gets. Verified against HarmonyX 2.10.2,
        /// the version this plugin builds with.</para>
        ///
        /// <para><b>Why not <c>[HarmonyAfter]</c>.</b> It keys on the <em>owner</em> ID, and
        /// every patch in this plugin shares one <c>Harmony</c> instance created in
        /// <c>Plugin.Awake</c> and handed to each bootstrap. Naming that ID would order this
        /// patch after itself and discriminate nothing.</para>
        ///
        /// <para>Any value strictly below <c>0</c> other than <c>-1</c> would do; <c>-10</c>
        /// leaves room for a future hook that must sit between the rewrite and this publish.</para>
        /// </summary>
        private const int PublishPriority = -10;

        private static int _profitQuotaBefore;
        private static int _quotaFulfilledBefore;

        /// <summary>
        /// The before-half of the payload.
        ///
        /// <para><see cref="Priority.First"/> so it reads the values the game's own rollover
        /// check was made against, ahead of every Y4NGZ prefix that rewrites them.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(TimeOfDay __instance)
        {
            if (__instance == null)
                return;

            _profitQuotaBefore = __instance.profitQuota;
            _quotaFulfilledBefore = __instance.quotaFulfilled;
        }

        /// <summary>
        /// Publishes the finalized rollover.
        ///
        /// <para><b>Why the priority is below <see cref="Priority.Last"/>.</b> Harmony sorts
        /// postfixes by descending priority and only falls back to registration order between
        /// equal ones. Three Y4NGZ postfixes already sit on this method at First, Normal and
        /// Last, and the Last one — <c>QuotaOverhaulSetNewProfitQuotaPatch.Postfix</c> — rewrites
        /// the new quota target. Declaring <c>Priority.Last</c> here too would leave which of
        /// the two runs first up to module init order, and on the losing order
        /// <see cref="QuotaRolloverFinalized.ProfitQuotaAfter"/> would carry vanilla's value
        /// rather than the one the crew actually sees. An explicit rank below Last is the only
        /// thing that makes "last of all of them" a property of the code rather than of the
        /// bootstrap sequence — see <see cref="PublishPriority"/> for why it is written as a
        /// bare integer and not as arithmetic on <see cref="Priority.Last"/>.</para>
        ///
        /// <para><b>Host only.</b> <c>SetNewProfitQuota</c> is server-side, but on the host it
        /// executes twice per rollover — once as the <c>SyncNewProfitQuotaClientRpc</c> send
        /// stub's caller and once through the loopback of the same body (the condition
        /// <see cref="QuotaRolloverDedup"/> was written for). The publisher's own single-publish
        /// latch absorbs that: the second call carries the same
        /// <see cref="QuotaRolloverFinalized.CompletedQuotaCount"/> and is refused.</para>
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(PublishPriority)]
        private static void Postfix(TimeOfDay __instance)
        {
            if (__instance == null)
                return;

            NetworkManager network = NetworkManager.Singleton;
            if (network != null && network.IsListening && !network.IsServer)
                return;

            // Consumed, not peeked: a delta belongs to the rollover it was staged for, and a
            // second rollover with no payout of its own must publish a zero rather than inherit
            // this one's.
            QuotaRolloverOutcomeStaging.Consume(out int outcomeDelta, out bool countedTowardQuota);

            var payload = new QuotaRolloverFinalized(
                completedQuotaCount: __instance.timesFulfilledQuota,
                profitQuotaBefore: _profitQuotaBefore,
                quotaFulfilledBefore: _quotaFulfilledBefore,
                profitQuotaAfter: __instance.profitQuota,
                daysUntilDeadlineAfter: __instance.daysUntilDeadline,
                objectiveOutcomeDelta: outcomeDelta,
                outcomeCountedTowardQuota: countedTowardQuota);

            if (QuotaRolloverNotifications.PublishQuotaRolloverFinalized(payload))
            {
                ModuleLog.Takeover.LogInfo(
                    $"[QuotaRollover] Quota rollover {payload.CompletedQuotaCount} published " +
                    $"(quota {payload.ProfitQuotaBefore} -> {payload.ProfitQuotaAfter}, " +
                    $"objective delta {payload.ObjectiveOutcomeDelta}, " +
                    $"counted={payload.OutcomeCountedTowardQuota}).");
                return;
            }

            // Not an error: this is the host's second execution of the same method body, and
            // suppressing it here is what stops milestone evaluation and the payload broadcast
            // from running twice. A staged delta re-staged after the first publish would be
            // lost by the Consume above, so re-stage what we took.
            if (outcomeDelta != 0 || countedTowardQuota)
                QuotaRolloverOutcomeStaging.ReportObjectiveOutcome(outcomeDelta, countedTowardQuota);

            ModuleLog.Takeover.LogInfo(
                "[QuotaRollover] Duplicate quota rollover publish suppressed for completed " +
                $"quota {payload.CompletedQuotaCount}.");
        }
    }
}
