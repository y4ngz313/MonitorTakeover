namespace Y4NGZCompany.ShipSystems.Takeover
{
    internal static class QuotaUnlockAnnouncementPolicy
    {
        internal static bool ShouldDispatch(
            int quota,
            int lastAnnouncedQuota,
            bool debugReplay)
        {
            return quota > 0 && (debugReplay || quota > lastAnnouncedQuota);
        }

        internal static bool ShouldPersist(bool debugReplay)
        {
            return !debugReplay;
        }

        internal static bool ShouldSuppressPresentation(
            int quota,
            int lastShownQuota,
            bool debugReplay)
        {
            return !debugReplay && quota == lastShownQuota;
        }
    }
}
