// OpenBodyCamsCompat.cs — LGUMonitorTakeover
//
// Soft-dependency suppression for OpenBodyCams. OBC attaches a
// BodyCamComponent to MonitorWall/Cube.001 and replaces material slot 2
// with its own HDRP/Unlit material via MonitorRenderer.SetMaterial in
// UpdateScreenMaterial. That call orphans our SetTexture("_BaseColorMap",
// _renderTexture) write, leaving the slot pointing at OBC's white-emissive
// "BodyCamMaterial". We disable the BodyCamComponent at takeover start so
// its LateUpdate stops running, then re-enable on restore.
//
// No hard reference to OpenBodyCams. Reflection only. No-op when OBC is
// not installed.

using System;
using BepInEx.Bootstrap;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    internal static class OpenBodyCamsCompat
    {
        private const string PluginGuid = "Zaggy1024.OpenBodyCams";

        private static bool _resolved;
        private static bool _present;
        private static Behaviour _bodyCamBehaviour;
        private static bool _wasEnabled;

        // OBC also instantiates a BodyCamOverlayMesh(Clone) under MonitorWall
        // whose geometry is a copy of Cube.001's submesh 2 and whose material
        // (BodyCamOverlayMeshMaterial) renders its own BodyCamOverlayTexture
        // RT. When the BodyCamComponent is disabled the overlay's RT stops
        // being fed and the mesh renders solid white over Cube.001's frame —
        // the visible white border. Hide it during takeover.
        private static GameObject _overlayMeshGO;
        private static bool _overlayWasActive;

        // Announcement-scoped suspension holds its own saved state so the
        // rewards ceremony and the takeover never read each other's snapshot.
        private static bool _announcementSuspended;
        private static bool _announcementWasEnabled;
        private static bool _announcementOverlayWasActive;

        private static void Resolve()
        {
            _resolved = true;
            try
            {
                if (!Chainloader.PluginInfos.ContainsKey(PluginGuid)) return;

                var monitorWall = GameObject.Find("Environment/HangarShip/ShipModels2b/MonitorWall");
                if (monitorWall == null)
                {
                    TakeoverBootstrap.Log.LogInfo("[OBCCompat] OpenBodyCams loaded but MonitorWall not found at resolve time.");
                    return;
                }

                Type bcType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "OpenBodyCams") continue;
                    bcType = asm.GetType("OpenBodyCams.Components.BodyCamComponent", false)
                          ?? asm.GetType("OpenBodyCams.BodyCamComponent", false);
                    if (bcType != null) break;
                }
                if (bcType == null)
                {
                    TakeoverBootstrap.Log.LogWarning("[OBCCompat] BodyCamComponent type not found in OpenBodyCams assembly.");
                    return;
                }

                // #599: OBC has native GeneralImprovements support. With
                // UseBetterMonitors on it puts the BodyCamComponent on one of
                // GI's replacement screens under
                // MonitorWall/MonitorGroup(Clone)/Monitors/..., not on Cube.001.
                // Searching the subtree covers vanilla and GI with one lookup —
                // the old Cube.001-only check silently found nothing under GI and
                // left OBC writing over the takeover.
                if (!(monitorWall.GetComponentInChildren(bcType, true) is Behaviour bc))
                {
                    TakeoverBootstrap.Log.LogInfo("[OBCCompat] No BodyCamComponent under MonitorWall — nothing to suppress.");
                    return;
                }

                _bodyCamBehaviour = bc;
                _present = true;
                TakeoverBootstrap.Log.LogInfo(
                    $"[OBCCompat] Detected OpenBodyCams BodyCamComponent on '{bc.gameObject.name}'.");

                // Find the overlay mesh — name pattern is "BodyCamOverlayMesh(Clone)".
                // Scoped to the MonitorWall subtree rather than searched globally so
                // we only catch the one OBC parents to the ship monitors; recursive
                // because under GI it hangs off a screen, not off the wall root.
                _overlayMeshGO = FindOverlayMeshUnder(monitorWall.transform);
                if (_overlayMeshGO != null)
                    TakeoverBootstrap.Log.LogInfo($"[OBCCompat] Detected OBC overlay mesh: {_overlayMeshGO.name}");
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[OBCCompat] Resolve failed: {ex.Message}");
            }
        }

        private static GameObject FindOverlayMeshUnder(Transform parent)
        {
            foreach (Transform child in parent)
            {
                if (child == null) continue;
                if (child.name != null && child.name.StartsWith("BodyCamOverlayMesh", StringComparison.Ordinal))
                    return child.gameObject;

                GameObject nested = FindOverlayMeshUnder(child);
                if (nested != null) return nested;
            }
            return null;
        }

        internal static void Suppress()
        {
            // Re-resolve when the cached behaviour has been destroyed:
            // GeneralImprovements rebuilds its MonitorGroup on every
            // StartOfRound.Start and on the client-join monitor sync, taking
            // OBC's host object with it (#599).
            if (!_resolved || (_present && _bodyCamBehaviour == null))
            {
                _resolved = false;
                _present = false;
                _overlayMeshGO = null;
                Resolve();
            }
            if (!_present || _bodyCamBehaviour == null) return;

            try
            {
                _wasEnabled = _bodyCamBehaviour.enabled;
                if (_wasEnabled)
                {
                    _bodyCamBehaviour.enabled = false;
                    TakeoverBootstrap.Log.LogInfo($"[OBCCompat] Suppressed OpenBodyCams BodyCamComponent on '{_bodyCamBehaviour.gameObject.name}'.");
                }

                if (_overlayMeshGO != null)
                {
                    _overlayWasActive = _overlayMeshGO.activeSelf;
                    if (_overlayWasActive)
                    {
                        _overlayMeshGO.SetActive(false);
                        TakeoverBootstrap.Log.LogInfo("[OBCCompat] Hid OBC overlay mesh under MonitorWall.");
                    }
                }
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[OBCCompat] Suppress failed: {ex.Message}");
            }
        }

        internal static void Restore()
        {
            if (!_present || _bodyCamBehaviour == null) return;

            try
            {
                if (_overlayMeshGO != null && _overlayWasActive && !_overlayMeshGO.activeSelf)
                {
                    _overlayMeshGO.SetActive(true);
                    TakeoverBootstrap.Log.LogInfo("[OBCCompat] Restored OBC overlay mesh under MonitorWall.");
                }

                if (_wasEnabled && !_bodyCamBehaviour.enabled)
                {
                    _bodyCamBehaviour.enabled = true;
                    TakeoverBootstrap.Log.LogInfo($"[OBCCompat] Restored OpenBodyCams BodyCamComponent on '{_bodyCamBehaviour.gameObject.name}'.");
                }
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[OBCCompat] Restore failed: {ex.Message}");
            }
        }

        // ── Announcement scope ────────────────────────────────────────────────
        // The quota rewards ceremony borrows the wall after the takeover has
        // already restored, so this scope must not disturb the takeover's
        // Suppress/Restore snapshot above.

        internal static void SuspendForAnnouncement()
        {
            if (_announcementSuspended) return;
            if (!_resolved) Resolve();
            if (!_present || _bodyCamBehaviour == null) return;

            try
            {
                _announcementWasEnabled = _bodyCamBehaviour.enabled;
                if (_announcementWasEnabled)
                    _bodyCamBehaviour.enabled = false;

                _announcementOverlayWasActive = _overlayMeshGO != null && _overlayMeshGO.activeSelf;
                if (_announcementOverlayWasActive)
                    _overlayMeshGO.SetActive(false);

                _announcementSuspended = true;
                TakeoverBootstrap.Log.LogInfo("[OBCCompat] Suspended OpenBodyCams for the unlock announcement.");
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[OBCCompat] Announcement suspend failed: {ex.Message}");
            }
        }

        internal static void RestoreForAnnouncement()
        {
            if (!_announcementSuspended) return;
            _announcementSuspended = false;
            if (_bodyCamBehaviour == null) return;

            try
            {
                // A takeover that starts mid-announcement aborts it on the very
                // frame IsActive turns true, but the takeover's own Suppress()
                // only runs after its orbit delay and light dimming. Restoring
                // physically here means that later Suppress() captures the true
                // pre-announcement state itself; merely ORing our snapshot into
                // the takeover's fields would be overwritten by that capture and
                // leave OBC disabled forever.
                if (_announcementOverlayWasActive && _overlayMeshGO != null && !_overlayMeshGO.activeSelf)
                    _overlayMeshGO.SetActive(true);

                if (_announcementWasEnabled && !_bodyCamBehaviour.enabled)
                    _bodyCamBehaviour.enabled = true;

                TakeoverBootstrap.Log.LogInfo("[OBCCompat] Restored OpenBodyCams after the unlock announcement.");
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[OBCCompat] Announcement restore failed: {ex.Message}");
            }
        }
    }
}
