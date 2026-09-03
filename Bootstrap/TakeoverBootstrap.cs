// Plugin.cs - LGUMonitorTakeover
// Entry point, config, asset bundle loading, and Harmony wiring.

using System;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Video;
using Y4NGZCompany.ShipSystems.Takeover;

namespace Y4NGZCompany.Bootstrap
{
    internal static class TakeoverBootstrap
    {
        // #612 task 1.4: Monitor Takeover's own Harmony owner id. Sharing com.y4ngz.company
        // across three future DLLs would have meant UnpatchSelf() in any one of them stripping
        // the other two's patches, silently, whenever that DLL's host object died.
        public const string PLUGIN_GUID = Y4NGZCore.Lifecycle.ModuleHarmonyIds.MonitorTakeover;
        public const string PLUGIN_NAME = "Monitor Takeover";
        public const string PLUGIN_VERSION = "1.0.1";
        private const string BUNDLE_FILENAME = "monitortakeover.bundle";
        private const string LEGACY_BUNDLE_FILENAME = "monitortakeover.lethalbundle";

        // #668. The takeover video ships as a LOOSE mp4 beside this assembly, not
        // as the bundled VideoClip, and this is a correctness fix rather than a
        // packaging preference.
        //
        // monitortakeover.lethalbundle was built with enableTranscoding = false.
        // A non-transcoded VideoClip keeps no media inside the bundle: it stores
        // the editor's source path and asks the platform decoder to open THAT at
        // play time. On any machine that is not the authoring machine the path
        // does not exist, and Unity says so in one line, every Prepare:
        //
        //   VideoPlayer cannot play clip : Assets/ModAssets/MonitorTakeover/
        //   y4ngz_monitor_takeover.mp4 — Cannot read file.
        //
        // That is why the wall showed CRT static instead of the video on a
        // profile whose decoder is demonstrably fine (the vanilla map reel plays).
        // VideoPlayer.source = Url + an absolute path sidesteps the clip asset
        // entirely, so the shipped file is the media rather than a reference to
        // media that was never shipped. The bundled clip stays as the fallback:
        // it costs nothing, and on the authoring machine it still works.
        private const string DEFAULT_VIDEO_FILENAME = "y4ngz_monitor_takeover.mp4";

        /// <summary>
        /// Absolute path to the loose takeover mp4, or null when the file is not
        /// deployed. Resolved once at load, next to this assembly — the same
        /// directory <see cref="FindBundlePath"/> probes, for the same reason.
        /// </summary>
        internal static string TakeoverVideoPath;

        // #612 task 1.4 (D1): the `internal static object Instance` that used to sit here — and
        // on ShipSystemsBootstrap, StoreBootstrap and TerminalBootstrap — is gone. All four were
        // assigned `Y4NGZCompany.Plugin.Instance` once at startup and then read by nothing, in
        // src/ or tests/, ever. Plugin.Instance is `internal static` on Contracted's plugin type
        // (Plugin.cs:71), so each was a hard compile break at the assembly boundary in exchange
        // for no behaviour whatsoever. Deleting them is the whole fix: there is no need to route
        // through an entry point because there was no need being served.
        //
        // Do not re-add. If something ever genuinely needs a MonoBehaviour here, it wants
        // MonitorTakeoverPlugin.CoroutineHost (this plugin's own hardened #393 host object,
        // #614 task 3.1), not the plugin component — and certainly not Contracted's, which
        // this assembly cannot name at all any more.
        internal static ManualLogSource Log;

        // #612 task 1.4: the three quota-rollover handoff flags that used to live here —
        // QuotaJustCompleted, MaskManPending and FinalEndingPending — moved to
        // Y4NGZCore.Modules.Quota.QuotaTakeoverHandoff. They were `internal static` on a type
        // scheduled to land in the Monitor Takeover DLL, and `internal` does not survive an
        // assembly boundary, so any consumer outside that DLL would have stopped compiling at
        // the split. Semantics are unchanged: still three independent per-peer bools, still set
        // by the rollover hooks in Patches.cs and consumed at SetShipReadyToLand, still not
        // networked. See that type for why they are not an enum and for the measurement showing
        // no Ship Systems file ever read them.
        //
        // The stale TODO that stood here — "to support non-host clients, broadcast via a custom
        // ClientRpc; for now the takeover runs on the host only" — was already wrong when it was
        // written: patch 1b in Patches.cs postfixes SyncNewProfitQuotaClientRpc, which lands on
        // every peer, and that is what gives clients the flags today.

