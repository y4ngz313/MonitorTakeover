namespace Y4NGZCompany.Core
{
    public enum ForcedQuotaCompletionResult
    {
        StartedImmediately,
        QueuedUntilOrbit,
        HostOnly,
        InvalidQuota,
        NotInRound,
        Busy,
        Unavailable
    }

    /// <summary>
    /// Optional debug integration surface. Callers should resolve this type by
    /// reflection so diagnostics remains usable without Y4NGZCompany installed.
    /// </summary>
    public static class QuotaProgressionDebugApi
    {
        public static ForcedQuotaCompletionResult ForceCompletedQuota(int completedQuota)
        {
            try
            {
                return Y4NGZCompany.ShipSystems.Takeover.QuotaTakeoverDebugController
                    .ForceCompletedQuota(completedQuota);
            }
            catch (System.Exception ex)
            {
                // #612 task 1.4 (R3): this file ships with Monitor Takeover (plan, "Core/ (12
                // files)"), so Y4NGZCompany.Plugin — Contracted's `internal` plugin type — is
                // not reachable from it after the split. Same source in the monolith.
                Y4NGZCore.Diagnostics.ModuleLog.Takeover.LogWarning(
                    // #782: the whole exception, not its message. A bare "Object reference not set"
                    // once cost a full diagnosis pass to locate; the stack names the site.
                    $"QuotaProgressionDebugApi.ForceCompletedQuota failed: {ex}");
                return ForcedQuotaCompletionResult.Unavailable;
            }
        }
    }
}
