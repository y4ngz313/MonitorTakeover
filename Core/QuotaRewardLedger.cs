using System;
using System.Collections.Generic;
using System.Linq;

namespace Y4NGZCompany.Core
{
    internal enum QuotaRewardOutcome
    {
        /// <summary>Nothing to do: the milestone grants none of this reward kind.</summary>
        NotConfigured,
        /// <summary>The ledger already records this reward as paid; nothing was granted again.</summary>
        AlreadyPaid,
        /// <summary>The reward landed and the ledger now records it as paid.</summary>
        Granted,
        /// <summary>Nothing was granted, so the reward stays owed and is retried later.</summary>
        Deferred
    }

    /// <summary>
    /// Ledger policy for one-shot quota milestone rewards. A reward id is only ever written as
    /// paid when the grant actually delivered something; a grant that delivered nothing (no
    /// provider, no fuel headroom, no terminal) is recorded in a distinct deferred state and
    /// retried on the next reward pass. Every entry is a plain ledger token, so an older build
    /// reading a ledger that contains deferred markers simply ignores them and still sees the
    /// reward as unpaid — which is the correct behaviour for a reward that was never granted.
    /// </summary>
    internal static class QuotaRewardLedger
    {
        /// <summary>Suffix that turns a reward id into its "owed but not granted" marker.</summary>
        internal const string DeferredSuffix = ":deferred";

        /// <summary>
        /// One-shot marker recording that this ledger has been healed of fuel rewards written
        /// as paid by the old success predicate. Every reader matches ledger tokens exactly,
        /// so an older build carrying this token simply ignores it.
        /// </summary>
        internal const string FuelHealMarker = "schema:fuel-heal.v1";

        private const string FuelKind = "fuel";

        internal static string RewardId(int quota, string kind) => "q" + quota + ":" + (kind ?? string.Empty);

        internal static string DeferredId(string rewardId) => (rewardId ?? string.Empty) + DeferredSuffix;

        internal static bool IsPaid(ICollection<string> ledger, string rewardId) =>
            ledger != null && !string.IsNullOrEmpty(rewardId) && ledger.Contains(rewardId);

        internal static bool IsDeferred(ICollection<string> ledger, string rewardId) =>
            !IsPaid(ledger, rewardId) && ledger != null && !string.IsNullOrEmpty(rewardId) && ledger.Contains(DeferredId(rewardId));

        /// <summary>
        /// Applies one milestone reward exactly once. <paramref name="grant"/> must report whether
        /// it actually delivered something and is never invoked when
        /// <paramref name="providerAvailable"/> is false. Calling this repeatedly for the same
        /// reward id is idempotent, which is what makes duplicate rollover hooks harmless.
        /// </summary>
        internal static QuotaRewardOutcome Apply(
            ICollection<string> ledger,
            string rewardId,
            float amount,
            bool providerAvailable,
            Func<float, bool> grant) => Apply(ledger, rewardId, amount, providerAvailable, grant, out _);

        /// <summary>
        /// <paramref name="ledgerChanged"/> reports whether this call actually mutated the
        /// ledger, so a caller can skip persisting a pass that changed nothing. A reward that
        /// was already deferred and still cannot be granted is the steady state of the retry
        /// pass and does not count as a change.
        /// </summary>
        internal static QuotaRewardOutcome Apply(
            ICollection<string> ledger,
            string rewardId,
            float amount,
            bool providerAvailable,
            Func<float, bool> grant,
            out bool ledgerChanged)
        {
            ledgerChanged = false;
            if (ledger == null || string.IsNullOrEmpty(rewardId) || amount <= 0f)
                return QuotaRewardOutcome.NotConfigured;
            if (ledger.Contains(rewardId))
                return QuotaRewardOutcome.AlreadyPaid;

            if (!providerAvailable || grant == null || !grant(amount))
            {
                string deferredId = DeferredId(rewardId);
                if (!ledger.Contains(deferredId))
                {
                    ledger.Add(deferredId);
                    ledgerChanged = true;
                }
                return QuotaRewardOutcome.Deferred;
            }

            if (ledger.Remove(DeferredId(rewardId))) ledgerChanged = true;
            if (!ledger.Contains(rewardId))
            {
                ledger.Add(rewardId);
                ledgerChanged = true;
            }
            return QuotaRewardOutcome.Granted;
        }

        internal static bool HasDeferredRewards(IEnumerable<string> ledger) =>
            ledger != null && ledger.Any(entry => entry != null && entry.EndsWith(DeferredSuffix, StringComparison.Ordinal));

        /// <summary>
        /// Deferred delivery is the mechanism, not an optional nicety: a reward already carrying
        /// a deferred marker is retried whether or not the optional-reward retry config is on.
        /// </summary>
        internal static bool ShouldRetryRewards(bool retryOptionalRewardsEnabled, IEnumerable<string> ledger) =>
            retryOptionalRewardsEnabled || HasDeferredRewards(ledger);

        /// <summary>
        /// One-time repair of ledgers written before the fuel success predicate was fixed. The
        /// rollover refill always ran before the milestone grant, so AddFuel returned 0 every
        /// time and every qN:fuel token in an old ledger records a payment that never happened.
        /// Each is converted back to its deferred state so the normal reward pass can pay it.
        /// Returns whether the ledger changed and therefore needs saving.
        /// </summary>
        internal static bool HealFalsePaidFuelRewards(ICollection<string> ledger)
        {
            if (ledger == null || ledger.Contains(FuelHealMarker)) return false;
            foreach (string falsePaid in ledger.Where(IsFuelRewardId).ToList())
            {
                ledger.Remove(falsePaid);
                string deferredId = DeferredId(falsePaid);
                if (!ledger.Contains(deferredId)) ledger.Add(deferredId);
            }
            ledger.Add(FuelHealMarker);
            return true;
        }

        private static bool IsFuelRewardId(string entry)
        {
            if (string.IsNullOrEmpty(entry) || entry[0] != 'q') return false;
            int separator = entry.IndexOf(':');
            if (separator < 2) return false;
            if (!string.Equals(entry.Substring(separator + 1), FuelKind, StringComparison.Ordinal)) return false;
            for (int index = 1; index < separator; index++)
                if (!char.IsDigit(entry[index])) return false;
            return true;
        }
    }
}