        // #681. The bundle handle is HELD for the process lifetime instead of being
        // unloaded once the assets are read, and that is the whole audio fix.
        //
        // Every takeover AudioClip in monitortakeover.lethalbundle is a streaming
        // clip: the serialized object carries only the header (name, length,
        // channels), and the sample payload lives in the bundle's companion
        // `.resource` FSB, read on demand at the moment an AudioSource plays it.
        // `bundle.Unload(false)` keeps the loaded objects alive but closes the
        // archive mount those reads go through, so from that instant every clip
        // is a valid-looking object whose samples can never be fetched:
        //
        //   Closing file archive:/CAB-b9a5c03e.../CAB-b9a5c03e....resource
        //   Could not open file archive:/CAB-b9a5c03e.../...resource for read
        //   Failed reading FSB data for audio clip "mumble_05".
        //
        // which is exactly what the Test 3 profile logged, for all five clips, on
        // every mumble step — alongside `playing 'mumble_05' (1.40s)
        // isPlaying=False`. The length in that line is the proof the media really
        // is in the bundle: unlike the takeover video (#668), nothing here is
        // missing and nothing needs to ship loose. The samples were simply locked
        // behind a door this method closed a few milliseconds after opening it.
        //
        // Holding the handle costs one mapped bundle for the session and is what
        // any clip-streaming consumer has to do. Nothing else may call Unload on
        // it; FindPreloadedBundle already returns this same instance were
        // LoadBundle ever re-entered.
        private static AssetBundle _bundle;

        // Cached assets from the bundle
        internal static VideoClip TakeoverVideoClip;
        internal static AudioClip[] MumbleClips = Array.Empty<AudioClip>();
        // Optional custom alarm clip. If the bundle contains "alarm_takeover"
        // we use it; otherwise TakeoverManager falls back to an in-game alarm
        // (ShipAlarmHornConstant etc.) which the user flagged as the wrong feel.
        // TODO (asset): author and ship a dedicated alarm clip in the bundle.
        internal static AudioClip AlarmClip;

        // Mask Man variant assets (quota-3 one-shot). Loaded from the same bundle.
        internal static VideoClip MaskManVideoClip;
        internal static AudioClip MaskManAudioClip;

        // Config entries
        internal static ConfigEntry<bool>  CfgEnabled;
        internal static ConfigEntry<float> CfgTakeoverDuration;
        internal static ConfigEntry<float> CfgOrbitDelay;
        internal static ConfigEntry<float> CfgMumbleVolume;
        internal static ConfigEntry<float> CfgTypewriterSpeed;
        internal static ConfigEntry<bool>  CfgHideHUD;
        internal static ConfigEntry<bool>  CfgDimLights;
        internal static ConfigEntry<float> CfgLightDimIntensity;
        internal static ConfigEntry<bool>  CfgOverrideFifthMonitor;
        internal static ConfigEntry<string> CfgAdditionalMonitorNameTokens;
        internal static ConfigEntry<float> CfgMaskManTailBuffer;
        internal static ConfigEntry<bool>  CfgVerboseLogging;
        internal static ConfigEntry<bool>  CfgDumpMonitorHierarchyOnOrbit;

        // Harmony
        private static Harmony _harmony;
        private static ConfigFile Config;

        // --------------------------------------------------------------------
        internal static void Initialize(ConfigFile config, ManualLogSource logger, Harmony harmony)
        {
            Log = logger;

            Config = config;
            BindConfig();

            if (!CfgEnabled.Value)
            {
                Log.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} - disabled via config.");
                return;
            }

            ResolveTakeoverVideoPath();

            if (!LoadBundle())
            {
                Log.LogError("Asset bundle failed to load - mod is disabled.");
                return;
            }

