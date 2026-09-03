// MonitorDebugger.cs — LGUMonitorTakeover
//
// Press F8 while standing in the ship to dump a full diagnostic report to the
// BepInEx console. Copy the output and share it so the monitor overrides can be
// written precisely against the actual runtime hierarchy.
//
// Remove or disable this file before shipping the mod.

using System.Text;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.UI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCore.Modules.ShipSystems;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    internal class MonitorDebugger : MonoBehaviour
    {
        // Called automatically from Patches.OnSetShipReadyToLand so no keypress needed.
        internal static void DumpAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("========== Y4NGZ MONITOR DEBUGGER ==========");

            var sor = StartOfRound.Instance;
            if (sor == null) { TakeoverBootstrap.Log.LogWarning("StartOfRound.Instance is null — are you in-game?"); return; }

            // ── 1. screenLevelVideoReel ──────────────────────────────────────
            sb.AppendLine("\n--- screenLevelVideoReel (VideoPlayer) ---");
            var vr = sor.screenLevelVideoReel;
            if (vr == null)
            {
                sb.AppendLine("  NULL");
            }
            else
            {
                sb.AppendLine($"  renderMode      : {vr.renderMode}");
                sb.AppendLine($"  targetTexture   : {(vr.targetTexture != null ? vr.targetTexture.name : "null")}");
                sb.AppendLine($"  clip            : {(vr.clip != null ? vr.clip.name : "null")}");
                sb.AppendLine($"  isPlaying       : {vr.isPlaying}");
                sb.AppendLine($"  gameObject path : {GetPath(vr.gameObject)}");

                // If MaterialOverride, log which renderer it targets
                if (vr.renderMode == VideoRenderMode.MaterialOverride)
                {
                    var targetRenderer = vr.GetComponent<Renderer>() ?? vr.GetComponentInChildren<Renderer>();
                    sb.AppendLine($"  MaterialOverride renderer: {(targetRenderer != null ? GetPath(targetRenderer.gameObject) : "none on same GO")}");
                }
            }

            // ── 2. mapScreen (ManualCameraRenderer) ─────────────────────────
            sb.AppendLine("\n--- mapScreen (ManualCameraRenderer) ---");
            var ms = sor.mapScreen;
            if (ms == null)
            {
                sb.AppendLine("  NULL");
            }
            else
            {
                sb.AppendLine($"  gameObject path : {GetPath(ms.gameObject)}");
                sb.AppendLine($"  cam             : {(ms.cam != null ? ms.cam.name : "null")}");
                if (ms.cam != null)
                    sb.AppendLine($"  cam.targetTexture: {(ms.cam.targetTexture != null ? ms.cam.targetTexture.name : "null")}");
                sb.AppendLine($"  mapCamera       : {(ms.mapCamera != null ? ms.mapCamera.name : "null")}");
                if (ms.mapCamera != null)
                    sb.AppendLine($"  mapCamera.targetTexture: {(ms.mapCamera.targetTexture != null ? ms.mapCamera.targetTexture.name : "null")}");
            }

            // ── 3. upperMonitorsCanvas ───────────────────────────────────────
            sb.AppendLine("\n--- upperMonitorsCanvas ---");
            var umc = sor.upperMonitorsCanvas;
            if (umc == null)
            {
                sb.AppendLine("  NULL");
            }
            else
            {
                sb.AppendLine($"  path            : {GetPath(umc)}");
                sb.AppendLine($"  parent          : {(umc.transform.parent != null ? GetPath(umc.transform.parent.gameObject) : "none")}");

                // All Canvas components in hierarchy
                var canvases = umc.GetComponentsInChildren<Canvas>(true);
                sb.AppendLine($"  child Canvas count: {canvases.Length}");
                foreach (var c in canvases)
                    sb.AppendLine($"    Canvas: {GetPath(c.gameObject)}  renderMode={c.renderMode}");

                // All MeshRenderers in parent
                if (umc.transform.parent != null)
                {
                    var mrs = umc.transform.parent.GetComponentsInChildren<MeshRenderer>(true);
                    sb.AppendLine($"  MeshRenderers in parent ({umc.transform.parent.name}): {mrs.Length}");
                    foreach (var r in mrs)
                    {
                        var mat = r.sharedMaterial;
                        string texInfo = mat?.mainTexture != null
                            ? $"{mat.mainTexture.GetType().Name}:{mat.mainTexture.name}"
                            : "no mainTexture";
                        sb.AppendLine($"    {GetPath(r.gameObject)}  mat={mat?.name}  tex={texInfo}");
                    }
                }
            }

            // ── 4. All MeshRenderers in ship that have a RenderTexture ───────
            sb.AppendLine("\n--- Ship MeshRenderers with RenderTexture mainTexture ---");
            var shipGO = GameObject.Find("Environment/HangarShip") ?? GameObject.Find("HangarShip");
            if (shipGO == null)
            {
                sb.AppendLine("  HangarShip not found in scene");
            }
            else
            {
                int rtCount = 0;
                foreach (var r in shipGO.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r.sharedMaterial?.mainTexture is RenderTexture rt)
                    {
                        sb.AppendLine($"  {GetPath(r.gameObject)}");
                        sb.AppendLine($"    material={r.sharedMaterial.name}  RT={rt.name} ({rt.width}x{rt.height})");
                        rtCount++;
                    }
                }
                if (rtCount == 0) sb.AppendLine("  (none found)");
            }

            // ── 5. profitQuotaMonitorBGImage / deadlineMonitorBGImage ────────
            sb.AppendLine("\n--- Top monitor UI components ---");
            LogUIComponent(sb, "profitQuotaMonitorBGImage", sor.profitQuotaMonitorBGImage);
            LogUIComponent(sb, "profitQuotaMonitorText",    sor.profitQuotaMonitorText);
            LogUIComponent(sb, "deadlineMonitorBGImage",    sor.deadlineMonitorBGImage);
            LogUIComponent(sb, "deadlineMonitorText",       sor.deadlineMonitorText);

            // ── 6. HUDContainer ──────────────────────────────────────────────
            sb.AppendLine("\n--- HUDContainer ---");
            var hud = HUDManager.Instance;
            if (hud?.HUDContainer != null)
                sb.AppendLine($"  path: {GetPath(hud.HUDContainer)}  active={hud.HUDContainer.activeSelf}");
            else
                sb.AppendLine("  HUDManager or HUDContainer is null");

            // ── Full MonitorWall hierarchy ───────────────────────────────────────
            sb.AppendLine("\n--- MonitorWall ALL MeshRenderers ---");
            var monitorWall = GameObject.Find("Environment/HangarShip/ShipModels2b/MonitorWall");
            if (monitorWall == null)
            {
                sb.AppendLine("  MonitorWall not found!");
            }
            else
            {
                foreach (var mr in monitorWall.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var sharedMat  = mr.sharedMaterial;
                    string sharedTex = sharedMat?.mainTexture != null
                        ? $"{sharedMat.mainTexture.GetType().Name}:{sharedMat.mainTexture.name}"
                        : "null";
                    // Check instance material without permanently creating one
                    string instTex = "n/a";
                    try
                    {
                        // hasMaterialPropertyBlock is cheaper than mr.material
                        var mpb = new MaterialPropertyBlock();
                        mr.GetPropertyBlock(mpb);
                        var mpbTex = mpb.GetTexture("_MainTex");
                        instTex = mpbTex != null
                            ? $"MPB:{mpbTex.name}"
                            : "no MPB";
                    }
                    catch { }
                    sb.AppendLine($"  {GetPath(mr.gameObject)}");
                    sb.AppendLine($"    sharedMat={sharedMat?.name}  sharedTex={sharedTex}  instCheck={instTex}");
                }
            }

            sb.AppendLine("\n--- shared small-monitor face map ---");

            sb.AppendLine("  " + ShipSystemsServices.DescribeMonitorFaceMap());
            sb.AppendLine("\n--- monitor-row runtime ---");
            sb.AppendLine("  " + ShipSystemsServices.DescribeMonitorRow());

            sb.AppendLine("\n========== END DEBUGGER ==========");

            // Write the whole thing to BepInEx log in one shot
            TakeoverBootstrap.Log.LogInfo(sb.ToString());
        }

        private static void LogUIComponent(StringBuilder sb, string label, Component c)
        {
            if (c == null) { sb.AppendLine($"  {label}: NULL"); return; }
            sb.AppendLine($"  {label}:");
            sb.AppendLine($"    path          : {GetPath(c.gameObject)}");
            sb.AppendLine($"    world position: {c.transform.position}");
            sb.AppendLine($"    enabled       : {((Behaviour)c).enabled}");
        }

        private static string GetPath(GameObject go)
        {
            if (go == null) return "(null)";
            var path = go.name;
            var t = go.transform.parent;
            int depth = 0;
            while (t != null && depth < 8)
            {
                path = t.name + "/" + path;
                t = t.parent;
                depth++;
            }
            return path;
        }
    }
}
