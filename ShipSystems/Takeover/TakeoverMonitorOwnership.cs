// TakeoverMonitorOwnership.cs — #610. The monitor takeover's side of the Core
// surface arbiter and the Core ship-layout fan-out.
//
// Everything here exists because of one boundary the DLL split has to cut. The
// takeover paints the ship's monitor wall; three things outside the takeover
// need to know that it is doing so, and until now each reached in and took what
// it needed:
//
//   * ShipLayoutRefresh resolved "…TakeoverManager, Y4NGZCompany" by reflection
//     and invoked the private ForceRestore. Post-split that string resolves to
//     nothing and fails silently, leaving monitor state saved from a wall that
//     no longer exists.
//   * CeremonyCameraMonitorBinding called TakeoverManager.IsShipSystemsClaimOwned-
//     Material directly to skip screens somebody else is already painting.
//   * The contracts patches read TakeoverManager.IsActive.
//
// All three are now questions asked of Core: a layout subscription, a claim
// probe, and a wall lease whose priority answers "is something at least this
// important on screen". None of the three askers names the takeover any more.

using System;
using Y4NGZCompany.Bootstrap;
using Y4NGZCore.Modules.Layout;
using Y4NGZCore.Modules.Monitor;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    internal static class TakeoverMonitorOwnership
    {
        /// <summary>
        /// The takeover's owner ID in the arbiter and in the claim-probe registry. Appears in
        /// ownership diagnostics; stable, so keep it.
        /// </summary>
        internal const string OwnerId = "Y4NGZCompany:MonitorTakeover";

        /// <summary>
        /// The quota unlock announcement's owner ID. A separate owner from the takeover on a
        /// separate surface, at a separate priority — they are two presentations that happen to
        /// live in the same namespace today and in the same order everywhere.
        /// </summary>
        internal const string AnnouncementOwnerId = "Y4NGZCompany:QuotaAnnouncement";

        private static bool _registered;
        private static MonitorLease _wallLease;
        private static MonitorLease _announcementLease;

        /// <summary>
        /// Registers the takeover with Core. Idempotent; called from
        /// <c>TakeoverBootstrap.Initialize</c>, which is this feature's own bootstrap — the
        /// point being that nothing else has to know to call it.
        /// </summary>
        internal static void EnsureRegistered()
        {
            if (_registered)
                return;
            _registered = true;

            MonitorArbiter.RegisterSurface(MonitorSurfaces.MonitorWall, new WallBinding());
            MonitorArbiter.RegisterSurface(
                MonitorSurfaces.AnnouncementScreens, new AnnouncementBinding());

            // Exactly what the reflected ForceRestore call did, in exactly the position it did
            // it: after the wall has been rebuilt, before anything reads its finished state.
            ShipLayoutNotifications.Subscribe(OnShipLayoutChanged, ShipLayoutOrders.MonitorOwners);

            // The claim probe is deliberately NOT registered here. The materials it recognises
            // belong to Ship Systems, not to the takeover, and this bootstrap runs behind the
            // takeover's own enable flag and asset-bundle load. Registering it here made "is
            // this screen already claimed" answer false whenever Monitor Takeover was disabled,
            // which is precisely when the ceremony's wall scan would paint over the monitor
            // row's faces. See ShipSystemsMonitorClaims.EnsureProbeRegistered.
        }

        private static void OnShipLayoutChanged(ShipLayoutChange change)
        {
            if (!change.Affects(ShipLayoutChangeScope.MonitorWall))
                return;

            // The takeover manager saves monitor renderer state. If the layout changed, its
            // saved state may be stale. ForceRestore cleans it up safely, and is a no-op when
            // no takeover is running.
            try
            {
                TakeoverManager.Instance?.ForceRestore($"ship layout changed: {change.Reason}");
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log?.LogWarning(
                    $"[TakeoverManager] Layout-change restore failed: {ex.Message}");
            }
        }

        // ── Wall ownership ────────────────────────────────────────────────────

        /// <summary>
        /// Declares that the takeover now owns the monitor wall, at
        /// <see cref="MonitorPriority.QuotaTakeover"/>.
        ///
        /// <para><b>This does not paint anything.</b> The takeover's own save/paint/restore of
        /// the wall is unchanged and still exact — it covers per-slot HDRP state, video clips,
        /// RawImage overlays and a General Improvements property-block lease, none of which the
        /// arbiter is in a position to snapshot for it. What the lease adds is the answer to
        /// "who owns the wall right now", asked and answered without either side naming the
        /// other. Moving the wall's snapshot itself behind
        /// <see cref="IMonitorSurfaceBinding"/> is the next task's work, not this one's: doing
        /// it here would change which content wins on screen, and this change must not.</para>
        ///
        /// <para>Idempotent — a second call while the lease is held is a no-op.</para>
        /// </summary>
        internal static void ClaimWall(string reason)
        {
            EnsureRegistered();
            if (_wallLease != null && _wallLease.IsHeld)
                return;

            _wallLease = MonitorArbiter.Acquire(
                new MonitorLeaseRequest(
                    OwnerId,
                    MonitorSurfaces.MonitorWall,
                    MonitorPriority.QuotaTakeover,
                    reason: reason),
                out MonitorLeaseOutcome outcome);

            if (outcome == MonitorLeaseOutcome.Granted || outcome == MonitorLeaseOutcome.Deferred)
                return;

            // NoSurface cannot happen — EnsureRegistered just registered it — so this is a
            // malformed request, which would be a bug here rather than a profile condition.
            TakeoverBootstrap.Log?.LogWarning(
                $"[TakeoverManager] Monitor-wall lease refused ({outcome}) for '{reason}'.");
            _wallLease = null;
        }

        /// <summary>
        /// Gives the wall back. Idempotent, and safe to call when the wall was never claimed —
        /// which is what every teardown path needs.
        /// </summary>
        internal static void ReleaseWall()
        {
            if (_wallLease == null)
                return;

            MonitorArbiter.Release(_wallLease);
            _wallLease = null;
        }

        // ── Announcement-screen ownership ─────────────────────────────────────

        /// <summary>
        /// Declares that the quota unlock announcement owns the large announcement screens, at
        /// <see cref="MonitorPriority.QuotaAnnouncement"/>.
        ///
        /// <para>This is the ownership half of #335. The reward screen and the temporary Ship
        /// Systems banners used to know nothing about each other, so whichever painted last won
        /// and the reward screen could be covered by a fuel overlay. They now sit at 4 and 5 in
        /// one table, on surfaces the arbiter can name, and the answer is the same from either
        /// side of the DLL split.</para>
        ///
        /// <para>Like <see cref="ClaimWall"/>, this does not paint: the announcement's own
        /// property-block lease and material-slot save are exact and unchanged. Returns true
        /// when the announcement is the surface's winner.</para>
        ///
        /// <para><b>#612 task 1.4: the caller obeys this now.</b>
        /// <c>QuotaUnlockAnnouncement.BindMonitors</c> discarded the return value until this
        /// task; it defers the sequence on false. That closes the loop on the ownership side but
        /// did not by itself change anything on screen, because the surface then had exactly one
        /// claimant — see the note in <see cref="AnnouncementBinding"/>, which records what each
        /// wave since has added.</para>
        ///
        /// <para><b>No <see cref="MonitorLeaseRequest.Displaced"/> callback here, deliberately
        /// (#614 task 3.1).</b> The ceremony ribbon needs one because it is the weaker of the two
        /// claimants and is the one that gets displaced; this lease is the stronger, and nothing
        /// shipped outranks <see cref="MonitorPriority.QuotaAnnouncement"/> on this surface. If
        /// something ever does — a safety or performance-report presentation claiming the
        /// announcement screens — the callback to add is NOT <c>RestoreAll</c>: that releases the
        /// lease and would cancel a ceremony the crew earned rather than suspend it. It would have
        /// to be a slot-only unbind that keeps the lease and the sequence, mirroring
        /// <c>CeremonyCameraMonitorBinding.StandDown</c>, plus a resume path this class does not
        /// have today. Adding an unexercised one now would be a guess.</para>
        /// </summary>
        internal static bool ClaimAnnouncementScreens(string reason)
        {
            EnsureRegistered();
            if (_announcementLease != null && _announcementLease.IsHeld)
                return _announcementLease.IsWinner;

            _announcementLease = MonitorArbiter.Acquire(
                new MonitorLeaseRequest(
                    AnnouncementOwnerId,
                    MonitorSurfaces.AnnouncementScreens,
                    MonitorPriority.QuotaAnnouncement,
                    reason: reason));

            return _announcementLease.IsWinner;
        }

        /// <summary>Gives the announcement screens back. Idempotent.</summary>
        internal static void ReleaseAnnouncementScreens()
        {
            if (_announcementLease == null)
                return;

            MonitorArbiter.Release(_announcementLease);
            _announcementLease = null;
        }

        // ── Session boundaries ────────────────────────────────────────────────

        /// <summary>
        /// Drops every monitor claim this session took. Called from the disconnect and
        /// reset-saved-values patches, beside the takeover's existing emergency restore.
        ///
        /// <para><b>What must not survive.</b> Held leases and the handles that name them. A
        /// lease describes a presentation running <em>now</em>; a disconnect ends every
        /// presentation whether or not its owner got to say so, because a coroutine killed by a
        /// scene change never reaches its release. A lease that survived would have the arbiter
        /// reporting the wall as owned in the next lobby, and every later claimant would queue
        /// behind a takeover that ended two sessions ago.</para>
        ///
        /// <para><b>What must survive.</b> <see cref="_registered"/>, and with it the surface
        /// registrations, the ship-layout subscription and (on the Ship Systems side) the claim
        /// probe. None of those is per-session: the bindings belong to plugin-lifetime statics
        /// and are still the right way to reach the hardware next lobby, the layout
        /// subscription would fan out twice if re-added, and the claim predicate is pure. Worse,
        /// clearing the registrations would leave the next lobby's owners getting
        /// <see cref="MonitorLeaseOutcome.NoSurface"/> until something happened to re-register,
        /// and this bootstrap only runs once per process.</para>
        ///
        /// <para>The <em>row</em> surface is the exception and needs no help here: it registers
        /// and unregisters with the meshes it describes, in
        /// <c>MonitorRowSurface.Ensure</c>/<c>Destroy</c>, because those really are rebuilt per
        /// session.</para>
        /// </summary>
        internal static void ResetForNewSession(string reason)
        {
            ReleaseWall();
            ReleaseAnnouncementScreens();

            // Belt and braces for a lease whose handle was lost with the object that held it.
            // Releasing by owner ID covers what releasing by handle cannot.
            MonitorArbiter.ReleaseAll(OwnerId);
            MonitorArbiter.ReleaseAll(AnnouncementOwnerId);

            TakeoverBootstrap.Log?.LogInfo(
                $"[TakeoverManager] Monitor claims reset ({reason}). " +
                MonitorArbiter.DescribeOwnership());
        }

        /// <summary>
        /// The announcement screens as a leasable surface. Ownership-only, for the same reason
        /// <see cref="WallBinding"/> is: the announcement already snapshots the exact slots it
        /// borrows, and a second snapshot of the same renderers is the bug, not the fix.
        ///
        /// <para><b>#612 task 1.4 — what is actually blocking #335, measured.</b> The plan for
        /// this task assumed the no-op bodies here were the reason #335 is still visible, and
        /// that moving the features' per-slot snapshots in here would close it. A grep of every
        /// <c>MonitorArbiter.Acquire</c> call site in <c>src/</c> says otherwise: the only leases
        /// taken anywhere are <c>monitor.row</c> (<c>MonitorRowSurface.cs:584</c>) and this
        /// feature's own two (<c>TakeoverMonitorOwnership.cs:116</c> and <c>:169</c>). <b>Ship
        /// Systems' ceremony banner never claims this surface at all.</b> With one claimant the
        /// arbiter is not arbitrating anything — the announcement wins trivially, every time,
        /// and the banner paints over it through a path the arbiter never sees. Real snapshot
        /// bodies here would change nothing about that, because a snapshot decides how a surface
        /// is handed back, not who is allowed to draw on it.</para>
        ///
        /// <para>What #335 needs, in order:
        /// <list type="number">
        /// <item>The announcement obeys <c>ClaimAnnouncementScreens</c>. <b>Done in task 1.4</b> —
        /// inert then by construction, live once step 2 landed.</item>
        /// <item>Ship Systems' ceremony banner acquires a lease on this surface at
        /// <c>MonitorPriority.Banner</c> (5, outranked by <c>QuotaAnnouncement</c> = 4) and
        /// stands down when it is not the winner. <b>Done in #613 task 2.1</b> —
        /// <c>CeremonyCameraMonitorBinding.TryHoldAnnouncementScreens</c>. This surface really
        /// has two claimants now, so the measurement in the paragraph above is history: the
        /// arbiter is arbitrating.</item>
        /// <item><b>The stand-down is pushed, not polled. Done in #614 task 3.1</b> —
        /// <c>MonitorLeaseRequest.Displaced</c>. This is the half of step 3 that was actually
        /// load-bearing. With two claimants that each paint the hardware themselves and each keep
        /// their own record of what was in the slot, the ORDER decides whether the arbitration is
        /// correct: the ribbon used to learn it had lost on its own next slow tick (throttled to
        /// 1.5s in <c>CeremonyCameraMonitorBinding.ResolveRetrySeconds</c>) while
        /// <c>QuotaUnlockAnnouncement.BindMonitors</c> runs <c>BindSlot</c> the instant its claim
        /// returns — so the announcement saved the ribbon's clone as the material to restore. The
        /// arbiter now raises the displaced owner's stand-down from inside the very
        /// <c>Acquire</c> that took the surface, before any capture or apply, so the incoming
        /// claimant always reads the vanilla material. Pinned by
        /// <c>MonitorArbitrationRegression</c>'s five displacement tests, executed against the
        /// real arbiter.</item>
        /// <item><b>Not done, and deliberately not attempted here:</b> relocating the two
        /// features' per-slot snapshots into these binding bodies so the surface has literally one
        /// snapshot rather than two correctly-ordered ones. The objection recorded in task 1.4
        /// still stands and is unaffected by the three steps above: the takeover captures lazily,
        /// per renderer, during its paint walk (<c>TakeoverManager.ApplyMonitorOverride</c>, the
        /// <c>_savedHDRPState.ContainsKey</c> guard); the announcement captures per scored slot in
        /// <c>BindSlot</c>; and <c>IMonitorSurfaceBinding.CaptureSnapshot</c> is one eager call at
        /// lease time, before either walk has decided which renderers it will touch. The two
        /// claimants also target different slot sets and carry claimant-specific side effects a
        /// shared binding cannot own — the ribbon's per-frame marquee UV transform
        /// (<c>UpdateScroll</c>), and the announcement's OpenBodyCams suspension, producer
        /// suspension, level-description suspension and General Improvements property-block lease,
        /// which bypasses material slots entirely. Moving them changes which renderers are
        /// snapshotted and when, on both backings, and cannot be validated off-engine. It wants
        /// its own task with an in-game verification, not a rider on an extraction.</item>
        /// </list></para>
        /// </summary>
        private sealed class AnnouncementBinding : IMonitorSurfaceBinding
        {
            public bool IsAvailable => StartOfRound.Instance != null;

            public MonitorSurfaceInfo Info =>
                new MonitorSurfaceInfo(
                    MonitorSurfaces.AnnouncementScreens,
                    WallBinding.CurrentBacking(),
                    detail: "large announcement screens");

            public object CaptureSnapshot() => null;

            public void RestoreSnapshot(object snapshot)
            {
            }

            public void Apply(MonitorLease lease)
            {
            }
        }

        /// <summary>
        /// The wall as a leasable surface. Ownership-only for now: the takeover keeps its own
        /// snapshot (see <see cref="ClaimWall"/>), so capture and restore here would be a second
        /// snapshot of the same renderers — the exact arrangement the arbiter exists to prevent.
        /// One owner, one snapshot; the owner is the takeover until 1.3 moves it.
        /// </summary>
        private sealed class WallBinding : IMonitorSurfaceBinding
        {
            public bool IsAvailable => StartOfRound.Instance != null;

            public MonitorSurfaceInfo Info =>
                new MonitorSurfaceInfo(
                    MonitorSurfaces.MonitorWall,
                    CurrentBacking(),
                    detail: "ship monitor wall");

            public object CaptureSnapshot() => null;

            public void RestoreSnapshot(object snapshot)
            {
            }

            public void Apply(MonitorLease lease)
            {
            }

            /// <summary>
            /// Which hardware is behind the wall right now. Reads the consolidated General
            /// Improvements probe (#599) rather than a private copy of the reflection.
            /// </summary>
            internal static MonitorSurfaceBacking CurrentBacking() =>
                Y4NGZCompany.Core.Compat.GeneralImprovementsMonitors.BetterMonitorsActive
                    ? MonitorSurfaceBacking.GeneralImprovements
                    : MonitorSurfaceBacking.Vanilla;
        }
    }
}