            _harmony = harmony ?? new Harmony(PLUGIN_GUID);
            _harmony.PatchAll(typeof(ShipSystems.Takeover.Patches));
            // #612 task 1.4 item (a): the host-authoritative rollover leg is a subscription to
            // the one QuotaRolloverFinalized event now, not a second postfix on
            // TimeOfDay.SetNewProfitQuota. Subscribed here, behind the same enable check the
            // patch was behind, so a config-disabled takeover still arms nothing.
            ShipSystems.Takeover.Patches.EnsureRolloverSubscription();
            // #610: register the monitor-wall surface, the ship-layout subscription and the
            // claim probe before anything can publish a layout change or scan the wall.
            ShipSystems.Takeover.TakeoverMonitorOwnership.EnsureRegistered();
            QuotaUnlockAnnouncement.EnsureInstance();
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #739: the forced-quota debug controller used to ride on EndingManager, which #713
            // deleted. Attach it to this plugin's own persistent host so Y4NGZDebugTools'
            // "Force Completed Quota / Takeover" control reaches a live instance again.
            QuotaTakeoverDebugController.EnsureAttached(MonitorTakeoverPlugin.CoroutineHost?.gameObject);
#endif

            Log.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loaded. " +
                        $"Video file: {(TakeoverVideoPath != null ? "OK" : "MISSING")}, " +
                        $"Video clip (fallback): {(TakeoverVideoClip != null ? "OK" : "MISSING")}, " +
                        $"Mumble clips: {MumbleClips.Length}/5, " +
                        $"MaskManVideo: {(MaskManVideoClip != null ? "OK" : "MISSING")}, " +
                        $"MaskManAudio: {(MaskManAudioClip != null ? "OK" : "MISSING")}");
        }

        // Config
        private static void BindConfig()
        {
            // #714: plain sections. G/A/P mirror the buckets in
            // MonitorTakeoverConfigSchemaMigration, which carries a pre-#714 profile's values
            // into exactly these (section, key) pairs before the first Bind below runs.
            const string G = MonitorTakeoverConfigSchemaMigration.GeneralSection;
            const string A = MonitorTakeoverConfigSchemaMigration.AudioSection;
            const string P = MonitorTakeoverConfigSchemaMigration.PresentationSection;

            CfgEnabled = Config.Bind(G, "Enabled", true,
                "Master toggle. Set to false to completely disable the mod.");

            CfgTakeoverDuration = Config.Bind(G, "Takeover Duration", 11.0f,
                "Total seconds the monitor takeover lasts before reverting to normal.");

            CfgOrbitDelay = Config.Bind(G, "Orbit Delay", 4.0f,
                "Seconds to wait after the ship enters orbit before starting the takeover.");

            CfgMumbleVolume = Config.Bind(A, "Mumble Volume", 0.6f,
                new ConfigDescription("Volume of Y4NGZ mumble audio (0 = silent, 1 = full).",
                    new AcceptableValueRange<float>(0f, 1f)));

            CfgTypewriterSpeed = Config.Bind(P, "Typewriter Speed", 0.026f,
                "Seconds between characters in the typewriter dialogue effect. " +
                "Lower = faster. Default 0.026 reveals the longest dialogue ~2s before sequence ends.");

            CfgHideHUD = Config.Bind(P, "Hide HUD", true,
                "Hide all player HUD elements during the takeover.");

            CfgDimLights = Config.Bind(P, "Dim Lights", true,
                "Dim ship interior lights during the takeover.");

            CfgLightDimIntensity = Config.Bind(P, "Light Dim Intensity", 0.12f,
                new ConfigDescription(
                    "How dim the ship lights go (fraction of original intensity). " +
                    "Lower = darker.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CfgOverrideFifthMonitor = Config.Bind(P, "Override Additional Monitors", true,
                "If true, any additional MeshRenderer under MonitorWall beyond the four known " +
                "monitor meshes is also overridden during takeover (catches small side screens).");

            CfgAdditionalMonitorNameTokens = Config.Bind(P, "Additional Monitor Name Tokens", string.Empty,
                "Optional comma-separated renderer or hierarchy-name filters for additional monitors. Empty overrides every detected extra monitor screen.");

            CfgMaskManTailBuffer = Config.Bind(P, "Mask Man Tail Buffer", 1.5f,
                new ConfigDescription(
                    "Seconds of held-on-monitor time after the Mask Man audio clip finishes before restoring.",
                    new AcceptableValueRange<float>(0f, 10f)));

            CfgVerboseLogging = Config.Bind(MonitorTakeoverConfigSchemaMigration.DiagnosticsSection, "Verbose Logging", false,
                "Log detailed bundle asset names during startup. Leave false for normal play.");

            CfgDumpMonitorHierarchyOnOrbit = Config.Bind(
                MonitorTakeoverConfigSchemaMigration.DiagnosticsSection,
                "Dump Monitor Hierarchy On Orbit",
                false,
                "Writes the full MonitorWall hierarchy to the BepInEx log whenever the ship reaches orbit. Dev-only; leave false for normal play.");

            // Note: the Mask Man one-shot flag is now persisted per-save via ES3
            // under the key "Y4NGZ_MaskManFired" (see Patches.cs and
            // TakeoverManager.cs). The previous global [State] config entry has
            // been removed so each save file gets its own first-time trigger.
        }

        // Asset bundle loading
        private static string FindBundlePath(string dir, params string[] fileNames)
        {
            foreach (var fileName in fileNames)
            {
                var path = Path.Combine(dir, fileName);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>
        /// Locates the loose takeover mp4 next to this assembly (#668).
        ///
        /// <para>Two candidates, in order: the plugin directory itself — where the
        /// csproj deploy step and <c>release\stage-all.ps1</c> both put it, beside
        /// monitortakeover.lethalbundle — and a <c>Video\</c> subfolder, which is
        /// what a player who tidied their profile by hand tends to create. Nothing
        /// else is probed: a wider search would find a stale copy in some other
        /// mod's folder and play it without ever saying which file it chose.</para>
        ///
        /// <para>Absent is not an error. The bundled VideoClip remains the
        /// fallback, so a profile that is missing the file behaves exactly as it
        /// did before this change rather than losing the takeover.</para>
        /// </summary>
        private static void ResolveTakeoverVideoPath()
        {
            TakeoverVideoPath = null;
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dir)) return;

                string[] candidates =
                {
                    Path.Combine(dir, DEFAULT_VIDEO_FILENAME),
                    Path.Combine(Path.Combine(dir, "Video"), DEFAULT_VIDEO_FILENAME)
                };

                foreach (string candidate in candidates)
                {
                    if (!File.Exists(candidate)) continue;
                    TakeoverVideoPath = Path.GetFullPath(candidate);
                    Log.LogInfo(
                        $"[LGUMonitorTakeover] Takeover video file: '{TakeoverVideoPath}'. " +
                        "This is played through VideoPlayer.url; the bundled clip is only the fallback.");
                    return;
                }

                Log.LogWarning(
                    $"[LGUMonitorTakeover] '{DEFAULT_VIDEO_FILENAME}' was not found in '{dir}' or its Video\\ "
                    + "subfolder. Falling back to the bundled VideoClip, which cannot decode on most profiles "
                    + "(the bundle was built without transcoding, so the clip only references the authoring "
                    + "machine's source file). Deploy the mp4 beside Y4NGZMonitorTakeover.dll.");
            }
            catch (Exception ex)
            {
                TakeoverVideoPath = null;
                Log.LogWarning($"[LGUMonitorTakeover] Takeover video path probe failed: {ex.Message}");
            }
        }

        private static AssetBundle FindPreloadedBundle(string probeAssetName)
        {
            foreach (AssetBundle bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (bundle == null) continue;
                try
                {
                    if (bundle.Contains(probeAssetName) || BundleHasMatchingAsset(bundle, probeAssetName)) return bundle;
                }
                catch
                {
                    // Best-effort fallback for legacy .lethalbundle deployments.
                }
            }
            return null;
        }

        private static T LoadFirstAsset<T>(AssetBundle bundle, params string[] assetNames)
            where T : UnityEngine.Object
        {
            foreach (var assetName in assetNames)
            {
                if (string.IsNullOrWhiteSpace(assetName)) continue;
                try
                {
                    var asset = bundle.LoadAsset<T>(assetName);
                    if (asset != null) return asset;
                }
                catch
                {
                    // Keep probing alternate bundle names.
                }
            }

            string[] bundleAssetNames = GetBundleAssetNames(bundle);
            foreach (var assetName in assetNames)
            {
                if (string.IsNullOrWhiteSpace(assetName)) continue;
                for (int i = 0; i < bundleAssetNames.Length; i++)
                {
                    string candidate = bundleAssetNames[i];
                    if (!AssetNameMatches(candidate, assetName)) continue;

                    try
                    {
                        var asset = bundle.LoadAsset<T>(candidate);
                        if (asset != null)
                        {
                            Log.LogInfo(
                                $"[LGUMonitorTakeover] Loaded {typeof(T).Name} '{asset.name}' via bundle asset '{candidate}' for key '{assetName}'.");
                            return asset;
                        }
                    }
                    catch
                    {
                        // Keep probing other matched candidates.
                    }
                }
            }

            return null;
        }

        private static bool BundleHasMatchingAsset(AssetBundle bundle, string assetName)
        {
            string[] bundleAssetNames = GetBundleAssetNames(bundle);
            for (int i = 0; i < bundleAssetNames.Length; i++)
            {
                if (AssetNameMatches(bundleAssetNames[i], assetName)) return true;
            }
            return false;
        }

        private static string[] GetBundleAssetNames(AssetBundle bundle)
        {
            try { return bundle?.GetAllAssetNames() ?? Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        private static bool AssetNameMatches(string bundleAssetName, string requestedName)
        {
            if (string.IsNullOrWhiteSpace(bundleAssetName) || string.IsNullOrWhiteSpace(requestedName))
                return false;

            string bundleNorm = NormalizeAssetName(bundleAssetName);
            string requestNorm = NormalizeAssetName(requestedName);
            if (bundleNorm.Equals(requestNorm, StringComparison.OrdinalIgnoreCase))
                return true;

            string bundleLeaf = GetAssetLeaf(bundleNorm);
            string requestLeaf = GetAssetLeaf(requestNorm);
            if (bundleLeaf.Equals(requestLeaf, StringComparison.OrdinalIgnoreCase))
                return true;

            string bundleStem = Path.GetFileNameWithoutExtension(bundleLeaf);
            string requestStem = Path.GetFileNameWithoutExtension(requestLeaf);
            if (bundleStem.Equals(requestStem, StringComparison.OrdinalIgnoreCase))
                return true;

            return bundleNorm.EndsWith("/" + requestLeaf, StringComparison.OrdinalIgnoreCase)
                || bundleNorm.EndsWith("/" + requestStem, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAssetName(string assetName)
        {
            return (assetName ?? string.Empty).Replace('\\', '/').Trim();
        }

        private static string GetAssetLeaf(string assetName)
        {
            string normalized = NormalizeAssetName(assetName);
            int slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        }

        private static bool LoadBundle()
        {
            try
            {
                string dir        = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                string bundlePath = FindBundlePath(dir, BUNDLE_FILENAME, LEGACY_BUNDLE_FILENAME);

                if (bundlePath == null)
                {
                    Log.LogError($"Bundle not found in {dir} (looked for {BUNDLE_FILENAME} and {LEGACY_BUNDLE_FILENAME}).");
                    // #614 task 3.1: this said "alongside Y4NGZCompany.dll", which stopped being
                    // true when the takeover left that assembly. FindBundlePath probes the
                    // directory of the EXECUTING assembly, which is this one, so a player who
                    // followed the old instruction would have put the bundle in a directory
                    // nothing here ever looks at. The directory is already printed on the line
                    // above; naming the assembly as well is what makes the two agree.
                    Log.LogError(
                        "Deploy monitortakeover.bundle or monitortakeover.lethalbundle alongside "
                        + "Y4NGZMonitorTakeover.dll, in the directory named above. It is NOT read "
                        + "from Y4NGZCompany's plugin folder any more.");
                    return false;
                }

                // #681: _bundle first. Now that the handle is held for the session,
                // a second LoadBundle must reuse it — LoadFromFile on an already
                // mounted bundle returns null, and the retry would report a corrupt
                // bundle for a bundle that is loaded and fine.
                AssetBundle bundle = _bundle
                    ?? FindPreloadedBundle("y4ngz_monitor_takeover")
                    ?? AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    Log.LogError("AssetBundle.LoadFromFile returned null (corrupt bundle?).");
                    return false;
                }

                // Diagnostic dump: every asset name in the bundle.
                // Unity sometimes lowercases / path-prefixes asset names
                // (e.g. "assets/modassets/monitortakeover/mask_man_video.mp4").
                // Print them all so the user can confirm the exact strings to
                // pass to LoadAsset<>() for new assets like Mask Man.
                if (CfgVerboseLogging.Value)
                {
                    try
                    {
                        var allNames = bundle.GetAllAssetNames();
                        Log.LogInfo($"[LGUMonitorTakeover] Bundle asset dump: {allNames.Length} entries.");
                        foreach (var n in allNames)
                            Log.LogInfo($"[LGUMonitorTakeover]   asset: {n}");
                    }
                    catch (Exception dumpEx)
                    {
                        Log.LogWarning($"[LGUMonitorTakeover] Bundle asset dump failed: {dumpEx.Message}");
                    }
                }

                // Video clip
                TakeoverVideoClip = LoadFirstAsset<VideoClip>(bundle, "y4ngz_monitor_takeover");
                if (TakeoverVideoClip == null)
                    Log.LogWarning("Could not load 'y4ngz_monitor_takeover' from bundle. " +
                                   "Monitors will go dark instead of showing video.");

                // Mumble audio
                var clips = new System.Collections.Generic.List<AudioClip>();
                for (int i = 1; i <= 5; i++)
                {
                    var clip = LoadFirstAsset<AudioClip>(bundle, $"mumble_0{i}");
                    if (clip != null)
                        clips.Add(clip);
                    else
                        Log.LogWarning($"Could not load 'mumble_0{i}' from bundle.");
                }
                MumbleClips = clips.ToArray();

                // Optional custom alarm clip (preferred over in-game alarms).
                AlarmClip = LoadFirstAsset<AudioClip>(bundle, "alarm_takeover");
                if (AlarmClip == null)
                    Log.LogInfo("Bundle has no 'alarm_takeover' clip - TakeoverManager will fall back to an in-game alarm. " +
                                "(TODO: ship a dedicated takeover alarm asset.)");

                // Mask Man variant assets (quota-3 one-shot). Null-tolerant -
                // missing clips just skip the takeover at runtime instead of
                // failing the whole mod.
                MaskManVideoClip = LoadFirstAsset<VideoClip>(bundle, "mask_man_video");
                if (MaskManVideoClip == null)
                    Log.LogWarning("Could not load 'mask_man_video' from bundle - Mask Man takeover will be skipped. " +
                                   "Verify the exact asset name from the bundle dump above.");

                MaskManAudioClip = LoadFirstAsset<AudioClip>(bundle, "mask_man_audio");
                if (MaskManAudioClip == null)
                    Log.LogWarning("Could not load 'mask_man_audio' from bundle - Mask Man takeover will be skipped. " +
                                   "Verify the exact asset name from the bundle dump above.");

                // #681. The bundle stays mounted; see the _bundle field comment for
                // why unloading it here silently broke every takeover clip. The
                // preload below is not the fix, it is the diagnosis: it forces the
                // FSB payload resident now, so a clip that genuinely cannot decode
                // says so once at load rather than as a wall of Unity errors in the
                // middle of the takeover.
                _bundle = bundle;
                PreloadBundledAudio();
                Log.LogInfo("Bundle loaded successfully (kept mounted so streamed audio clips stay readable).");
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError($"Exception loading bundle: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Forces every bundled takeover clip's sample data resident at load (#681).
        ///
        /// <para><see cref="AudioClip.LoadAudioData"/> pulls the FSB payload through
        /// the still-mounted bundle archive. For the clips authored as
        /// DecompressOnLoad or CompressedInMemory that is all they will ever need
        /// from the archive again; for a Streaming clip the mount itself is what
        /// keeps working, which is why <see cref="_bundle"/> is held either way.</para>
        ///
        /// <para>A failure here is reported, not thrown. The takeover degrades to a
        /// silent one exactly as it did before, but the reason lands in the log at
        /// startup — next to the clip's name — instead of arriving as
        /// "Failed reading FSB data" once per playback attempt with nothing in the
        /// message tying it to this mod.</para>
        /// </summary>
        private static void PreloadBundledAudio()
        {
            var clips = new System.Collections.Generic.List<AudioClip>(MumbleClips);
            clips.Add(AlarmClip);
            clips.Add(MaskManAudioClip);

            foreach (AudioClip clip in clips)
            {
                if (clip == null) continue;
                try
                {
                    // Already resident (or an in-memory clip): nothing to do.
                    if (clip.loadState == AudioDataLoadState.Loaded) continue;
                    if (!clip.LoadAudioData())
                    {
                        Log.LogWarning(
                            $"[LGUMonitorTakeover] Audio clip '{clip.name}' would not preload its sample data from the "
                            + "bundle. It will be silent. This normally means the bundle's .resource companion is "
                            + "missing or truncated - re-deploy monitortakeover.lethalbundle.");
                    }
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"[LGUMonitorTakeover] Preloading audio clip '{clip.name}' failed: {ex.Message}");
                }
            }
        }
    }
}



