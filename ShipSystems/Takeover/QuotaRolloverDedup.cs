using System;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    /// <summary>
    /// One-per-rollover gate for the quota hooks. On the host a single rollover can drive the
    /// patched methods three times: the SyncNewProfitQuotaClientRpc send stub, its loopback
    /// execution of the same method body, and the TimeOfDay.SetNewProfitQuota postfix. A
    /// rollover is identified by save file plus fulfilled-quota count, so every fire after the
    /// first is dropped instead of relying on the side effects happening to be idempotent.
    /// </summary>
    internal sealed class QuotaRolloverDedup
    {
        private string _handledKey;

        /// <summary>Null <paramref name="saveFileName"/> is normalised, matching the unsaved lobby case.</summary>
        internal static string BuildKey(string saveFileName, int timesFulfilledQuota) =>
            (string.IsNullOrEmpty(saveFileName) ? "unsaved" : saveFileName) + ":" + timesFulfilledQuota;

        /// <summary>
        /// True when the caller owns this rollover and should run its side effects. A null key
        /// means the key could not be computed, and dedup fails open rather than latching a
        /// placeholder that would suppress every later rollover.
        /// </summary>
        internal bool TryClaim(string key)
        {
            if (key == null) return true;
            if (_handledKey != null && string.Equals(_handledKey, key, StringComparison.Ordinal)) return false;
            _handledKey = key;
            return true;
        }

        internal void Reset() => _handledKey = null;
    }
}
