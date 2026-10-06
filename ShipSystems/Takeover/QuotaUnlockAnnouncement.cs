// QuotaUnlockAnnouncement.cs — vanilla-styled unlock announcement shown on the
// large ship monitor(s) after a quota takeover fully ends. Host builds the
// display lines from the quota milestone config, broadcasts them, and every
// client renders locally once its own takeover has finished restoring.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core;
using Y4NGZCompany.Core.Compat;
// #611 task 1.3: no Ship Systems import. The ceremony asks Y4NGZCore what the ship's own
// monitor content is doing, takes its colours from the shared monitor palette, and uses
// the typewriter maths that moved into Y4NGZCore with it.
// MonitorRowCeremonyMath / MonitorRowTypewriterStep moved into Y4NGZCore in #611 task 1.3
// keeping this namespace, so this using resolves into the shared assembly, not Ship Systems.
using Y4NGZCompany.ShipSystems.MonitorRow;
using Y4NGZCore.Modules.ShipSystems;
using Y4NGZCompany.ShipSystems.Rendering;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    internal sealed class QuotaUnlockAnnouncement : MonoBehaviour
    {
        private const string MessageName = "Y4NGZCompany.QuotaUnlockAnnouncement.v1";
        private const string MonitorWallPath = "Environment/HangarShip/ShipModels2b/MonitorWall/Cube.001";
        private const string StingClipFileName = "digital_text_sequence_terminal_01.wav";
        private const int RenderWidth = 768;
        private const int RenderHeight = 432;
        private const int MaxVisibleLines = 10;
        private const float RevealDuration = 1.0f;
        private const float PendingPayloadTimeout = 30f;
        // #451: CCTV holds the primary-monitor scope for the whole duration of
        // monitor mode, not transiently, so "someone owns the wall — skip" lost the
        // reward presentation outright. A refused announcement is queued instead
        // and retried on this cadence until the scope frees up.
        private const float QueuedRetryInterval = 2f;
        private const float QueuedGiveUpSeconds = 900f;

        internal static QuotaUnlockAnnouncement Instance { get; private set; }

        // NGO can recreate CustomMessagingManager per session while the
        // NetworkManager singleton persists, so re-registration keys off the
        // messaging manager instance itself.
        private CustomMessagingManager _registeredMessagingManager;

        // Pending payload from the host, held until the local takeover ends.
        private bool _hasPending;
        private int _pendingQuota;
        private string[] _pendingLines = Array.Empty<string>();
        private float _pendingReceivedAt;

        // Announcement that was ready to play but could not claim the large
        // monitor (or arrived while another presentation was on screen). Held
        // until it either plays or is explicitly given up on — it is never
        // silently dropped (#451).
        private bool _hasQueued;
        private int _queuedQuota;
        private string[] _queuedLines = Array.Empty<string>();
        private bool _queuedDebugReplay;
        private float _queuedAt;
        private float _nextQueuedRetryAt;
        // The degraded broadcast is a one-shot consolation, not a loop: without
        // this the retry would reopen the ship-wide scope every few seconds for
        // as long as CCTV held the wall.
        private bool _queuedBroadcastPlayed;

        // Host-side one-shot: the last quota whose unlocks were announced,
        // persisted per save so it survives reloads and mid-save mod updates.
        private const string LastAnnouncedQuotaSaveKey = "Y4NGZ_UnlockAnnouncementLastQuota";
        private int _lastSentQuota = -1;
        private string _lastSentQuotaSaveName;
        private int _lastShownQuota = -1;
        private int _debugReplayQuota = -1;

        // Off-screen render rig.
        private GameObject _rigRoot;
        private Camera _rigCamera;
        private RenderTexture _renderTexture;
        private Canvas _canvas;
        private TextMeshProUGUI _headerText;
        private TextMeshProUGUI _bodyText;

        // Own the physical shared-material slot with an isolated clone. Saving
        // the whole material reference makes release exact and prevents
        // renderer.materials from mutating an unassigned transient instance.
        private struct SlotBinding
        {
            internal MeshRenderer Renderer;
            internal int SlotIndex;
            internal Material SavedMaterial;
            internal Material RuntimeMaterial;
        }

        private readonly List<SlotBinding> _bindings = new List<SlotBinding>();
        private GeneralImprovementsMonitorLease _generalImprovementsMonitorLease;
        private readonly List<(ManualCameraRenderer Renderer, bool WasEnabled)>
            _suspendedMonitorRenderers = new List<(ManualCameraRenderer, bool)>();
        private Coroutine _showCoroutine;
        // Degraded presentation: the ship-wide broadcast without the large-monitor
        // reward screen. Kept on its own handle so the Update() watchdog that
        // reasserts rig bindings never mistakes it for a bound announcement.
        private Coroutine _broadcastCoroutine;
        private bool _monitorScopeClaimed;
        private bool _presentationScopeClaimed;
        private Behaviour _suspendedLevelDescription;
        private bool _levelDescriptionWasEnabled;
        private AudioSource _audioSource;
        private AudioClip _tickClip;
        // Procedural per-step tick, generated once and reused across ceremonies.
        private AudioClip _ceremonyTickClip;
        // CRT restore-burst frames, generated per-ceremony and blitted straight
        // into the announcement RT; the burst clip is generated once like the tick.
        private Texture2D _crtWhite;
        private Texture2D _crtBlack;
        private Texture2D _crtNoise;
        private AudioClip _crtStaticClip;

        internal static void EnsureInstance()
        {
            if (Instance != null) return;
            var go = new GameObject("Y4NGZ_QuotaUnlockAnnouncement");
            go.AddComponent<QuotaUnlockAnnouncement>();
        }

        internal static void PrepareDebugReplay(int completedQuota)
        {
            if (completedQuota <= 0)
                return;

            EnsureInstance();
            if (Instance != null)
                Instance._debugReplayQuota = completedQuota;
        }

        internal static void ClearDebugReplay()
        {
            if (Instance != null)
                Instance._debugReplayQuota = -1;
        }

        /// <summary>
        /// A full-wall lease must capture GI's normal property block, never an
        /// announcement block it would resurrect on restore. This synchronous
        /// handoff runs immediately before TakeoverManager binds the wall; the
        /// Update watchdog remains the fallback for foreign takeover callers.
        /// </summary>
        internal static void AbortForExternalTakeover()
        {
            QuotaUnlockAnnouncement instance = Instance;
            if (instance == null ||
                (instance._showCoroutine == null && instance._broadcastCoroutine == null &&
                 instance._generalImprovementsMonitorLease == null &&
                 instance._bindings.Count == 0 &&
                 !instance._monitorScopeClaimed &&
                 !instance._presentationScopeClaimed))
                return;
            instance.AbortAndRestore("full monitor takeover started");
        }

        private bool ConsumeDebugReplay(int quota)
        {
            if (_debugReplayQuota != quota)
                return false;

            _debugReplayQuota = -1;
            return true;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            TakeoverManager.OnTakeoverEnded += OnTakeoverEnded;
            StartCoroutine(LoadTickAudio());
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            TakeoverManager.OnTakeoverEnded -= OnTakeoverEnded;
            AbortAndRestore("OnDestroy");
            if (_registeredMessagingManager != null)
            {
                try { _registeredMessagingManager.UnregisterNamedMessageHandler(MessageName); } catch { }
            }
            DestroyRig();
            Instance = null;
        }

        private void Update()
        {
            NetworkManager nm = NetworkManager.Singleton;
            CustomMessagingManager messaging = nm != null && nm.IsListening ? nm.CustomMessagingManager : null;
            if (messaging != null && !ReferenceEquals(messaging, _registeredMessagingManager))
            {
                if (_registeredMessagingManager != null)
                {
                    try { _registeredMessagingManager.UnregisterNamedMessageHandler(MessageName); } catch { }
                }
                messaging.RegisterNamedMessageHandler(MessageName, OnAnnouncementMessage);
                _registeredMessagingManager = messaging;
            }

            if (_hasPending && Time.realtimeSinceStartup - _pendingReceivedAt > PendingPayloadTimeout)
            {
                _hasPending = false;
                _pendingLines = Array.Empty<string>();
            }

            // A new takeover claiming the wall while we are showing means our
            // saved slot state is about to be stomped — get out of the way now.
            if ((_showCoroutine != null || _broadcastCoroutine != null) && TakeoverManager.IsActive)
                AbortAndRestore("takeover started during announcement");
            else if (_showCoroutine != null)
                ReassertBindings();

            TickQueuedAnnouncement();
        }

        // ── Queued announcements (#451) ───────────────────────────────────────

        /// <summary>
        /// Hold an announcement that could not play right now. Purely local: the
        /// host has already broadcast the payload, so every client queues (or
        /// shows) on its own schedule.
        /// </summary>
        private void QueueAnnouncement(int quota, string[] lines, bool debugReplay, string reason)
        {
            if (lines == null || lines.Length == 0)
                return;

            if (_hasQueued && _queuedQuota == quota)
            {
                // Already waiting for the same quota — don't restart its clock.
                return;
            }

            _hasQueued = true;
            _queuedQuota = quota;
            _queuedLines = lines;
            _queuedDebugReplay = debugReplay;
            _queuedAt = Time.realtimeSinceStartup;
            _nextQueuedRetryAt = Time.realtimeSinceStartup + QueuedRetryInterval;
            TakeoverBootstrap.Log?.LogInfo(
                $"[QuotaUnlockAnnouncement] Queued quota {quota} announcement ({reason}); " +
                $"retrying every {QueuedRetryInterval:0.#}s for up to {QueuedGiveUpSeconds:0}s.");
        }

        private void ClearQueuedAnnouncement()
        {
            _hasQueued = false;
            _queuedQuota = 0;
            _queuedLines = Array.Empty<string>();
            _queuedDebugReplay = false;
            _queuedBroadcastPlayed = false;
        }

        private void TickQueuedAnnouncement()
        {
            if (!_hasQueued || Time.realtimeSinceStartup < _nextQueuedRetryAt)
                return;

            _nextQueuedRetryAt = Time.realtimeSinceStartup + QueuedRetryInterval;

            if (Time.realtimeSinceStartup - _queuedAt > QueuedGiveUpSeconds)
            {
                TakeoverBootstrap.Log?.LogWarning(
                    $"[QuotaUnlockAnnouncement] Giving up on the queued quota {_queuedQuota} announcement " +
                    $"after {QueuedGiveUpSeconds:0}s: the large monitor never became available.");
                ClearQueuedAnnouncement();
                return;
            }

            if (_showCoroutine != null || _broadcastCoroutine != null || TakeoverManager.IsActive)
                return;

            if (ShipSystemsServices.IsPrimaryMonitorTakeoverActive &&
                !GeneralImprovementsMonitors.BetterMonitorsActive)
                return;

            int quota = _queuedQuota;
            string[] lines = _queuedLines;
            bool debugReplay = _queuedDebugReplay;
            ClearQueuedAnnouncement();
            TakeoverBootstrap.Log?.LogInfo(
                $"[QuotaUnlockAnnouncement] Dequeued quota {quota} announcement; the large monitor is free.");
            TryShow(quota, lines, debugReplay);
        }

        // ── Trigger ───────────────────────────────────────────────────────────

        private void OnTakeoverEnded()
        {
            try
            {
                if (QuotaProgressionRegistry.EnableUnlockAnnouncement != true) return;

                NetworkManager nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                {
                    int quota = TimeOfDay.Instance != null ? Math.Max(0, TimeOfDay.Instance.timesFulfilledQuota) : 0;
                    bool debugReplay = ConsumeDebugReplay(quota);
                    if (!QuotaUnlockAnnouncementPolicy.ShouldDispatch(
                            quota, GetLastAnnouncedQuota(), debugReplay))
                        return;
                    string[] lines = BuildUnlockLines(quota);
                    if (lines.Length == 0) return;
                    if (QuotaUnlockAnnouncementPolicy.ShouldPersist(debugReplay))
                        SetLastAnnouncedQuota(quota);
                    SendAnnouncement(quota, lines);
                    TryShow(quota, lines, debugReplay);
                    return;
                }

                // Client: the takeover just ended locally — show whatever payload
                // arrived while it was running.
                if (_hasPending)
                {
                    _hasPending = false;
                    string[] lines = _pendingLines;
                    _pendingLines = Array.Empty<string>();
                    bool debugReplay = ConsumeDebugReplay(_pendingQuota);
                    TryShow(_pendingQuota, lines, debugReplay);
                }
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] OnTakeoverEnded failed: {ex.Message}");
            }
        }

        // ── Networking ────────────────────────────────────────────────────────

        private void SendAnnouncement(int quota, string[] lines)
        {
            CustomMessagingManager manager = NetworkManager.Singleton?.CustomMessagingManager;
            if (manager == null) return;
            string joined = string.Join("\n", lines);
            if (joined.Length > 1000) joined = joined.Substring(0, 1000);
            using (var writer = new FastBufferWriter(6144, Allocator.Temp))
            {
                writer.WriteValueSafe(quota);
                FixedString4096Bytes payload = joined;
                writer.WriteValueSafe(payload);
                manager.SendNamedMessageToAll(MessageName, writer);
            }
        }

        // Per-save one-shot tracking through ES3.
        private int GetLastAnnouncedQuota()
        {
            string saveName = GameNetworkManager.Instance?.currentSaveFileName;
            if (string.IsNullOrEmpty(saveName)) return _lastSentQuota;
            if (_lastSentQuotaSaveName != saveName)
            {
                _lastSentQuotaSaveName = saveName;
                try { _lastSentQuota = ES3.Load<int>(LastAnnouncedQuotaSaveKey, saveName, -1); }
                catch (Exception ex)
                {
                    _lastSentQuota = -1;
                    TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] Failed loading last announced quota: {ex.Message}");
                }
            }
            return _lastSentQuota;
        }

        private void SetLastAnnouncedQuota(int quota)
        {
            _lastSentQuota = quota;
            string saveName = GameNetworkManager.Instance?.currentSaveFileName;
            if (string.IsNullOrEmpty(saveName)) return;
            _lastSentQuotaSaveName = saveName;
            try { ES3.Save<int>(LastAnnouncedQuotaSaveKey, quota, saveName); }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] Failed persisting last announced quota: {ex.Message}");
            }
        }

        private void OnAnnouncementMessage(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId) return;
            reader.ReadValueSafe(out int quota);
            reader.ReadValueSafe(out FixedString4096Bytes payload);
            if (NetworkManager.Singleton?.IsServer == true) return;
            if (QuotaProgressionRegistry.EnableUnlockAnnouncement != true) return;

            string[] lines = payload.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            if (TakeoverManager.IsActive)
            {
                // The takeover is still running here — hold the payload and show
                // it from the OnTakeoverEnded handler.
                _hasPending = true;
                _pendingQuota = quota;
                _pendingLines = lines;
                _pendingReceivedAt = Time.realtimeSinceStartup;
                return;
            }

            TryShow(quota, lines, ConsumeDebugReplay(quota));
        }

        // ── Line building (host) ──────────────────────────────────────────────

        private static string[] BuildUnlockLines(int quota)
        {
            var lines = new List<string>();
            QuotaProgressionRegistry.QuotaMilestoneSummary summary = QuotaProgressionRegistry.GetMilestoneSummary(quota);
            var constellations = new List<string>();
            var seenConstellations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (summary != null && summary.Enabled)
            {
                foreach (string name in summary.Contracts) lines.Add("CONTRACT · " + DisplayName(name));
                foreach (string name in summary.ShipUpgrades) lines.Add("SHIP UPGRADE · " + DisplayName(name));
                foreach (string name in summary.Constellations)
                {
                    if (seenConstellations.Add(name)) constellations.Add(name);
                }
            }

            // LethalMoonUnlocks fallback rules: constellations newly unlocked at
            // exactly this quota (not covered by the Company config list).
            try
            {
                // The source is cumulative by contract, so the "newly unlocked at this quota"
                // set is the difference between two reads. Asking for a delta instead would
                // push this subtraction into the provider, which does not know which quota is
                // being announced.
                var before = new HashSet<string>(
                    Y4NGZCore.Modules.Quota.ExternalQuotaUnlocks.GetUnlockedAtQuota(
                        Y4NGZCore.Modules.Quota.QuotaUnlockCategories.Constellation, quota - 1));
                foreach (string name in Y4NGZCore.Modules.Quota.ExternalQuotaUnlocks.GetUnlockedAtQuota(
                             Y4NGZCore.Modules.Quota.QuotaUnlockCategories.Constellation, quota))
                {
                    if (!before.Contains(name) && seenConstellations.Add(name)) constellations.Add(name);
                }
            }
            catch { }

            foreach (string name in constellations) lines.Add("CONSTELLATION · " + DisplayName(name));

            if (summary != null && summary.Enabled)
            {
                // #862: moons read as the terminal shows them, minus vanilla's numeric prefix.
                foreach (string name in summary.Moons) lines.Add("MOON · " + DisplayName(QuotaProgressionRegistry.MoonKey(name)));
                foreach (string name in summary.Suits) lines.Add("SUIT · " + DisplayName(name));
                foreach (string name in summary.StoreItems) lines.Add("STORE · " + DisplayName(name));
                if (summary.TokensPerPlayer > 0) lines.Add($"+{summary.TokensPerPlayer} TOKENS PER PLAYER");
                if (summary.GroupCredits > 0) lines.Add($"+{summary.GroupCredits} CREDITS");
                if (summary.ShipFuel > 0f && ShipSystemsServices.IsFuelSystemEnabled) lines.Add($"+{summary.ShipFuel:0} FUEL");
            }

            return lines.ToArray();
        }

        // "CCTVTerminal" -> "CCTV TERMINAL"; "Pro-flashlight" -> "PRO-FLASHLIGHT".
        private static string DisplayName(string raw)
        {
            raw = (raw ?? string.Empty).Trim();
            var chars = new List<char>(raw.Length + 8);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (i > 0 && char.IsUpper(c) && char.IsLower(raw[i - 1])) chars.Add(' ');
                chars.Add(c);
            }
            return new string(chars.ToArray()).ToUpperInvariant();
        }

        // ── Ship-wide presentation scope ──────────────────────────────────────

        private bool ClaimPresentationScope(int quota)
        {
            try
            {
                ShipSystemsServices.SetQuotaPresentationActive(true, quota);
                _presentationScopeClaimed = true;
                return true;
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log?.LogWarning(
                    $"[QuotaUnlockAnnouncement] Ship-wide presentation scope failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The reward screen needs the large monitor, but the ship-wide broadcast
        /// does not: the small-monitor surfaces are a separate mesh that CCTV never
        /// claims. When the large monitor is unavailable this still gives the crew
        /// the quota-met broadcast, while the full announcement waits in the queue
        /// for the scope to release (#451).
        /// </summary>
        private IEnumerator BroadcastOnlySequence(int quota)
        {
            if (!ClaimPresentationScope(quota))
            {
                _broadcastCoroutine = null;
                yield break;
            }

            float seconds = Mathf.Clamp(QuotaProgressionRegistry.UnlockAnnouncementSeconds, 2f, 15f);
            TakeoverBootstrap.Log?.LogInfo(
                $"[QuotaUnlockAnnouncement] Large monitor unavailable; running the ship-wide quota {quota} " +
                $"broadcast alone for {seconds:0.#}s.");

            float started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < seconds && !TakeoverManager.IsActive)
                yield return null;

            RestoreAll();
            _broadcastCoroutine = null;
        }

        // ── Display ───────────────────────────────────────────────────────────

        private void TryShow(int quota, string[] lines, bool debugReplay = false)
        {
            if (lines == null || lines.Length == 0) return;
            if (QuotaUnlockAnnouncementPolicy.ShouldSuppressPresentation(
                    quota, _lastShownQuota, debugReplay))
            {
                TakeoverBootstrap.Log?.LogInfo(
                    $"[QuotaUnlockAnnouncement] Skipping quota {quota} announcement: policy suppressed " +
                    $"(lastShown={_lastShownQuota}, debugReplay={debugReplay}).");
                return;
            }
            if (_showCoroutine != null || _broadcastCoroutine != null)
            {
                QueueAnnouncement(quota, lines, debugReplay, "another quota presentation is on screen");
                return;
            }
            if (TakeoverManager.IsActive)
            {
                QueueAnnouncement(quota, lines, debugReplay, "a monitor takeover is running");
                return;
            }
            // This quota is going on screen now; nothing is left to wait for.
            if (_hasQueued && _queuedQuota == quota)
                ClearQueuedAnnouncement();
            _showCoroutine = StartCoroutine(ShowSequence(quota, lines, debugReplay));
        }

        private IEnumerator ShowSequence(int quota, string[] lines, bool debugReplay = false)
        {
            bool ready = false;
            bool primaryScopeHeld = false;
            try
            {
                EnsureRig();
                ready = BindMonitors(out primaryScopeHeld);
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] Setup failed: {ex.Message}");
            }

            if (!ready)
            {
                RestoreAll();
                _showCoroutine = null;

                // #451: the large monitor being owned by someone else is not a
                // reason to lose the reward. Queue the full announcement for when
                // the scope frees up, and run the ship-wide broadcast now — CCTV
                // only owns Cube.001, so every other claimed surface is ours.
                if (primaryScopeHeld)
                {
                    bool alreadyBroadcast = _hasQueued && _queuedQuota == quota && _queuedBroadcastPlayed;
                    QueueAnnouncement(quota, lines, debugReplay, "primary monitor scope held by another system");
                    if (!alreadyBroadcast)
                    {
                        _queuedBroadcastPlayed = true;
                        _broadcastCoroutine = StartCoroutine(BroadcastOnlySequence(quota));
                    }
                }
                yield break;
            }

            // Mark shown only once the bind succeeded, so a transient bind
            // failure doesn't permanently swallow this quota's announcement.
            _lastShownQuota = quota;

            // The reward stays on this monitor, but the rest of the ship joins
            // it: every claim-owned surface holds a Company broadcast and none
            // of them resume normal content before this scope releases.
            ClaimPresentationScope(quota);

            if (lines.Length > MaxVisibleLines)
            {
                int hidden = lines.Length - (MaxVisibleLines - 1);
                lines = lines.Take(MaxVisibleLines - 1).Concat(new[] { $"+{hidden} MORE" }).ToArray();
            }

            float totalSeconds = Mathf.Clamp(QuotaProgressionRegistry.UnlockAnnouncementSeconds, 2f, 15f);
            bool ceremony = QuotaProgressionRegistry.RewardsCeremonyEnabled;
            float started = Time.realtimeSinceStartup;

            try
            {
                _headerText.text = $"QUOTA {quota} · NEW AUTHORIZATIONS";
                _bodyText.text = string.Empty;
                RenderRig();
                PlayAppearSting();
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] Header render failed: {ex.Message}");
            }

            if (!ceremony)
            {
                // Ceremony disabled: whole-line reveals over RevealDuration and
                // a hold measured from sequence start, unchanged.
                float lineInterval = RevealDuration / Mathf.Max(1, lines.Length);
                for (int i = 0; i < lines.Length; i++)
                {
                    yield return new WaitForSecondsRealtime(lineInterval);
                    try
                    {
                        _bodyText.text = string.Join("\n", lines.Take(i + 1));
                        RenderRig();
                        PlayLineTick();
                    }
                    catch (Exception ex)
                    {
                        TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] Line render failed: {ex.Message}");
                    }
                }
            }
            else
            {
                // Batched schedule: the render-rate floor is a hard bound —
                // every RenderRig() is a manual HDRP camera pass, so no config
                // value may produce more renders than the floor allows.
                MonitorRowTypewriterStep[] steps = MonitorRowCeremonyMath.BuildTypewriterSchedule(
                    lines,
                    QuotaProgressionRegistry.CeremonyTypewriterSecondsPerChar,
                    MonitorRowCeremonyMath.MinRenderStepSeconds,
                    MonitorRowCeremonyMath.MaxTypingSecondsDefault);

                // Completed lines stay in the builder as a fixed prefix; each
                // step resets to it and appends the current line's visible
                // slice, so the composed string is the only per-step allocation.
                var body = new StringBuilder(256);
                int completedLength = 0;
                for (int i = 0; i < steps.Length; i++)
                {
                    // A takeover claiming the wall stomps the saved slot state;
                    // stop rendering now rather than wait a frame for the
                    // Update() watchdog to abort.
                    if (TakeoverManager.IsActive) break;

                    MonitorRowTypewriterStep step = steps[i];
                    string line = lines[step.LineIndex];
                    try
                    {
                        body.Length = completedLength;
                        body.Append(line, 0, step.VisibleChars);
                        _bodyText.text = body.ToString();
                        RenderRig();
                        PlayCeremonyTypeTick();
                    }
                    catch (Exception ex)
                    {
                        TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] Ceremony render failed: {ex.Message}");
                    }

                    if (step.DelaySeconds > 0f)
                        yield return new WaitForSecondsRealtime(step.DelaySeconds);

                    if (step.LineCompleted)
                    {
                        body.Length = completedLength;
                        body.Append(line).Append('\n');
                        completedLength = body.Length;
                        if (QuotaProgressionRegistry.CeremonyLineConfirmSting)
                            PlayLineTick();
                        if (step.LineIndex < lines.Length - 1)
                            yield return new WaitForSecondsRealtime(0.15f);
                    }
                }

                // The hold is a dwell on the finished screen: it starts when
                // typing ends, not when the sequence began.
                started = Time.realtimeSinceStartup;
            }

            while (Time.realtimeSinceStartup - started < totalSeconds && !TakeoverManager.IsActive)
                yield return null;

            // A takeover arriving during the hold brings its own CRT effect —
            // the dissolve belongs only to the quiet path back to normal views.
            if (ceremony &&
                QuotaProgressionRegistry.CeremonyRestoreStatic &&
                !TakeoverManager.IsActive)
            {
                yield return CrtRestoreBurst();
            }

            RestoreAll();
            _showCoroutine = null;
        }

        // Brief CRT channel-change on the announcement RT before RestoreAll
        // returns the monitors to their normal views. Frames are blitted
        // straight into the RT both monitors already sample — no rig-camera
        // pass — so the whole dissolve costs three blits on pooled textures.
        // Takeover cadence (white flash → snow → tube-off black), compressed.
        private IEnumerator CrtRestoreBurst()
        {
            bool ready = false;
            try
            {
                if (_crtWhite == null) _crtWhite = CRTStatic.CreateSolidTexture(Color.white);
                if (_crtBlack == null) _crtBlack = CRTStatic.CreateSolidTexture(Color.black);
                if (_crtNoise == null) _crtNoise = CRTStatic.CreateNoiseTexture(256, 256);
                if (_crtStaticClip == null) _crtStaticClip = CRTStatic.CreateBurstClip();
                ready = _renderTexture != null && _renderTexture.IsCreated();
                if (ready && _audioSource != null)
                    _audioSource.PlayOneShot(_crtStaticClip, 0.5f);
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] CRT burst setup failed: {ex.Message}");
            }
            if (!ready) yield break;

            if (!TryBlitCrtFrame(_crtWhite)) yield break;
            yield return new WaitForSecondsRealtime(0.05f);
            if (TakeoverManager.IsActive) yield break;
            if (!TryBlitCrtFrame(_crtNoise)) yield break;
            yield return new WaitForSecondsRealtime(0.2f);
            if (TakeoverManager.IsActive) yield break;
            if (!TryBlitCrtFrame(_crtBlack)) yield break;
            yield return new WaitForSecondsRealtime(0.12f);
        }

        private bool TryBlitCrtFrame(Texture2D frame)
        {
            if (frame == null || _renderTexture == null || !_renderTexture.IsCreated())
                return false;
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(frame, _renderTexture);
                return true;
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] CRT frame blit failed: {ex.Message}");
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        // The three frame textures are per-ceremony state: pooled for the
        // burst's blits, then released with the presentation so nothing
        // outlives the announcement. Each ceremony regenerates the snow, so
        // the pattern also differs run to run.
        private void ReleaseCrtFrames()
        {
            if (_crtWhite != null) { Destroy(_crtWhite); _crtWhite = null; }
            if (_crtBlack != null) { Destroy(_crtBlack); _crtBlack = null; }
            if (_crtNoise != null) { Destroy(_crtNoise); _crtNoise = null; }
        }

        private void EnsureRig()
        {
            if (_rigRoot != null &&
                _rigCamera != null &&
                _canvas != null &&
                _headerText != null &&
                _bodyText != null &&
                _renderTexture != null &&
                _renderTexture.IsCreated())
            {
                return;
            }

            DestroyRig();
            _rigRoot = new GameObject("Y4NGZ_UnlockAnnouncementRig");
            DontDestroyOnLoad(_rigRoot);
            _rigRoot.transform.position = new Vector3(
                OffscreenRigParking.SlotX(OffscreenRigSlot.QuotaAnnouncement),
                OffscreenRigParking.ParkingHeight,
                0f);
            if (!OffscreenRigParking.Fits(RenderWidth * 0.5f, RenderHeight * 0.5f))
            {
                TakeoverBootstrap.Log?.LogWarning(
                    "[QuotaUnlockAnnouncement] Rig exceeds its parking slot; " +
                    "it may be captured by a neighbouring rig's camera.");
            }

            _renderTexture = new RenderTexture(RenderWidth, RenderHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "Y4NGZ_UnlockAnnouncementRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                antiAliasing = 1
            };
            if (!_renderTexture.Create())
            {
                DestroyRig();
                throw new InvalidOperationException("Quota announcement RenderTexture.Create() failed.");
            }

            var cameraGo = new GameObject("AnnouncementCamera");
            cameraGo.transform.SetParent(_rigRoot.transform, false);
            cameraGo.transform.localPosition = new Vector3(0f, 0f, -10f);
            _rigCamera = cameraGo.AddComponent<Camera>();
            _rigCamera.clearFlags = CameraClearFlags.SolidColor;
            _rigCamera.backgroundColor = MonitorPalette.MonitorBackground;
            _rigCamera.orthographic = true;
            _rigCamera.orthographicSize = RenderHeight * 0.5f;
            _rigCamera.aspect = RenderWidth / (float)RenderHeight;
            _rigCamera.nearClipPlane = 0.1f;
            _rigCamera.farClipPlane = 30f;
            _rigCamera.cullingMask = 1 << 5;
            _rigCamera.targetTexture = _renderTexture;
            _rigCamera.enabled = false;

            // Manually driven (enabled = false, rendered via explicit Camera.Render()
            // calls). Under HDRP a camera without HDAdditionalCameraData cannot resolve
            // into its assigned RenderTexture and instead stomps the backbuffer with its
            // clear color. Mirrors ShipTurretController.Core.cs's manual-camera pattern.
            HDAdditionalCameraData hdrp = cameraGo.AddComponent<HDAdditionalCameraData>();
            hdrp.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hdrp.backgroundColorHDR = _rigCamera.backgroundColor;
            hdrp.volumeLayerMask = 0;
            Y4NGZCompany.ShipSystems.Rendering.CameraRenderProfile.ApplyProfile(
                _rigCamera, Y4NGZCompany.ShipSystems.Rendering.CameraRenderRole.Unlit);

            var canvasGo = new GameObject("AnnouncementCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(_rigRoot.transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.worldCamera = _rigCamera;
            RectTransform canvasRt = canvasGo.GetComponent<RectTransform>();
            canvasRt.sizeDelta = new Vector2(RenderWidth, RenderHeight);
            Image background = canvasGo.AddComponent<Image>();
            background.color = MonitorPalette.MonitorBackground;
            background.raycastTarget = false;

            _headerText = CreateText(canvasRt, "Header", 40f, TextAlignmentOptions.Center, MonitorPalette.MonitorLabel);
            _headerText.fontStyle = FontStyles.Bold;
            SetOffsets(_headerText.rectTransform, new Vector2(24f, RenderHeight - 84f), new Vector2(-24f, -20f));

            _bodyText = CreateText(canvasRt, "Body", 30f, TextAlignmentOptions.Top, MonitorPalette.UiAccent);
            _bodyText.lineSpacing = 8f;
            SetOffsets(_bodyText.rectTransform, new Vector2(48f, 20f), new Vector2(-48f, -96f));

            SetLayerRecursive(_rigRoot, 5);
            _canvas.enabled = false;

            _audioSource = _rigRoot.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f;
        }

        private void DestroyRig()
        {
            ReleaseCrtFrames();
            if (_rigCamera != null)
            {
                _rigCamera.enabled = false;
                _rigCamera.targetTexture = null;
            }
            if (_renderTexture != null)
            {
                if (_renderTexture.IsCreated())
                    _renderTexture.Release();
                Destroy(_renderTexture);
            }
            if (_rigRoot != null)
                Destroy(_rigRoot);
            _rigRoot = null;
            _rigCamera = null;
            _renderTexture = null;
            _canvas = null;
            _headerText = null;
            _bodyText = null;
            _audioSource = null;
        }

        private static TextMeshProUGUI CreateText(Transform parent, string name, float size, TextAlignmentOptions alignment, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.alignment = alignment;
            text.enableWordWrapping = false;
            text.richText = false;
            text.color = color;
            text.raycastTarget = false;
            AssignVanillaFont(text);
            return text;
        }

        private static void AssignVanillaFont(TextMeshProUGUI text)
        {
            TextMeshProUGUI levelText = StartOfRound.Instance != null ? StartOfRound.Instance.screenLevelDescription : null;
            if (levelText != null && levelText.font != null)
            {
                text.font = levelText.font;
                if (levelText.fontSharedMaterial != null)
                    text.fontSharedMaterial = levelText.fontSharedMaterial;
                return;
            }
            if (HUDManager.Instance != null && HUDManager.Instance.clockNumber != null && HUDManager.Instance.clockNumber.font != null)
                text.font = HUDManager.Instance.clockNumber.font;
        }

        private static void SetOffsets(RectTransform rt, Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursive(child.gameObject, layer);
        }


        private void RenderRig()
        {
            if (_rigCamera == null ||
                _canvas == null ||
                _headerText == null ||
                _bodyText == null ||
                _renderTexture == null ||
                !_renderTexture.IsCreated())
            {
                throw new InvalidOperationException("Quota announcement render rig is unavailable.");
            }

            RenderTexture previous = RenderTexture.active;
            try
            {
                _canvas.enabled = true;
                Canvas.ForceUpdateCanvases();
                _headerText.ForceMeshUpdate();
                _bodyText.ForceMeshUpdate();
                ReassertBindings();
                _rigCamera.Render();
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log?.LogWarning(
                    $"[QuotaUnlockAnnouncement] Offscreen render failed: {ex.Message}");
                throw;
            }
            finally
            {
                _canvas.enabled = false;
                RenderTexture.active = previous;
            }
        }

        // ── Monitor binding ───────────────────────────────────────────────────

        private bool BindMonitors(out bool primaryScopeHeld)
        {
            primaryScopeHeld = false;

            bool mirror = QuotaProgressionRegistry.AnnounceOnBothMonitors ||
                (QuotaProgressionRegistry.RewardsCeremonyEnabled &&
                 QuotaProgressionRegistry.CeremonyMirrorBothMonitors);

            // A full-wall takeover outranks this one-shot. Treat it as a held
            // scope so ShowSequence queues instead of consuming the quota.
            if (ShipSystemsServices.IsExternalMonitorTakeoverActive)
            {
                primaryScopeHeld = true;
                TakeoverBootstrap.Log?.LogInfo(
                    "[QuotaUnlockAnnouncement] Full monitor takeover active; deferring the large-monitor announcement.");
                return false;
            }

            // #610/#612: declare ownership of the announcement screens before binding anything,
            // and — new in task 1.4 — actually obey the answer.
            //
            // The claim used to be a bare statement whose return value was discarded, with a
            // comment saying so: "it does not paint, and it does not gate". An ownership
            // decision nobody reads is not an ownership decision, and that is why #335 was still
            // visible on screen after the arbiter landed.
            //
            // #613 task 2.1 made the branch below REACHABLE, and #614 task 3.1 made the ordering
            // around it correct. The note that stood here said this surface had exactly one
            // claimant and that the stand-down branch was unreachable; that was true when it was
            // written and is not now. CeremonyCameraMonitorBinding.TryHoldAnnouncementScreens
            // takes a Banner-priority lease (5, outranked by QuotaAnnouncement = 4), so the two
            // presentations really do contest this surface and the arbiter's answer governs what
            // is drawn, in both directions.
            //
            // What that made newly load-bearing: BindSlot, a few lines below, reads
            // renderer.sharedMaterials and saves whatever is in the slot as the material to put
            // back. If the ribbon had not yet handed the slot over, that "original" would be the
            // ribbon's clone. The ribbon only noticed on its own next slow tick, up to 1.5s later.
            // MonitorLeaseRequest.Displaced closes it: the arbiter raises the displaced owner's
            // stand-down from inside the Acquire that ClaimAnnouncementScreens performs, before
            // this method returns, so BindSlot always reads the vanilla material.
            //
            // Standing down sets primaryScopeHeld, matching the full-takeover branch above: the
            // sequence is queued for when the screen frees up, never silently consumed. A reward
            // the crew earned must not be spent on a screen they could not see.
            if (!TakeoverMonitorOwnership.ClaimAnnouncementScreens(
                    mirror ? "quota unlock announcement (mirrored)" : "quota unlock announcement"))
            {
                primaryScopeHeld = true;
                TakeoverBootstrap.Log?.LogInfo(
                    "[QuotaUnlockAnnouncement] Announcement screens are owned by a stronger "
                    + "claimant; deferring the large-monitor announcement.");
                return false;
            }

            // GI owns the underlying materials. Borrow only renderer-local
            // property blocks, then let the presentation scope make CCTV stand
            // down while the announcement is visible.
            if (GeneralImprovementsMonitors.BetterMonitorsActive)
            {
                _generalImprovementsMonitorLease?.Release();
                _generalImprovementsMonitorLease = new GeneralImprovementsMonitorLease(
                    mirror
                        ? GeneralImprovementsMonitorLease.SurfaceSet.AnnouncementMirrored
                        : GeneralImprovementsMonitorLease.SurfaceSet.AnnouncementPrimary);
                bool giBound = _generalImprovementsMonitorLease.Acquire(
                    _renderTexture,
                    out string leaseDetail);
                if (!giBound) primaryScopeHeld = true;
                TakeoverBootstrap.Log?.LogInfo(
                    $"[QuotaUnlockAnnouncement] {leaseDetail}; mirror={mirror}.");
                return giBound;
            }

            // Vanilla fallback: score and clone the Cube.001 slots. The GI
            // single-material path returned above without changing materials.

            MeshRenderer renderer = FindMonitorWallRenderer();
            if (renderer == null)
            {
                TakeoverBootstrap.Log?.LogInfo("[QuotaUnlockAnnouncement] MonitorWall Cube.001 not found; skipping announcement.");
                return false;
            }

            // The primary-monitor scope is shared with CCTV (via
            // ShipSystemsApi.Begin/EndPrimaryMonitorTakeover). If someone else
            // already holds it, don't fight over the wall — the caller queues the
            // announcement for when the scope releases rather than stomping their
            // claim or dropping the reward.
            if (ShipSystemsServices.IsPrimaryMonitorTakeoverActive)
            {
                primaryScopeHeld = true;
                TakeoverBootstrap.Log?.LogInfo("[QuotaUnlockAnnouncement] Primary monitor scope already claimed (CCTV?); deferring the large-monitor announcement.");
                return false;
            }


            // OBC's LateUpdate rewrites its slot's material at any time, so it
            // must be paused before the secondary slot is scored, not after.
            if (mirror)
                OpenBodyCamsCompat.SuspendForAnnouncement();

            int leftIndex = ResolveLeftMonitorMaterialIndex(renderer);
            int rightIndex = mirror
                ? ResolveSecondaryScreenIndex(renderer, leftIndex)
                : -1;
            bool bound = false;

            // Unconditional now: with no ship-systems monitor content installed the setter
            // is a documented no-op, and claiming the scope locally is still correct because
            // this ceremony is the thing on screen either way. The old null check meant that
            // with no controller the scope was never claimed and never released -- the flag and
            // the release path already tolerated both, so this is strictly less state.
            ShipSystemsServices.SetPrimaryMonitorTakeoverActive(true);
            _monitorScopeClaimed = true;

            // Material assignment alone is not ownership: vanilla
            // ManualCameraRenderer.Update rewrites this same slot and the
            // level-description graphic is an independent world-space layer.
            SuspendMonitorProducers(renderer, leftIndex, rightIndex);
            if (leftIndex >= 0) bound |= BindSlot(renderer, leftIndex);
            bool secondaryBound = false;
            if (rightIndex >= 0 && rightIndex != leftIndex)
                secondaryBound = BindSlot(renderer, rightIndex);
            bound |= secondaryBound;

            // No claim landed on the right monitor — give OBC back now instead
            // of freezing its feed for the whole announcement.
            if (!secondaryBound)
                OpenBodyCamsCompat.RestoreForAnnouncement();

            if (!bound)
            {
                TakeoverBootstrap.Log?.LogInfo("[QuotaUnlockAnnouncement] No bindable monitor slots resolved; skipping announcement.");
                return false;
            }
            return true;
        }

        private void SuspendMonitorProducers(
            MeshRenderer targetRenderer,
            int leftIndex,
            int rightIndex)
        {
            RestoreMonitorProducers();
            StartOfRound round = StartOfRound.Instance;
            if (round == null)
                return;

            try
            {
                ManualCameraRenderer mapScreen = round.mapScreen;
                SuspendMonitorProducer(mapScreen, targetRenderer, leftIndex, rightIndex);
                foreach (ManualCameraRenderer candidate in FindObjectsOfType<ManualCameraRenderer>())
                    SuspendMonitorProducer(candidate, targetRenderer, leftIndex, rightIndex);
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log?.LogWarning(
                    $"[QuotaUnlockAnnouncement] Monitor producer suspension failed: {ex.Message}");
            }

            _suspendedLevelDescription = round.screenLevelDescription;
            if (_suspendedLevelDescription != null)
            {
                _levelDescriptionWasEnabled = _suspendedLevelDescription.enabled;
                _suspendedLevelDescription.enabled = false;
            }
        }

        private void SuspendMonitorProducer(
            ManualCameraRenderer mapScreen,
            MeshRenderer targetRenderer,
            int leftIndex,
            int rightIndex)
        {
            if (mapScreen == null || mapScreen.mesh != targetRenderer ||
                (mapScreen.materialIndex != leftIndex && mapScreen.materialIndex != rightIndex))
            {
                return;
            }

            for (int i = 0; i < _suspendedMonitorRenderers.Count; i++)
            {
                if (_suspendedMonitorRenderers[i].Renderer == mapScreen)
                    return;
            }

            _suspendedMonitorRenderers.Add((mapScreen, mapScreen.enabled));
            mapScreen.enabled = false;
        }

        private void RestoreMonitorProducers()
        {
            for (int i = 0; i < _suspendedMonitorRenderers.Count; i++)
            {
                try
                {
                    var suspended = _suspendedMonitorRenderers[i];
                    if (suspended.Renderer != null)
                        suspended.Renderer.enabled = suspended.WasEnabled;
                }
                catch { }
            }
            _suspendedMonitorRenderers.Clear();

            if (_suspendedLevelDescription != null)
            {
                try { _suspendedLevelDescription.enabled = _levelDescriptionWasEnabled; }
                catch { }
            }
            _suspendedLevelDescription = null;
            _levelDescriptionWasEnabled = false;
        }

        private static MeshRenderer FindMonitorWallRenderer()
        {
            GameObject exact = GameObject.Find(MonitorWallPath);
            MeshRenderer renderer = exact != null ? exact.GetComponent<MeshRenderer>() : null;
            if (renderer != null) return renderer;

            Transform monitor = StartOfRound.Instance != null && StartOfRound.Instance.elevatorTransform != null
                ? StartOfRound.Instance.elevatorTransform.Find("ShipModels2b/MonitorWall/Cube.001")
                : null;
            return monitor != null ? monitor.GetComponent<MeshRenderer>() : null;
        }

        private static int ResolveLeftMonitorMaterialIndex(MeshRenderer renderer)
        {
            Material[] materials;
            try { materials = renderer.sharedMaterials; } catch { return -1; }

            int mapIndex = StartOfRound.Instance?.mapScreen != null && StartOfRound.Instance.mapScreen.mesh == renderer
                ? StartOfRound.Instance.mapScreen.materialIndex
                : -1;
            if (mapIndex >= 0 && mapIndex < materials.Length) return mapIndex;
            return materials.Length > 1 ? 1 : -1;
        }

        // Same scoring as CCTVVanillaMonitorDisplay.ResolveCubeSecondaryScreenIndex:
        // confident non-map Unlit screen slot, floor keeps the HDRP/Lit bezel out.
        private static int ResolveSecondaryScreenIndex(MeshRenderer renderer, int leftIndex)
        {
            Material[] materials;
            try { materials = renderer.sharedMaterials; } catch { return -1; }
            if (materials.Length < 3) return -1;

            int bestIndex = -1;
            int bestScore = 199;
            for (int i = 0; i < materials.Length; i++)
            {
                if (i == leftIndex) continue;
                Material material = materials[i];
                if (material == null) continue;

                string name = material.name ?? string.Empty;
                // OpenBodyCams owns any BodyCam-named slot; binding it would
                // fight OBC's own material rewrite.
                if (name.IndexOf("BodyCam", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                int score = 0;
                string shaderName = material.shader != null ? material.shader.name : string.Empty;
                if (shaderName.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) >= 0) score += 250;
                if (name.IndexOf("ShipScreen", StringComparison.OrdinalIgnoreCase) >= 0) score += 180;
                if (name.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0) score -= 120;
                if (material.HasProperty("_UnlitColorMap")) score += 120;
                if (material.mainTexture is RenderTexture) score += 70;
                if (shaderName.IndexOf("HDRP/Lit", StringComparison.OrdinalIgnoreCase) >= 0) score -= 180;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }
            return bestIndex;
        }

        private bool BindSlot(MeshRenderer renderer, int slotIndex)
        {
            Material[] materials;
            try { materials = renderer.sharedMaterials; } catch { return false; }
            if (materials == null || slotIndex < 0 || slotIndex >= materials.Length)
                return false;
            Material savedMaterial = materials[slotIndex];
            if (savedMaterial == null)
                return false;

            // Never paint an HDRP/Lit slot — that submesh carries the bezel.
            string shaderName = savedMaterial.shader != null ? savedMaterial.shader.name : string.Empty;
            if (shaderName.IndexOf("HDRP/Lit", StringComparison.Ordinal) >= 0)
                return false;

            Material runtimeMaterial = new Material(savedMaterial)
            {
                name = savedMaterial.name + "_Y4NGZQuotaAnnouncement"
            };
            ApplyTextureToMaterial(runtimeMaterial, _renderTexture);
            materials[slotIndex] = runtimeMaterial;
            try
            {
                renderer.sharedMaterials = materials;
            }
            catch
            {
                Destroy(runtimeMaterial);
                return false;
            }

            _bindings.Add(new SlotBinding
            {
                Renderer = renderer,
                SlotIndex = slotIndex,
                SavedMaterial = savedMaterial,
                RuntimeMaterial = runtimeMaterial
            });
            TakeoverBootstrap.Log?.LogInfo(
                $"[QuotaUnlockAnnouncement] Bound monitor materialIndex={slotIndex}; " +
                $"renderTextureCreated={_renderTexture != null && _renderTexture.IsCreated()}.");
            return true;
        }

        private static void ApplyTextureToMaterial(Material material, Texture texture)
        {
            if (material == null || texture == null)
                return;
            try { material.mainTexture = texture; } catch { }
            string[] properties =
            {
                "_BaseColorMap",
                "_UnlitColorMap",
                "_BaseMap",
                "_EmissiveColorMap"
            };
            for (int i = 0; i < properties.Length; i++)
            {
                if (material.HasProperty(properties[i]))
                    material.SetTexture(properties[i], texture);
            }
            if (material.HasProperty("_EmissiveColor"))
                material.SetColor("_EmissiveColor", Color.white * 1.5f);
            if (material.HasProperty("_EmissiveColorLDR"))
                material.SetColor("_EmissiveColorLDR", Color.white);
            material.EnableKeyword("_EMISSION");
            material.EnableKeyword("_EMISSIVE_COLOR_MAP");
        }

        private void ReassertBindings()
        {
            _generalImprovementsMonitorLease?.Maintain();
            for (int i = 0; i < _bindings.Count; i++)
            {
                SlotBinding binding = _bindings[i];
                if (binding.Renderer == null || binding.RuntimeMaterial == null)
                    continue;
                try
                {
                    Material[] materials = binding.Renderer.sharedMaterials;
                    if (binding.SlotIndex < 0 || binding.SlotIndex >= materials.Length)
                        continue;
                    if (materials[binding.SlotIndex] != binding.RuntimeMaterial)
                    {
                        materials[binding.SlotIndex] = binding.RuntimeMaterial;
                        binding.Renderer.sharedMaterials = materials;
                    }
                }
                catch (Exception ex)
                {
                    TakeoverBootstrap.Log?.LogWarning(
                        $"[QuotaUnlockAnnouncement] Slot reassert failed: {ex.Message}");
                }
            }

            for (int i = 0; i < _suspendedMonitorRenderers.Count; i++)
            {
                ManualCameraRenderer producer = _suspendedMonitorRenderers[i].Renderer;
                if (producer != null && producer.enabled)
                    producer.enabled = false;
            }
            if (_suspendedLevelDescription != null && _suspendedLevelDescription.enabled)
                _suspendedLevelDescription.enabled = false;
        }

        private void RestoreAll()
        {
            ReleaseCrtFrames();
            TakeoverMonitorOwnership.ReleaseAnnouncementScreens();
            _generalImprovementsMonitorLease?.Release();
            _generalImprovementsMonitorLease = null;
            foreach (SlotBinding binding in _bindings)
            {
                try
                {
                    if (binding.Renderer == null) continue;
                    Material[] materials = binding.Renderer.sharedMaterials;
                    if (materials == null || binding.SlotIndex < 0 || binding.SlotIndex >= materials.Length)
                        continue;
                    if (materials[binding.SlotIndex] == binding.RuntimeMaterial)
                    {
                        materials[binding.SlotIndex] = binding.SavedMaterial;
                        binding.Renderer.sharedMaterials = materials;
                    }
                }
                catch (Exception ex)
                {
                    TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] Slot restore failed: {ex.Message}");
                }
                finally
                {
                    if (binding.RuntimeMaterial != null)
                        Destroy(binding.RuntimeMaterial);
                }
            }
            _bindings.Clear();
            RestoreMonitorProducers();
            OpenBodyCamsCompat.RestoreForAnnouncement();

            if (_presentationScopeClaimed)
            {
                _presentationScopeClaimed = false;
                try { ShipSystemsServices.SetQuotaPresentationActive(false, 0); }
                catch (Exception ex) { TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] Presentation scope release failed: {ex.Message}"); }
            }

            if (_monitorScopeClaimed)
            {
                _monitorScopeClaimed = false;
                try { ShipSystemsServices.SetPrimaryMonitorTakeoverActive(false); }
                catch (Exception ex) { TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] Monitor scope release failed: {ex.Message}"); }
            }
        }

        private void AbortAndRestore(string reason)
        {
            if (_showCoroutine != null)
            {
                try { StopCoroutine(_showCoroutine); } catch { }
                _showCoroutine = null;
                TakeoverBootstrap.Log?.LogInfo($"[QuotaUnlockAnnouncement] Aborted ({reason}).");
            }
            if (_broadcastCoroutine != null)
            {
                try { StopCoroutine(_broadcastCoroutine); } catch { }
                _broadcastCoroutine = null;
                TakeoverBootstrap.Log?.LogInfo($"[QuotaUnlockAnnouncement] Broadcast-only presentation aborted ({reason}).");
            }
            RestoreAll();
        }

        // ── Audio (client-local) ──────────────────────────────────────────────

        private void PlayAppearSting()
        {
            HUDManager hud = HUDManager.Instance;
            if (hud == null || hud.UIAudio == null) return;
            AudioClip[] clips = hud.tipsSFX;
            if (clips != null && clips.Length > 0 && clips[0] != null)
                hud.UIAudio.PlayOneShot(clips[0], 0.5f);
            else if (hud.finishAddingToTotalSFX != null)
                hud.UIAudio.PlayOneShot(hud.finishAddingToTotalSFX, 0.5f);
        }

        private void PlayLineTick()
        {
            if (_audioSource == null || _tickClip == null) return;
            _audioSource.PlayOneShot(_tickClip, 0.35f);
        }

        private void PlayCeremonyTypeTick()
        {
            if (_audioSource == null) return;
            if (_ceremonyTickClip == null)
                _ceremonyTickClip = TypewriterAudio.CreateTick();
            _audioSource.PlayOneShot(_ceremonyTickClip, 0.4f);
        }

        private IEnumerator LoadTickAudio()
        {
            string path = ResolveTickAudioPath(out string[] probed);
            if (string.IsNullOrEmpty(path))
            {
                // #614 task 3.1: was a bare `yield break`. On a Monitor-Takeover-without-Contracted
                // profile the clip really was unresolvable — it only ever deployed into Contracted's
                // plugin directory — and the announcement lost its per-line tick with not one line
                // anywhere to say why. One warning, naming every path tried, so the next report of
                // "the ceremony is silent" is a two-minute diagnosis. Not an error: the ceremony is
                // fully playable without the sting, and PlayLineTick already no-ops on a null clip.
                TakeoverBootstrap.Log?.LogWarning(
                    $"[QuotaUnlockAnnouncement] Tick audio '{StingClipFileName}' not found; the "
                    + "reward ceremony will play without its per-line sting. Probed: "
                    + string.Join(" | ", probed));
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.WAV))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    TakeoverBootstrap.Log?.LogWarning($"[QuotaUnlockAnnouncement] Failed loading tick audio '{path}': {request.error}");
                    yield break;
                }
                _tickClip = DownloadHandlerAudioClip.GetContent(request);
                if (_tickClip != null) _tickClip.name = "Y4NGZ_UnlockAnnouncementTick";
            }
        }

        /// <summary>
        /// Where the tick sting is, or null. <paramref name="probed"/> always comes back populated
        /// so the caller's one warning can name what it looked at.
        ///
        /// <para><b>#614 task 3.1 rewrote the candidate list, and the first entry is the fix.</b>
        /// Every candidate here used to assume the clip lived in <b>Contracted's</b> plugin
        /// directory: the assembly-relative probe resolved to <c>Y4NGZCompany.dll</c>'s folder
        /// because that is where this code shipped, and the explicit fallback hard-coded
        /// <c>y4ngz-Y4NGZCompany</c>. Both assumptions died with the extraction — the
        /// assembly-relative probe now points at this package, which shipped no audio at all, and
        /// the hard-coded fallback misses a Gale-imported profile entirely, which names the
        /// directory <c>Y4NGZCompany</c> with no prefix. On a profile without Contracted the clip
        /// was simply unreachable.</para>
        ///
        /// <para>So the clip ships here now (see the <c>assets\audio\ui</c> Content item in
        /// <c>Y4NGZMonitorTakeover.csproj</c>) under <c>Audio\UI\</c>, and this looks there first.
        /// Contracted's copy is still probed afterwards, under <b>both</b> profile layout names —
        /// the same two-layout hazard every deploy target in this repository documents, and the
        /// shape <c>WasteDisposalIncineratorAudio.ResolveAudioPath</c> already uses. Keeping those
        /// fallbacks costs two <c>File.Exists</c> calls once per session and means a profile that
        /// somehow ends up with only Contracted's copy still gets its sting.</para>
        /// </summary>
        private static string ResolveTickAudioPath(out string[] probed)
        {
            string assemblyDir = Path.GetDirectoryName(typeof(QuotaUnlockAnnouncement).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                // This package's own payload, in whatever directory it was actually loaded from.
                Path.Combine(assemblyDir, "Audio", "UI", StingClipFileName),
                // Same package, hand-placed in the legacy folder name.
                Path.Combine(assemblyDir, "ContractAudio", "UI", StingClipFileName),
                // This package by name, for the case where the assembly location is unusable
                // (shadow-copied or single-file host). Gale layout first, hand-made second, the
                // same order every DeployTarget in this repository prefers.
                Path.Combine(Paths.PluginPath, "Y4NGZMonitorTakeover", "Audio", "UI", StingClipFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZMonitorTakeover", "Audio", "UI", StingClipFileName),
                // Contracted's copy, BOTH layouts. The second of these is the one that used to be
                // here alone.
                Path.Combine(Paths.PluginPath, "Y4NGZCompany", "ContractAudio", "UI", StingClipFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", "ContractAudio", "UI", StingClipFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "ContractAudio", "UI", StingClipFileName),
                Path.Combine(Paths.PluginPath, "ContractAudio", "UI", StingClipFileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ContractAudio", "UI", StingClipFileName)
            };

            probed = candidates;
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
