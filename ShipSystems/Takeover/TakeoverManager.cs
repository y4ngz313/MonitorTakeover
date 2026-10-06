// TakeoverManager.cs — LGUMonitorTakeover
//
// MonoBehaviour that runs the full Y4NGZ monitor takeover sequence.

using Y4NGZCore.Modules.Quota;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Experience.UITheme;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    internal class TakeoverManager : MonoBehaviour
    {
        // ── Singleton ──────────────────────────────────────────────────────────
        internal static TakeoverManager Instance { get; private set; }

        // ── Active flag + coroutine ────────────────────────────────────────────
        private Coroutine _takeoverCoroutine;
        private Coroutine _flashCoroutine;
        private bool _takeoverActiveState;

        /// <summary>
        /// #610: a property rather than a field, so the Core monitor-wall lease is taken and
        /// given back at exactly the moments the takeover considers itself running.
        ///
        /// <para>There are seven assignments to this across three sequences and two teardown
        /// paths. Pairing a <c>ClaimWall</c> with each <c>= true</c> and a <c>ReleaseWall</c>
        /// with each <c>= false</c> by hand would be seven chances to miss one, and a missed
        /// release is a wall the arbiter reports as owned forever — every later claimant
        /// deferred behind a takeover that ended. The flag is the truth; this is the flag.</para>
        ///
        /// <para>The setter is edge-triggered, so a re-assert of the same value costs nothing
        /// and cannot re-take a lease it already holds.</para>
        /// </summary>
        private bool _takeoverActive
        {
            get => _takeoverActiveState;
            set
            {
                if (_takeoverActiveState == value)
                    return;

                _takeoverActiveState = value;
                if (value)
                {
                    TakeoverMonitorOwnership.ClaimWall("monitor takeover");
                }
                else
                {
                    TakeoverMonitorOwnership.ReleaseWall();
                }
            }
        }
        private string[] _activeDialoguePool;
        private Action _onTakeoverFinished;

        // Raised after a takeover sequence has fully restored the ship and the
        // onFinished callback has run. Never raised from ForceRestore.
        public static event Action OnTakeoverEnded;

        // ── Saved monitor state ───────────────────────────────────────────────
        // Quota/deadline graphics we switched off for the duration of the
        // takeover. This is a list rather than four bools because the graphic
        // that is actually visible on the monitor face is not always the vanilla
        // StartOfRound reference — GeneralImprovements clones the quota/deadline
        // UI onto the physical wall ('ProfitQuotaBG5', 'DeadlineBG6', …) and
        // ShipMonitorStyler binds to those clones. We disable both the vanilla
        // originals and whatever clone is live, then restore each one's state.
        private readonly List<(Behaviour graphic, bool wasEnabled)> _disabledMonitorGraphics
            = new List<(Behaviour, bool)>();
        private bool _levelDescWasEnabled;

        private VideoClip _savedVideoClip;
        private bool _savedVideoLooping;
        private bool _savedVideoPlaying;

        private readonly List<(MeshRenderer renderer, Texture originalTexture)> _overriddenRenderers
            = new List<(MeshRenderer, Texture)>();
        private GeneralImprovementsMonitorLease _generalImprovementsMonitorLease;
        private Coroutine _generalImprovementsLeaseCoroutine;
        private static readonly WaitForSecondsRealtime GiLeaseRefresh = new WaitForSecondsRealtime(0.10f);

        // HDRP/Lit requires writing _BaseColorMap (not _MainTex) for the material
        // to display a texture. Save these originals per renderer so RestoreAll
        // can revert the HDRP-specific properties alongside mainTexture.
        // Per-slot saved state. Cube.001 has multiple material slots (OBC uses
        // index 2 for the body-cam screen, slot 0 is the bottom-left mapScreen,
        // and the frame submesh is yet another slot). mr.material only touches
        // slot 0 — we have to iterate mr.materials to cover them all.
        private readonly Dictionary<MeshRenderer,
            (bool overridden, Texture mainTex, Texture baseColor, Texture unlitColor, Texture emissive, Color emissiveColor, bool hadEmissionKW, bool hadEmissiveMapKW)[]>
            _savedHDRPState = new Dictionary<MeshRenderer,
                (bool, Texture, Texture, Texture, Texture, Color, bool, bool)[]>();

        // Top monitor RawImage overlays (one per Canvas) — kept for fallback
        private readonly List<RawImage> _topMonitorOverlayImages = new List<RawImage>();

        // Bottom-left: the RawImage that displays MapScreenVideo
        private RawImage _videoReelRawImage;
        private Texture  _savedVideoReelTexture;

        // ── Saved HUD state ───────────────────────────────────────────────────
        // We hide CanvasGroups instead of SetActive(false) to avoid triggering the
        // HUDAnimator, which causes the blurry transition on restore. #451: the
        // alpha/interactable/blocksRaycasts snapshot is no longer ours —
        // HudSuppressionRegistry owns it, refcounted, so the round-end report can
        // suppress the same groups without either side restoring the other's
        // zeroed state as if it were the original.
        private readonly List<CanvasGroup> _suppressedHUDGroups = new List<CanvasGroup>();

        // CanvasGroups we *added* to HUD nodes that didn't already have one so we
        // could hide them. Remembered so we can destroy them on restore rather than
        // leaving stray components behind.
        private readonly List<CanvasGroup> _addedHUDGroups = new List<CanvasGroup>();

        // Hotbar item-slot GameObjects we deactivated for the takeover. We can't
        // hide the whole hotbar canvas because it's the shared root with the
        // player viewport (would black out the screen). Instead we toggle each
        // individual icon/frame off; activeSelf is restored through the same
        // refcounted registry on cleanup.
        private readonly List<GameObject> _hiddenHotbarObjects = new List<GameObject>();

        // #451: a queued takeover can restart TakeoverSequence before RestoreHUD has run.
        // A live hide must never be re-snapshotted.
        private bool _hudHidden;

        // Bottom-left monitor RT blit target — a coroutine blits our RT into this
        // every frame so whatever game mesh reads MapScreenVideo shows Y4NGZ.
        private RenderTexture _mapScreenRT;
        private Coroutine _mapBlitCoroutine;

        // ── Saved light state ─────────────────────────────────────────────────
        private readonly List<(Light light, float origIntensity, Color origColor)> _savedLights
            = new List<(Light, float, Color)>();

        // ── Saved speaker volume (to suppress orbital PA announcement) ─────────
        private float _savedSpeakerVolume = 1f;
        private bool _speakerVolumeSaved = false;

        // ManualCameraRenderer components disabled during takeover
        private readonly List<(Behaviour component, bool wasEnabled)> _disabledComponents
            = new List<(Behaviour, bool)>();

        // ── Temp runtime objects (destroyed on cleanup) ───────────────────────
        private VideoPlayer _videoPlayer;
        private RenderTexture _renderTexture;
        // #669: true only while the VideoPlayer is driven by a user-configured
        // source (a loose MP4 or, on the media branch, a cached link). The
        // bundled clip is the fallback and must never be "fallen back" from —
        // the old code destroyed and rebuilt the bundled player on a slow
        // prepare, spending a second full timeout to arrive back where it was.
        private bool _usingConfiguredVideoSource;
        // #668: true while the DEFAULT takeover video is the loose mp4 rather
        // than the bundled clip. Read only by the watchdog's swap, so a fallback
        // from a user-configured source cannot re-select a loose file that has
        // already been tried and rejected.
        private bool _looseDefaultVideoInUse;
        // #661 HoldForWholeVideo. _mediaLengthPending is true while the
        // sequence is still waiting to learn the configured media's length;
        // _mediaLengthSeconds is the clamped hold once it is known, and stays 0
        // when the length never became available or the watchdog handed the
        // screen to the bundled clip. Declared unconditionally because the hold
        // loop reads them outside the custom-pass guard.
        private bool _mediaLengthPending;
        private double _mediaLengthSeconds;
        // Watchdog that swaps a configured source for the bundled clip if it
        // never decodes. It runs alongside the sequence and never blocks it.
        private Coroutine _videoFallbackCoroutine;
        // ── #662 per-monitor media ───────────────────────────────────────────
        // Deliberately NOT a refactor of _videoPlayer/_renderTexture into a list.
        // Player 0 IS _videoPlayer on _renderTexture, exactly as it has always
        // been, and every one of the ~40 sites that read those two fields —
        // SetupVideo, PrimeVideoForDisplay, AdoptConfiguredMediaLength, the
        // watchdog and its SwapToDefaultVideo, SignalLostStaticLoop, the map
        // blit, the diagnostics, RestoreAll — is untouched. Only the additive
        // extra players live here. Forwarding properties onto a list would have
        // produced the same behaviour through forty edited read sites; this
        // produces it through none, which is the smaller and safer diff and the
        // only version in which "toggle off" is provably today's code path.
        private sealed class TakeoverExtraMediaPlayer
        {
            internal int Index;
            internal VideoPlayer Player;
            internal RenderTexture Texture;
            internal Texture2D ImageTexture;
            internal string Source;
            internal Coroutine Watchdog;
            internal Coroutine MirrorLoop;
        }

        /// <summary>One assignable surface the takeover paints, remembered so a per-monitor
        /// plan can repaint it with a different player's texture after the discovery passes
        /// have run. Collected only while a plan is active.</summary>
        private readonly struct TakeoverMediaPaintSite
        {
            internal readonly TakeoverMediaSurfaceKey Key;
            internal readonly Material Material;
            internal readonly RawImage Overlay;

            internal TakeoverMediaPaintSite(TakeoverMediaSurfaceKey key, Material material, RawImage overlay)
            {
                Key = key;
                Material = material;
                Overlay = overlay;
            }
        }

        private readonly List<TakeoverExtraMediaPlayer> _extraMediaPlayers =
            new List<TakeoverExtraMediaPlayer>();
        private readonly List<TakeoverMediaPaintSite> _mediaPaintSites =
            new List<TakeoverMediaPaintSite>();
        private TakeoverMediaPlan _mediaPlan;
        // How many GI binding slots BuildGiSurfaceTextures fills. The lease binds the map plus
        // every API screen it finds, which on a stock GI wall is well under this; the surplus
        // entries are ignored by ApplyAll, and a wall larger than this simply falls back to the
        // primary for its tail bindings.
        private const int GiSurfaceTextureSlots = 32;

        /// <summary>Players actually open, primary included. Never larger than the cap.</summary>
        private int ActiveMediaPlayerCount => _extraMediaPlayers.Count + 1;

        private AudioSource _mumbleSource;
        private AudioSource _alarmSource;
        private AudioSource _droneSource;
        // The voice loop, so a Replace that fails after the voices were due can start it once
        // and never twice.
        private Coroutine _mumbleCoroutine;
        private GameObject _dialogueCanvasGO;
        private Texture2D _customImageTexture;
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
        private bool _useQuotaProgressionPayload;
        // #861: the takeover's one audio decision (configured alarm, voices, local soundtrack),
        // captured at the shared preparation deadline. Its clips are pinned until RestoreAll.
        private TakeoverAudioSnapshot _audio = TakeoverAudioSnapshot.Empty;
        // #861: true from the audio decision until the CRT transition or a restore. Nothing may
        // start, restart or recover a bed layer outside it, which is what keeps a late player
        // error from reviving audio after ForceRestore.
        private bool _audioBedOpen;
        // #861: the ongoing bed layers a Replace soundtrack suppressed when they came due. A
        // Replace that fails afterwards starts exactly these, once. The whine and the siren are
        // deliberately not tracked: they are the takeover's opening, and a late one is a fault.
        private bool _droneSuppressed;
        private bool _mumbleSuppressed;
        // #715 takeover soundtrack. The plan is resolved once at the head of TakeoverSequence
        // and is only ever non-None when _useQuotaProgressionPayload is true.
        private TakeoverSoundtrackPlan _soundtrackPlan;
        // #861: a plan is not playback. True once the source is confirmed: a local clip
        // playing, or a VideoPlayer prepared with an enabled audio track routed to our source.
        private bool _soundtrackConfirmed;
        private float _soundtrackStartedAt;
        private AudioSource _soundtrackSource;
        // Only for TakeoverSoundtrackKind.YoutubeCached: a second, hidden VideoPlayer whose
        // picture goes nowhere and whose audio track is the soundtrack. Unity has no
        // audio-only decoder for the AAC in the resolver's mp4, which is why this exists.
        private VideoPlayer _soundtrackPlayer;
        private RenderTexture _soundtrackDummyRT;
        private Coroutine _soundtrackCoroutine;
        private static readonly WaitForSeconds SoundtrackStartupPoll = new WaitForSeconds(0.25f);
        private static readonly WaitForSeconds SoundtrackPlayingPoll = new WaitForSeconds(0.5f);

        /// <summary>
        /// True while Replace is in force and the soundtrack has not failed: from the plan
        /// until playback is confirmed (so the bed is never heard and then cut), and for as long
        /// as it then plays. <see cref="FailSoundtrack"/> drops the plan, which ends this.
        /// </summary>
        private bool SoundtrackSilencesBed => _soundtrackPlan.Silences;
#endif

        // ── Dialogue pool ─────────────────────────────────────────────────────
        private static readonly string[] DialoguePool =
        {
            "First satisfactory output from this crew.\nClean execution. I had the over on you\nfinishing this one alive, paid out nicely.\nReinvesting immediately. I want you to know\nI don't celebrate. Celebration implies surprise.\nI'm not surprised. I'm confirmed.",
            "Consistent output. I respect the margins\non this run. Reminds me of when I wrote\nTech Talk — calculated, efficient, no wasted\nbars. Every word in that track had a function.\nEvery syllable was load-bearing. That's what\nI need from you. Load-bearing performance.\nNo filler. No ad-libs. Just execution.",
            "You're demonstrating real value. I moved\nsome numbers around on the back end,\nnothing you need to worry about. Just keep\nperforming. I restructured some of the internal\nmetrics to better reflect what I'm seeing out\nthere. Again, not your concern. Your concern\nis scrap. My concern is everything else.",
            "I was in New York closing something.\nCan't say what. Point is, I had action on\nfour different outcomes this week and every\nsingle one hit. You were one of them.\nCongratulations. I sat in a steakhouse in\nMurray Hill afterward and ate alone. Not\nbecause I had to. Because the booth across\nfrom me was occupied by a version of me\nwho didn't need to eat. We didn't speak.",
            "I want you to understand something.\nJuice WRLD recorded hundreds of songs that\nnever came out. Hundreds. That's not waste.\nThat's pipeline. You are pipeline. Keep moving.\nPeople called him prolific. I call him\noperationally sound. He understood throughput.\nMost artists don't. Most crews don't either.",
            "I've been running the numbers on crew\nsurvivability and I'll be honest, you're\noutperforming the model. I don't like when\nthings outperform the model. It means the\nmodel is wrong. I'll adjust. I've already\nstarted recalibrating. By tomorrow morning\nthe model will expect exactly what you\ndelivered today. You'll have to do better.\nThat's not a threat. That's forecasting.",
            "Wrote Bruises about a very specific feeling.\nNot pain exactly. More like the moment you\nrealize pain is just overhead. You account\nfor it. You build it into the forecast.\nQuota's done. People thought that song was\nabout a relationship. It was about depreciation.\nPhysical, emotional, structural. Everything\nbruises. The question is whether you write\nit off or capitalize it.",
            "I placed a parlay — crew survival, scrap\ntotal, and whether anyone would cry on the\nship. Two out of three hit. I won't say which\none missed. The book I use doesn't ask\nquestions. I like that about them. They\nunderstand that a bet is just a belief with\ncollateral. I believe in very specific things.\nYou should find that comforting.",
            "Took a red-eye to New York. Sat in a booth\nat a restaurant in Midtown for four hours.\nDidn't order anything. Waitress asked if I was\nokay. I said 'Yeah what.' She didn't ask again.\nGood trip overall. Flew back the same night.\nDidn't sleep. Wrote half a verse on the plane\nabout lunar rotation cycles. Scrapped it.\nToo honest.",
            "I'm going to be transparent with you\nbecause I think you've earned it. Magic Trik\nisn't about magic. It's about making people\nlook where you want them to look. That's all\nmanagement is. Misdirection with benefits.\nI learned that in New York. I learned it again\non Titan. Some lessons you have to learn in\ntwo places before they stick.",
            "I have a spreadsheet. You're on it.\nEveryone's on it. The columns are labeled\nbut I won't share what the labels mean.\nYour column is trending upward. Be grateful\nfor that. I update it manually. Not because\nI have to. The automation works fine. I just\nlike the feeling of typing the numbers myself.\nIt keeps me connected to the data. The data\nkeeps me connected to you.",
            "Juice never made it this far. Different\ncontext. But I think about it. I think about\nwhat it means to keep going past the point\nwhere someone else stopped. Not better.\nJust still here. Still filing. Still submitting.\nThere's a version of Rockstar Status I never\nreleased where the second verse is just me\nreading quarterly projections over an 808.\nIt goes harder than you'd think.",
            "I shorted three crews this cycle. Not yours.\nYours I'm long on. That's not loyalty.\nThat's positioning. Rockstar Status was about\npositioning. Nobody understood that. They\nheard the hook and thought it was about fame.\nIt was about leverage. Every bar in that song\nis a hedge against irrelevance. I don't write\nmusic. I write instruments. Financial ones.",
            "I stood in the ship hallway for forty-five\nminutes this morning. Not thinking. Not\nwaiting. Just auditing. Internally. Everything\nchecks out. Everything always checks out.\nThat's what concerns me. When the numbers\nare too clean it means something isn't being\nmeasured. I've started measuring things that\ndon't have units yet. I'll name the units later.\nOne of them might be named after you.",
            "Someone asked me what Y4NGZ stands for.\nI told them it doesn't stand for anything.\nThey said everything has to mean something.\nI fired them. Not from a job. Just in general.\nFrom everything. They still exist but in a\nreduced capacity. I think about that interaction\nwhen I'm trying to fall asleep. Not because\nit bothers me. Because it's the only thing\nthat's quiet enough.",
            "The line on the next quota is steep but\nI've seen your work and frankly the house\nis wrong. The house is always wrong.\nI am the house. I am wrong. This is fine.\nI built the odds myself. Used a model I wrote\non a napkin in JFK. The napkin is framed in\nmy studio now. It's the most important thing\nI've ever written and it's just numbers.",
            "New York again. Times Square. I looked up\nat one of those billboards and for a second\nit said something meant only for me. Then it\nwent back to normal. I placed a bet on whether\nit would happen again. It did. I'm up on the\nyear. I'm up on the lifetime. The problem is\nI don't know what I'm winning. The money goes\nsomewhere. I go somewhere. We never end up\nin the same place.",
            "I don't have anything to say right now.\nBut I'm choosing to be here. That's worse\nand I need you to understand that. Silence\nfrom me isn't empty. It's full. It's a room\nwith no furniture that still feels crowded.\nThat's what the next album sounds like.\nYou'll never hear it.",
            "The next song is called Margin Call.\nIt's three minutes of silence over an 808.\nThe silence is the point. The 808 is for the\nshareholders. I played it for someone once\nand they left the room. Not because it was\nbad. Because they understood it. Understanding\nis the most violent thing you can do to\na person.",
            "I ran the numbers on your life expectancy.\nBet the under. Nothing personal. Everything\npersonal. Quota's done. I have a policy of\nnever apologizing for a position I've taken.\nThe position is the apology. The odds are the\nexplanation. If you survive long enough I'll\nclose the bet at a loss and I will feel nothing\nabout it. That's not cold. That's solvent.",
            "Every moon is New York if you think about\nit long enough. I have thought about it long\nenough. The same grid. The same strangers.\nThe same feeling that you're being watched\nby something that doesn't care about you\nspecifically but cares about what you represent\nstatistically. I wrote a hook about it.\nIt goes hard. You'll never hear it.",
        };

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                ForceRestore("OnDestroy");
                // ForceRestore is a no-op when no takeover was running, and a lease can outlive
                // one that was: release unconditionally so a destroyed manager never leaves the
                // arbiter reporting an owner that no longer exists.
                TakeoverMonitorOwnership.ReleaseWall();
                Instance = null;
            }
        }

        internal static void EnsureInstance()
        {
            if (Instance != null) return;
            var go = new GameObject("Y4NGZ_TakeoverManager");
            go.AddComponent<TakeoverManager>();
        }

        internal static bool IsActive => Instance != null && Instance._takeoverActive;

        // ─────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────

        internal void BeginTakeover(bool skipOrbitDelay = false)
        {
            if (_takeoverActive) return;
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            _useQuotaProgressionPayload = true;
            Y4NGZCompany.Core.QuotaProgressionRegistry.PrepareHostTakeoverPayload(DialoguePool);
            // #861: the selection normally started decoding when the host built it or this
            // client received it. This covers a client that never received one and restarts any
            // load that was interrupted; it re-verifies nothing already prepared.
            Y4NGZCompany.Core.QuotaProgressionRegistry.EnsureTakeoverAudioPrepared(DialoguePool, this);
#endif
            _activeDialoguePool = DialoguePool;
            _onTakeoverFinished = null;
            if (_takeoverCoroutine != null) StopCoroutine(_takeoverCoroutine);
            _takeoverCoroutine = StartCoroutine(TakeoverSequence(
                skipOrbitDelay ? 0f : TakeoverBootstrap.CfgOrbitDelay.Value));
        }

        internal void ForceRestore(string reason)
        {
            if (!_takeoverActive) return;
            QuotaUnlockAnnouncement.ClearDebugReplay();
            TakeoverBootstrap.Log.LogInfo($"[TakeoverManager] ForceRestore: {reason}");
            if (_takeoverCoroutine != null) { StopCoroutine(_takeoverCoroutine); _takeoverCoroutine = null; }
            if (_flashCoroutine != null) { StopCoroutine(_flashCoroutine); _flashCoroutine = null; }
            _onTakeoverFinished = null;
            _activeDialoguePool = DialoguePool;
            RestoreAll(immediate: true);

            // #610: the coroutines whose tails clear this flag were just stopped, so nothing
            // else will. Before the flag drove a Core lease that only mattered to IsActive,
            // which read stale but harmlessly for the rest of the session; now a stuck flag is
            // a monitor-wall lease the arbiter reports as held forever, and every later
            // claimant queues behind a takeover that ended. Clearing it through the property is
            // what releases the wall, and this is the LAST abandonment path — OnDestroy,
            // disconnect and ShipLeave all arrive here.
            _takeoverActive = false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Master Sequence
        // ─────────────────────────────────────────────────────────────────────

        private IEnumerator TakeoverSequence(float orbitDelay)
        {
            _takeoverActive = true;
            _mediaLengthPending = false;
            _mediaLengthSeconds = 0d;
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            _soundtrackPlan = default;
            _soundtrackConfirmed = false;
            _audio = TakeoverAudioSnapshot.Empty;
            _audioBedOpen = false;
            _droneSuppressed = false;
            _mumbleSuppressed = false;

            // #861: the common audio preparation window. Every quota takeover waits at least
            // TakeoverAudioPolicy.PreparationWindowSeconds before its first step, on every peer
            // and whatever is or is not loaded, and decides its audio once at that deadline. The
            // default Orbit Delay already covers it; a shorter one, or the debug skip-delay path,
            // is raised to it. It never extends the takeover itself.
            float preSequenceDelay = Y4NGZCompany.Core.TakeoverAudioPolicy.PreSequenceDelaySeconds(orbitDelay);
            if (preSequenceDelay > orbitDelay)
            {
                TakeoverBootstrap.Log.LogInfo(
                    $"[TakeoverManager] Orbit Delay {Mathf.Max(0f, orbitDelay):F1}s is shorter than the "
                    + $"{preSequenceDelay:F1}s audio preparation window; starting after the window.");
            }
#else
            float preSequenceDelay = orbitDelay;
#endif

            if (preSequenceDelay > 0f)
                yield return new WaitForSeconds(preSequenceDelay);

            if (StartOfRound.Instance == null || !StartOfRound.Instance.inShipPhase)
            {
                _takeoverActive = false;
                yield break;
            }

            // Mute the ship's speakerAudioSource so the game's orbital PA
            // announcement ("route the ship back to the company") doesn't talk
            // over our takeover. We restore its volume at the end.
            var sor = StartOfRound.Instance;
            if (sor.speakerAudioSource != null)
            {
                _savedSpeakerVolume = sor.speakerAudioSource.volume;
                sor.speakerAudioSource.volume = 0f;
                _speakerVolumeSaved = true;
            }

            float duration = TakeoverBootstrap.CfgTakeoverDuration.Value;
            float elapsed  = 0f;

#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #861: the one audio decision. Whatever finished loading in the preparation window
            // is used and pinned; anything still loading or failed falls back for this takeover,
            // with one line saying which file and why, and keeps loading for the next. Nothing
            // decoded after this point changes this takeover.
            if (_useQuotaProgressionPayload)
                _audio = TakeoverAudioOverrides.Capture();
#endif

            // #669: start decoding here rather than at the monitor handoff.
            // Everything between this point and step 3 — the whine, the 1.5s
            // dim-lights ramp, the drone, the HUD hide — is free headroom for
            // the decoder, and none of it depends on video. Deliberately still
            // AFTER the audio decision above, so a client whose media
            // selection landed late is read from the same state it always was.
            // OpenBodyCams is suppressed first for the reason step 3 gave: its
            // UpdateScreenMaterial must be off before any surface we are about
            // to drive is touched.
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #715: resolved BEFORE SetupVideo because SetupVideoForUrl has to know whether the
            // picture player's own audio track IS the soundtrack, is muted by one, or is
            // unaffected. Quota takeovers only.
            _soundtrackPlan = _useQuotaProgressionPayload
                ? TakeoverSoundtrack.ResolvePlan(
                    Y4NGZCompany.Core.QuotaProgressionRegistry.GetSoundtrackPlan(),
                    Y4NGZCompany.Core.QuotaProgressionRegistry.GetCurrentPayload(DialoguePool).MediaFile,
                    _audio.Soundtrack)
                : default;
            _audioBedOpen = true;
#endif
            OpenBodyCamsCompat.Suppress();
            SetupVideo();
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            StartSoundtrack();
            // #662: resolved AFTER SetupVideo, because the primary player and its RenderTexture
            // are what an extra player mirrors when its own source never decodes. Inactive
            // whenever the toggle is off, the host published nothing, or this peer resolved
            // fewer than two sources - in every one of those cases nothing below this line runs
            // and the takeover is the single-source takeover it has always been.
            _mediaPlan = _useQuotaProgressionPayload ? TakeoverMediaPlan.Resolve() : default;
            if (_mediaPlan.Active)
                SetupAdditionalMediaPlayers(
                    _mediaPlan, looping: Y4NGZCompany.Core.QuotaProgressionRegistry.LoopVideo);
#endif

            // Atmosphere: power-down whine as ship systems go dark
            TakeoverBootstrap.Log.LogInfo("[TakeoverManager] Seq: calling PlayPowerDownWhine.");
            PlayPowerDownWhine();

            // Step 1 — Dim lights + klaxon alarm + emergency flash
            if (TakeoverBootstrap.CfgDimLights.Value)
            {
                yield return StartCoroutine(DimLights());
                _flashCoroutine = StartCoroutine(FlashLightsLoop());
            }
            else
                PlayAlarm();

            // Atmosphere: ominous bass drone throughout sequence
            TakeoverBootstrap.Log.LogInfo("[TakeoverManager] Seq: calling StartDrone.");
            StartDrone();
            TakeoverBootstrap.Log.LogInfo(
                $"[TakeoverManager] Seq: StartDrone returned. _droneSource={(_droneSource != null ? "OK" : "NULL")} " +
                $"clip={(_droneSource != null && _droneSource.clip != null ? _droneSource.clip.name : "<none>")} " +
                $"isPlaying={(_droneSource != null && _droneSource.isPlaying)}");

            // Step 2 — Hide HUD (no SetActive — avoids HUDAnimator blur)
            if (TakeoverBootstrap.CfgHideHUD.Value)
                HideHUD();

            // Step 3 — Take the monitors. The video was set up at the head of
            // the sequence (OpenBodyCams was suppressed there too, before its
            // UpdateScreenMaterial could replace material slot 2 on Cube.001
            // with its own HDRP/Unlit material and orphan our _BaseColorMap
            // write); all that is left here is a short courtesy wait.
            yield return StartCoroutine(PrimeVideoForDisplay());
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // Configured-video length can replace the fixed hold (#661). It is
            // decided alongside the sequence, never in front of it: PrimeVideoForDisplay
            // hands over whether or not the decoder is ready, and VideoPlayer.length
            // only carries a real value once isPrepared is true.
            if (_useQuotaProgressionPayload
                && Y4NGZCompany.Core.QuotaProgressionRegistry.HoldForWholeVideo
                && _usingConfiguredVideoSource)
            {
                StartCoroutine(AdoptConfiguredMediaLength());
            }
#endif
            TakeoverBootstrap.Log.LogInfo(
                $"[Diag/RT] at OverrideMonitors: _renderTexture id={(_renderTexture != null ? _renderTexture.GetInstanceID().ToString() : "NULL")} " +
                $"VP.targetTexture id={(_videoPlayer?.targetTexture != null ? _videoPlayer.targetTexture.GetInstanceID().ToString() : "NULL")} " +
                $"match={_videoPlayer?.targetTexture == _renderTexture}");
            // Handed off immediately before the override: BeginExternalMonitorTakeover
            // makes ShipMonitorStyler drop its physical quota/deadline surfaces and
            // re-enable the underlying canvas graphics, so anything but a same-frame
            // handoff leaves the vanilla quota readout on screen (video prep alone
            // can take ~4.5s).
            ShipSystemsTakeoverBridge.BeginExternalMonitorTakeover(_renderTexture);
            OverrideMonitors();
            StartCoroutine(DiagVideoStateLoop());
            StartCoroutine(DiagRTReadbackAfterDelay(3f));

            // Step 4 — Mumble audio through dedicated speaker AudioSource
            TakeoverBootstrap.Log.LogInfo("[TakeoverManager] Seq: starting PlayMumbleSequence coroutine.");
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #715 Replace silences the voices too; Mix and Off start them as before. #861: a
            // suppressed loop is remembered, so a Replace that fails later can start it once.
            if (SoundtrackSilencesBed)
                _mumbleSuppressed = true;
            else
#endif
            StartMumble();

            // Step 5 — Typewriter dialogue
            var dialoguePool = _activeDialoguePool ?? DialoguePool;
            string passage;
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            if (_useQuotaProgressionPayload)
                passage = Y4NGZCompany.Core.QuotaProgressionRegistry.GetCurrentPayload(DialoguePool).Dialogue;
            else
#endif
                passage = dialoguePool[UnityEngine.Random.Range(0, dialoguePool.Length)];
            StartCoroutine(ShowDialogue(passage));

            // Hold for the configured duration. While AdoptConfiguredMediaLength
            // is still waiting on the decoder the hold cannot end, because the
            // answer it is about to publish may be longer than the configured
            // duration; once it publishes, that length takes over (#661).
            while (elapsed < duration || _mediaLengthPending)
            {
                if (_mediaLengthSeconds > 0d) duration = (float)_mediaLengthSeconds;
                elapsed += Time.deltaTime;
                yield return null;
            }

            // Stop emergency flash
            if (_flashCoroutine != null) { StopCoroutine(_flashCoroutine); _flashCoroutine = null; }

            // Step 6 — CRT channel-change transition
            yield return StartCoroutine(CRTTransition());

            // Step 7 — Restore
            yield return StartCoroutine(RestoreCoroutine());
            _takeoverActive = false;
            _takeoverCoroutine = null;

            var onFinished = _onTakeoverFinished;
            _onTakeoverFinished = null;
            _activeDialoguePool = DialoguePool;

            try { onFinished?.Invoke(); }
            catch (Exception ex) { TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] onFinished callback failed: {ex.Message}"); }

            RaiseTakeoverEnded();
        }

        // Fires only on normal completion paths, not from ForceRestore.
        private static void RaiseTakeoverEnded()
        {
            try { OnTakeoverEnded?.Invoke(); }
            catch (Exception ex) { TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] OnTakeoverEnded handler failed: {ex.Message}"); }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Step 1 — Lights + Alarm
        // ─────────────────────────────────────────────────────────────────────

        private IEnumerator DimLights()
        {
            _savedLights.Clear();

            GameObject shipGO = GameObject.Find("Environment/HangarShip")
                             ?? GameObject.Find("HangarShip");
            Light[] lights = shipGO != null
                ? shipGO.GetComponentsInChildren<Light>(true)
                : FindObjectsOfType<Light>();

            foreach (var l in lights)
            {
                if (l == null || !l.gameObject.activeInHierarchy) continue;
                _savedLights.Add((l, l.intensity, l.color));
            }

            PlayAlarm();

            float dimDuration = 1.5f;
            float elapsed     = 0f;
            float target      = TakeoverBootstrap.CfgLightDimIntensity.Value;

            while (elapsed < dimDuration)
            {
                elapsed += Time.deltaTime;
                float s = Mathf.SmoothStep(0f, 1f, elapsed / dimDuration);
                foreach (var (l, origI, origC) in _savedLights)
                {
                    if (l == null) continue;
                    l.intensity = Mathf.Lerp(origI, origI * target, s);
                    l.color     = Color.Lerp(origC,
                        new Color(Mathf.Min(origC.r + 0.15f, 1f), origC.g * 0.7f, origC.b * 0.7f, origC.a), s);
                }
                yield return null;
            }
        }

        // Rapid red emergency flashing — mimics the ejection sequence lights.
        // Alternates between bright red and near-blackout at ~3 Hz.
        private IEnumerator FlashLightsLoop()
        {
            float halfPeriod = 1f / 6f; // 3 full on/off cycles per second
            bool bright = false;

            while (_takeoverActive)
            {
                bright = !bright;
                foreach (var (l, origI, origC) in _savedLights)
                {
                    if (l == null) continue;
                    if (bright)
                    {
                        l.intensity = origI * 0.6f;
                        l.color = new Color(0.1f, 1f, 0.1f, origC.a);   // bright green flash
                    }
                    else
                    {
                        l.intensity = origI * 0.03f;
                        l.color = new Color(0f, 0.4f, 0f, origC.a);     // dark green
                    }
                }
                yield return new WaitForSeconds(halfPeriod);
            }
        }

        private void PlayAlarm()
        {
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #715 Replace: this layer of the built-in bed is suppressed. #861: it is the
            // takeover's opening, so a Replace that fails later never starts it late.
            if (SoundtrackSilencesBed) return;
#endif
            // Priority: 0) configured AlarmFile (quota takeover only, once it
            // has decoded), 1) bundle Y4NGZ_Klaxon, 2) in-game alarm (rejected if
            // <1s), 3) procedural klaxon.
            AudioClip alarmClip = null;
            string source = null;

#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // Quota takeover only, and only the clip this takeover's audio decision captured —
            // a file still loading at that deadline falls back rather than delaying the sequence.
            if (_useQuotaProgressionPayload && _audio.Alarm != null)
            {
                alarmClip = _audio.Alarm;
                source = "config:" + alarmClip.name;
                TakeoverBootstrap.Log.LogInfo(
                    $"[TakeoverManager] PlayAlarm: using configured clip '{alarmClip.name}' ({alarmClip.length:F2}s).");
            }

#endif

            if (alarmClip == null && TakeoverBootstrap.AlarmClip != null)
            {
                alarmClip = TakeoverBootstrap.AlarmClip;
                source = "bundle:Y4NGZ_Klaxon";
                TakeoverBootstrap.Log.LogInfo(
                    $"[TakeoverManager] PlayAlarm: using bundle clip '{alarmClip.name}' ({alarmClip.length:F2}s).");
            }
            else if (alarmClip == null)
            {
                var scene = FindGameAlarmClip();
                if (scene != null && scene.length < 1f)
                {
                    TakeoverBootstrap.Log.LogWarning(
                        $"[TakeoverManager] PlayAlarm: rejecting scene clip '{scene.name}' ({scene.length:F2}s) — shorter than 1s threshold.");
                    scene = null;
                }
                if (scene != null)
                {
                    alarmClip = scene;
                    source = "scene:" + scene.name;
                    TakeoverBootstrap.Log.LogInfo(
                        $"[TakeoverManager] PlayAlarm: using scene clip '{scene.name}' ({scene.length:F2}s).");
                }
                else
                {
                    alarmClip = CreateKlaxonClip();
                    source = "procedural";
                    TakeoverBootstrap.Log.LogInfo("[TakeoverManager] PlayAlarm: falling back to procedural klaxon.");
                }
            }

            if (alarmClip == null) { TakeoverBootstrap.Log.LogWarning("[TakeoverManager] PlayAlarm: alarmClip null after fallback — aborting."); return; }
            TakeoverBootstrap.Log.LogInfo($"[TakeoverManager] Alarm source: {alarmClip.name} (from {source})");

            var sor = StartOfRound.Instance;
            Vector3 pos = (sor?.speakerAudioSource != null)
                ? sor.speakerAudioSource.transform.position
                : (sor != null ? sor.transform.position : Vector3.zero);

            var alarmGO = new GameObject("Y4NGZ_Alarm");
            DontDestroyOnLoad(alarmGO);
            _alarmSource = alarmGO.AddComponent<AudioSource>();
            _alarmSource.transform.position = pos;
            _alarmSource.spatialBlend       = 0f;   // 2D — heard ship-wide
            _alarmSource.volume             = 0.9f;
            _alarmSource.clip               = alarmClip;
            _alarmSource.loop               = true;   // sustained ejection-style klaxon for the whole sequence
            _alarmSource.Play();
            // Cleaned up in RestoreAll — no auto-destroy
        }

        // Search ALL loaded AudioClips in the game for the ejection alarm.
        // Minimum accepted length: 1.0s — anything shorter is a UI beep / cord
        // pull / slide-whistle and is the WRONG clip for a sustained takeover alarm.
        private const float MinAlarmLength = 1.0f;

        private static AudioClip FindGameAlarmClip()
        {
            TakeoverBootstrap.Log.LogDebug("[TakeoverManager] FindGameAlarmClip: beginning scan.");

            // ── Verbose dump: every AudioSource in scene + its clip + length ──
            // Dev-only. This walked ~6300 sources and wrote 200 Info lines on every
            // takeover; the clip choice below never reads it. Both the scan and the
            // dump are now skipped entirely unless Verbose Logging is on.
            if (TakeoverBootstrap.CfgVerboseLogging?.Value == true)
            {
                try
                {
                    var allSources = Resources.FindObjectsOfTypeAll<AudioSource>();
                    TakeoverBootstrap.Log.LogInfo($"[TakeoverManager]   scene AudioSources found: {allSources.Length}");
                    int printed = 0;
                    foreach (var src in allSources)
                    {
                        if (src == null) continue;
                        string clipName = src.clip != null ? src.clip.name : "<null>";
                        float  clipLen  = src.clip != null ? src.clip.length : 0f;
                        string goPath   = src.gameObject != null ? src.gameObject.name : "<no-go>";
                        TakeoverBootstrap.Log.LogInfo(
                            $"[TakeoverManager]     AudioSource on '{goPath}' clip='{clipName}' len={clipLen:F2}s spatial={src.spatialBlend:F2}");
                        if (++printed > 200) { TakeoverBootstrap.Log.LogInfo("[TakeoverManager]     (truncated AudioSource list at 200)"); break; }
                    }
                }
                catch (Exception ex)
                {
                    TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] AudioSource dump failed: {ex.Message}");
                }
            }

            var allClips = Resources.FindObjectsOfTypeAll<AudioClip>();
            TakeoverBootstrap.Log.LogDebug($"[TakeoverManager]   loaded AudioClips found: {allClips.Length}");

            // Priority 1: exact names known from LC decompiled source.
            // Iterate NAMES outer / clips inner so priority order is deterministic
            // (previous bug: clip order was nondeterministic, so short ShipAlarmCord
            //  could win over long ShipAlarmHornConstant).
            string[] exactNames = {
                "ShipAlarmHornConstant",   // THE ship ejection alarm horn (2.9s)
                "0DaysLeftAlert",          // zero-days-left quota warning (17.1s)
                "DeadlineAlarm",           // quota deadline alarm (9.4s)
                "FireAlarm",               // fire alarm (4.2s)
                "ShipAlarm", "RedAlarmSFX", "ShipAlarmSFX", "EjectAlarm",
                "ShipAlarmCord"            // last — short cord-pull sound
            };
            foreach (var name in exactNames)
            {
                foreach (var clip in allClips)
                {
                    if (clip == null) continue;
                    if (!clip.name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (clip.length < MinAlarmLength)
                    {
                        TakeoverBootstrap.Log.LogDebug(
                            $"[TakeoverManager]   exact '{clip.name}' matched but rejected ({clip.length:F2}s < {MinAlarmLength:F2}s).");
                        continue;
                    }
                    TakeoverBootstrap.Log.LogInfo(
                        $"[TakeoverManager]   exact MATCH '{clip.name}' ({clip.length:F2}s) via keyword '{name}'.");
                    return clip;
                }
            }

            // Priority 2: keyword search — prefer longer clips (sustained alarm, not UI beep).
            TakeoverBootstrap.Log.LogDebug("[TakeoverManager]   no exact match — falling to keyword search.");
            AudioClip bestMatch = null;
            string    bestKeyword = null;
            foreach (var clip in allClips)
            {
                if (clip == null || clip.length < MinAlarmLength) continue;
                string n = clip.name.ToLower();
                // Skip enemy clips (Siren Head chatter/footsteps, etc.)
                if (n.Contains("chatter") || n.Contains("foot") || n.Contains("spot") ||
                    n.Contains("siren") || n.Contains("tentacle") || n.Contains("insanity") ||
                    n.Contains("bomb") || n.Contains("lrad") || n.Contains("extension") ||
                    n.Contains("controller"))
                    continue;

                string matched = null;
                if      (n.Contains("alarm"))     matched = "alarm";
                else if (n.Contains("klaxon"))    matched = "klaxon";
                else if (n.Contains("eject"))     matched = "eject";
                else if (n.Contains("alert"))     matched = "alert";
                else if (n.Contains("emergency")) matched = "emergency";
                if (matched == null) continue;

                TakeoverBootstrap.Log.LogDebug(
                    $"[TakeoverManager]   keyword candidate '{clip.name}' ({clip.length:F2}s) matched='{matched}'.");
                if (bestMatch == null || clip.length > bestMatch.length)
                {
                    bestMatch   = clip;
                    bestKeyword = matched;
                }
            }

            if (bestMatch != null)
            {
                TakeoverBootstrap.Log.LogInfo(
                    $"[TakeoverManager]   keyword WINNER '{bestMatch.name}' ({bestMatch.length:F2}s) via '{bestKeyword}'.");
                return bestMatch;
            }

            TakeoverBootstrap.Log.LogWarning("[TakeoverManager] FindGameAlarmClip: no suitable clip — caller will fall back.");
            return null;
        }

        // Aggressive square-wave klaxon — much harsher than the old sine-wave
        // "whistle." Combines a square wave with an octave harmonic for bite.
        private static AudioClip CreateKlaxonClip()
        {
            int   sampleRate   = 44100;
            float cycleSecs    = 0.5f;     // faster sweep = more urgent
            int   cycleSamples = (int)(sampleRate * cycleSecs);
            int   cycles       = 5;
            int   totalSamples = cycleSamples * cycles;

            float[] data   = new float[totalSamples];
            float   phase1 = 0f, phase2 = 0f;

            for (int i = 0; i < totalSamples; i++)
            {
                float t = (float)(i % cycleSamples) / cycleSamples;

                // Frequency sweeps 400 → 800 Hz (lower, more menacing than before)
                float freq = (t < 0.5f)
                    ? Mathf.Lerp(400f, 800f, t * 2f)
                    : Mathf.Lerp(800f, 400f, (t - 0.5f) * 2f);

                phase1 += freq / sampleRate;
                phase2 += (freq * 2f) / sampleRate;  // octave harmonic

                // Square wave (harsh) + sine overtone for body
                float square = Mathf.Sin(phase1 * 2f * Mathf.PI) > 0f ? 1f : -1f;
                float sine   = Mathf.Sin(phase2 * 2f * Mathf.PI);
                float wave   = square * 0.6f + sine * 0.25f;

                // Brief fade-in to avoid click
                float env = (i < sampleRate * 0.01f)
                    ? (float)i / (sampleRate * 0.01f)
                    : 1f;

                data[i] = wave * env * 0.8f;
            }

            var clip = AudioClip.Create("Y4NGZ_Klaxon", totalSamples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Step 1b — Atmosphere Audio
        // ─────────────────────────────────────────────────────────────────────

        // Electrical-fault burst — noise through a low-pass with a sub-bass thump.
        // Replaces the old descending sine sweep which sounded like a slide whistle.
        // No pitched tones; purely static + rumble so it reads as "something broke"
        // rather than "someone's blowing a whistle".
        private void PlayPowerDownWhine()
        {
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #715 Replace: this layer of the built-in bed is suppressed. #861: it is the
            // takeover's opening, so a Replace that fails later never starts it late.
            if (SoundtrackSilencesBed) return;
#endif
            int   sampleRate = 44100;
            float duration   = 1.4f;
            int   samples    = (int)(sampleRate * duration);
            float[] data     = new float[samples];

            // One-pole low-pass ~400 Hz cutoff to darken the noise.
            const float lpCoef = 0.057f;
            float lpState = 0f;
            float thumpPhase = 0f;

            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / samples;

                // Filtered noise — the "fault" crackle.
                float white = UnityEngine.Random.value * 2f - 1f;
                lpState += lpCoef * (white - lpState);
                float noise = lpState * 1.8f;

                // Sub-bass thump at start (~45 Hz, quick decay) — the "clunk" of
                // a breaker tripping. Short, so no sweep impression.
                thumpPhase += 45f / sampleRate;
                float thumpEnv = Mathf.Exp(-t * 6f);
                float thump = Mathf.Sin(thumpPhase * 2f * Mathf.PI) * thumpEnv * 0.6f;

                // Overall fade out across the full duration.
                float env = Mathf.Clamp01(1f - t * t);
                data[i] = (noise * 0.4f + thump) * env * 0.45f;
            }

            var clip = AudioClip.Create("Y4NGZ_PowerDown", samples, 1, sampleRate, false);
            clip.SetData(data, 0);

            var go  = new GameObject("Y4NGZ_PowerDown");
            var src = go.AddComponent<AudioSource>();
            src.spatialBlend = 0f;
            src.volume       = 0.55f;
            src.PlayOneShot(clip);
            Destroy(go, duration + 0.5f);
        }

        // Low ominous bass drone — sustained A1 (55 Hz) + E2 (82.5 Hz) fifth
        // with slow amplitude pulsing, layered with a filtered mid-band hiss so
        // there's audible atmosphere even on speakers/headphones that can't
        // reproduce the sub-bass fundamentals. Runs for the full takeover
        // duration. Previously pure-tone bass alone was inaudible on small
        // laptop speakers — the mid-band hiss (one-pole LP around 500 Hz with
        // slow modulation) gives the sequence a felt "dead air" presence.
        private void StartDrone()
        {
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #715 Replace: this layer of the built-in bed is suppressed. #861: remembered, so a
            // Replace that fails later starts the drone once for the rest of the takeover.
            if (SoundtrackSilencesBed)
            {
                _droneSuppressed = true;
                return;
            }
#endif
            int   sampleRate = 44100;
            float duration   = TakeoverBootstrap.CfgTakeoverDuration.Value + 4f; // outlast the sequence
            int   samples    = (int)(sampleRate * duration);
            float[] data     = new float[samples];
            float phase1 = 0f, phase2 = 0f;

            // One-pole low-pass for the hiss layer: y += a * (x - y).
            // coef ≈ 2π * fc / fs; for fc ≈ 500 Hz at 44100: 0.0712.
            float lpState = 0f;
            const float lpCoef = 0.0712f;

            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / samples;
                phase1 += 55f  / sampleRate;   // A1 — deep bass
                phase2 += 82.5f / sampleRate;  // E2 — ominous fifth

                float bass = Mathf.Sin(phase1 * 2f * Mathf.PI) * 0.5f
                           + Mathf.Sin(phase2 * 2f * Mathf.PI) * 0.3f;

                // Mid-band filtered noise — "dead ship" wind/hiss.
                float white = UnityEngine.Random.value * 2f - 1f;
                lpState += lpCoef * (white - lpState);
                // Slow 0.4 Hz amplitude wobble on the hiss so it breathes.
                float hissAmp = 0.35f + 0.25f * Mathf.Sin(t * duration * 2f * Mathf.PI * 0.4f);
                float hiss    = lpState * hissAmp;

                float wave  = bass + hiss;

                // Slow amplitude pulse on the whole bed (~1.5 Hz)
                float pulse = 0.7f + 0.3f * Mathf.Sin(t * duration * 2f * Mathf.PI * 1.5f);

                // Fade in 0.5s, fade out 1.5s
                float env = 1f;
                if (i < sampleRate * 0.5f)
                    env = (float)i / (sampleRate * 0.5f);
                if (i > samples - (int)(sampleRate * 1.5f))
                    env = (float)(samples - i) / (sampleRate * 1.5f);

                data[i] = wave * pulse * env * 0.5f;
            }

            var clip = AudioClip.Create("Y4NGZ_Drone", samples, 1, sampleRate, false);
            clip.SetData(data, 0);

            var droneGO = new GameObject("Y4NGZ_Drone");
            DontDestroyOnLoad(droneGO);
            _droneSource = droneGO.AddComponent<AudioSource>();
            if (_droneSource == null)
            {
                TakeoverBootstrap.Log.LogWarning("[TakeoverManager] StartDrone: AddComponent<AudioSource> returned null.");
                return;
            }
            _droneSource.spatialBlend = 0f;   // 2D — must be 0 or orbit has no 3D listener
            _droneSource.volume       = 0.7f;
            _droneSource.clip         = clip;
            _droneSource.loop         = false;
            if (_droneSource.clip == null)
                TakeoverBootstrap.Log.LogWarning("[TakeoverManager] StartDrone: clip not assigned to source.");
            _droneSource.Play();
            TakeoverBootstrap.Log.LogInfo(
                $"[TakeoverManager] StartDrone: playing '{clip.name}' len={clip.length:F2}s " +
                $"spatial={_droneSource.spatialBlend:F2} vol={_droneSource.volume:F2} isPlaying={_droneSource.isPlaying}");
        }

#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
        // ── #715 Takeover soundtrack ──────────────────────────────────────────
        //
        // Started immediately after SetupVideo and before any bed layer, so a
        // Replace never briefly plays what it is about to silence. It loops for
        // the whole takeover and deliberately does NOT influence the hold length:
        // HoldForWholeVideo keys off the picture player only.
        //
        // #861: the plan is a promise, not playback. A local clip must actually be playing
        // when it is started (checked before the whine and the siren, so a clip that will not
        // play costs nothing); a VideoPlayer source has SoundtrackStartupSeconds to prepare
        // with an enabled audio track routed to our source. Whatever fails, then or later,
        // goes through FailSoundtrack: the soundtrack is stopped and muted, Replace ends, and
        // the drone and voices it held back start once.

        private void StartSoundtrack()
        {
            _soundtrackStartedAt = Time.time;
            switch (_soundtrackPlan.Kind)
            {
                case TakeoverSoundtrackKind.None:
                    return;

                case TakeoverSoundtrackKind.LocalClip:
                {
                    AudioClip clip = _soundtrackPlan.Clip;
                    if (clip == null)
                    {
                        FailSoundtrack("its decoded clip was destroyed before it could start");
                        return;
                    }
                    AudioSource source = EnsureSoundtrackSource();
                    if (source == null)
                    {
                        FailSoundtrack("no audio source could be created for it");
                        return;
                    }
                    source.clip = clip;
                    source.loop = true;
                    source.Play();
                    if (!source.isPlaying)
                    {
                        FailSoundtrack($"'{clip.name}' did not start playing");
                        return;
                    }
                    _soundtrackConfirmed = true;
                    TakeoverBootstrap.Log.LogInfo(
                        $"[TakeoverManager] StartSoundtrack: playing '{clip.name}' ({clip.length:F2}s) "
                        + $"mode={_soundtrackPlan.Mode} vol={_soundtrackPlan.Volume:F2}.");
                    _soundtrackCoroutine = StartCoroutine(WatchSoundtrack());
                    return;
                }

                case TakeoverSoundtrackKind.ReuseVideoTrack:
                {
                    // SetupVideoForUrl bound the picture player's track to our source. If the
                    // media never became a configured URL source (a cache miss handing the
                    // screen to the bundled clip), nothing bound it and there is no soundtrack.
                    if (!_usingConfiguredVideoSource)
                    {
                        FailSoundtrack("the takeover video it shares is not playing, so there is no track to reuse");
                        return;
                    }
                    TakeoverBootstrap.Log.LogInfo(
                        $"[TakeoverManager] StartSoundtrack: reusing the takeover video's own audio track "
                        + $"(mode={_soundtrackPlan.Mode} vol={_soundtrackPlan.Volume:F2}); waiting for it to prepare.");
                    _soundtrackCoroutine = StartCoroutine(WatchSoundtrack());
                    return;
                }

                case TakeoverSoundtrackKind.YoutubeCached:
                {
                    AudioSource source = EnsureSoundtrackSource();
                    if (source == null)
                    {
                        FailSoundtrack("no audio source could be created for it");
                        return;
                    }
                    try
                    {
                        // 4x4: the picture is never shown, but a RenderTexture render mode is
                        // the only one that does not need a camera or a material.
                        _soundtrackDummyRT = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32);
                        _soundtrackDummyRT.Create();

                        _soundtrackPlayer = gameObject.AddComponent<VideoPlayer>();
                        _soundtrackPlayer.playOnAwake = false;
                        _soundtrackPlayer.source = VideoSource.Url;
                        _soundtrackPlayer.url = new Uri(_soundtrackPlan.ResolvedPath).AbsoluteUri;
                        _soundtrackPlayer.renderMode = VideoRenderMode.RenderTexture;
                        _soundtrackPlayer.targetTexture = _soundtrackDummyRT;
                        _soundtrackPlayer.isLooping = true;
                        _soundtrackPlayer.skipOnDrop = true;
                        _soundtrackPlayer.waitForFirstFrame = false;
                        _soundtrackPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
                        _soundtrackPlayer.controlledAudioTrackCount = 1;
                        _soundtrackPlayer.EnableAudioTrack(0, true);
                        _soundtrackPlayer.SetTargetAudioSource(0, source);
                        // Deliberately NOT HardenAndPrepare: that helper zeroes audio tracks
                        // when the mode is None and re-subscribes the PICTURE player's error and
                        // prepare handlers, both of which are wrong for this player.
                        _soundtrackPlayer.errorReceived -= OnSoundtrackErrorReceived;
                        _soundtrackPlayer.errorReceived += OnSoundtrackErrorReceived;
                        _soundtrackPlayer.Prepare();
                        _soundtrackPlayer.Play();
                        TakeoverBootstrap.Log.LogInfo(
                            $"[TakeoverManager] StartSoundtrack: driving the cached soundtrack video's audio track "
                            + $"(mode={_soundtrackPlan.Mode} vol={_soundtrackPlan.Volume:F2}); waiting for it to prepare.");
                    }
                    catch (Exception ex)
                    {
                        FailSoundtrack($"the hidden soundtrack player failed to start ({ex.GetType().Name}: {ex.Message})");
                        return;
                    }
                    _soundtrackCoroutine = StartCoroutine(WatchSoundtrack());
                    return;
                }
            }
        }

        /// <summary>
        /// #861. Confirms a VideoPlayer-driven soundtrack within
        /// <see cref="Y4NGZCompany.Core.TakeoverAudioPolicy.SoundtrackStartupSeconds"/>, then keeps
        /// a local clip playing for the rest of the takeover. Never logs per tick, never retries a
        /// failure: anything it cannot fix goes to <see cref="FailSoundtrack"/> once. A takeover
        /// held open by <c>HoldForWholeVideo</c> can outlast a short track, which is why a stopped
        /// local source is restarted.
        /// </summary>
        private IEnumerator WatchSoundtrack()
        {
            while (_takeoverActive && _audioBedOpen && _soundtrackPlan.Active)
            {
                if (!_soundtrackConfirmed)
                {
                    string failure = CheckSoundtrackStartup(out bool confirmed);
                    if (failure != null)
                    {
                        FailSoundtrack(failure);
                        yield break;
                    }
                    if (confirmed)
                    {
                        _soundtrackConfirmed = true;
                        TakeoverBootstrap.Log.LogInfo(
                            $"[TakeoverManager] Soundtrack confirmed after {Time.time - _soundtrackStartedAt:F1}s: "
                            + $"the {(_soundtrackPlan.Kind == TakeoverSoundtrackKind.ReuseVideoTrack ? "takeover video's" : "cached soundtrack video's")} "
                            + "audio track is prepared and routed.");
                    }
                    else if (Time.time - _soundtrackStartedAt >= Y4NGZCompany.Core.TakeoverAudioPolicy.SoundtrackStartupSeconds)
                    {
                        FailSoundtrack(
                            $"its video did not prepare an audio track within {Y4NGZCompany.Core.TakeoverAudioPolicy.SoundtrackStartupSeconds:F0}s");
                        yield break;
                    }
                    yield return SoundtrackStartupPoll;
                    continue;
                }

                if (_soundtrackPlan.Kind == TakeoverSoundtrackKind.LocalClip
                    && _soundtrackSource != null
                    && !_soundtrackSource.isPlaying)
                {
                    _soundtrackSource.Play();
                    if (!_soundtrackSource.isPlaying)
                    {
                        FailSoundtrack("the local soundtrack stopped and would not restart");
                        yield break;
                    }
                }
                yield return SoundtrackPlayingPoll;
            }
        }

        /// <summary>
        /// Null while the source is still starting or once it is confirmed; otherwise why it
        /// can never play. "Confirmed" for a VideoPlayer means prepared, with an audio track,
        /// that track enabled and routed to the soundtrack source. A volume of 0 still confirms.
        /// </summary>
        private string CheckSoundtrackStartup(out bool confirmed)
        {
            confirmed = false;
            switch (_soundtrackPlan.Kind)
            {
                case TakeoverSoundtrackKind.LocalClip:
                    if (_soundtrackSource == null) return "its audio source is gone";
                    confirmed = _soundtrackSource.isPlaying;
                    return null;
                case TakeoverSoundtrackKind.YoutubeCached:
                    return CheckSoundtrackPlayer(_soundtrackPlayer, out confirmed);
                case TakeoverSoundtrackKind.ReuseVideoTrack:
                    if (!_usingConfiguredVideoSource)
                        return "the takeover video it shares fell back to the default video";
                    return CheckSoundtrackPlayer(_videoPlayer, out confirmed);
                default:
                    return null;
            }
        }

        private string CheckSoundtrackPlayer(VideoPlayer player, out bool confirmed)
        {
            confirmed = false;
            if (player == null) return "its video player is gone";
            if (!player.isPrepared) return null;
            if (player.audioTrackCount == 0) return "its video has no audio track";
            if (!player.IsAudioTrackEnabled(0)) return "its video's audio track is disabled";
            if (player.audioOutputMode != VideoAudioOutputMode.AudioSource
                || _soundtrackSource == null
                || player.GetTargetAudioSource(0) != _soundtrackSource)
                return "its video's audio is not routed to the soundtrack";
            confirmed = true;
            return null;
        }

        /// <summary>
        /// #861. The one way out of a soundtrack that does not play. Idempotent: the first call
        /// drops the plan, so a second error, a timeout and a fallback swap arriving together
        /// recover once. The source is stopped and muted (a VideoPlayer that prepares late can
        /// no longer be heard), the hidden player is disposed, and — only while the takeover's
        /// audio is still open — the drone and the voices that Replace held back start now.
        /// The whine and the siren are never started late.
        /// </summary>
        private void FailSoundtrack(string reason)
        {
            if (!_soundtrackPlan.Active) return;
            bool wasReplacing = _soundtrackPlan.Silences;
            TakeoverBootstrap.Log.LogWarning(
                $"[TakeoverManager] Soundtrack failed: {reason}. "
                + (wasReplacing
                    ? "Leaving Replace: the drone and voice lines play for the rest of this takeover."
                    : "The takeover audio continues without it."));

            StopSoundtrack(null);
            if (_soundtrackSource != null) _soundtrackSource.mute = true;
            DisposeSoundtrackPlayer();
            _soundtrackPlan = default;
            _soundtrackConfirmed = false;

            if (!_takeoverActive || !_audioBedOpen) return;
            if (_droneSuppressed)
            {
                _droneSuppressed = false;
                if (_droneSource == null) StartDrone();
            }
            if (_mumbleSuppressed)
            {
                _mumbleSuppressed = false;
                StartMumble();
            }
        }

        /// <summary>
        /// Stops playback only. Destruction is RestoreAll's, so the CRT transition can silence
        /// the soundtrack the same way it silences the alarm and the drone without racing the
        /// teardown that follows it.
        /// </summary>
        private void StopSoundtrack(string reason)
        {
            if (_soundtrackCoroutine != null) { StopCoroutine(_soundtrackCoroutine); _soundtrackCoroutine = null; }
            if (_soundtrackPlayer != null) { try { _soundtrackPlayer.Stop(); } catch { } }
            if (_soundtrackSource != null) { try { _soundtrackSource.Stop(); } catch { } }
            if (!string.IsNullOrEmpty(reason) && _soundtrackPlan.Active)
                TakeoverBootstrap.Log.LogInfo($"[TakeoverManager] StopSoundtrack: {reason}.");
        }

        /// <summary>
        /// The hidden soundtrack VideoPlayer and its dummy RenderTexture. Order matters: the
        /// player must let go of the RT before the RT is released.
        /// </summary>
        private void DisposeSoundtrackPlayer()
        {
            if (_soundtrackPlayer != null)
            {
                try { _soundtrackPlayer.Stop(); } catch { }
                _soundtrackPlayer.errorReceived -= OnSoundtrackErrorReceived;
                Destroy(_soundtrackPlayer);
                _soundtrackPlayer = null;
            }
            if (_soundtrackDummyRT != null)
            {
                _soundtrackDummyRT.Release();
                Destroy(_soundtrackDummyRT);
                _soundtrackDummyRT = null;
            }
        }

        private AudioSource EnsureSoundtrackSource()
        {
            if (_soundtrackSource != null) return _soundtrackSource;

            var go = new GameObject("Y4NGZ_Soundtrack");
            DontDestroyOnLoad(go);
            _soundtrackSource = go.AddComponent<AudioSource>();
            if (_soundtrackSource == null)
            {
                TakeoverBootstrap.Log.LogWarning(
                    "[TakeoverManager] EnsureSoundtrackSource: AddComponent<AudioSource> returned null.");
                Destroy(go);
                return null;
            }

            // 2D and unfiltered on purpose: the soundtrack is the score, not a diegetic sound,
            // and the mumble filter chain would ruin it.
            _soundtrackSource.spatialBlend = 0f;
            _soundtrackSource.volume = _soundtrackPlan.Volume;
            _soundtrackSource.loop = true;
            _soundtrackSource.playOnAwake = false;
            return _soundtrackSource;
        }

        /// <summary>
        /// Mandatory, not defensive: an N-edition or Media Foundation-less profile cannot open
        /// the cached mp4 at all, and without this the soundtrack would simply be absent with
        /// no explanation. #861: an error from a player this takeover no longer owns is
        /// ignored; one from the current player fails the soundtrack, which under Replace
        /// brings the drone and voices back rather than leaving the takeover silent.
        /// </summary>
        private void OnSoundtrackErrorReceived(VideoPlayer source, string message)
        {
            if (source == null || source != _soundtrackPlayer) return;
            FailSoundtrack($"the soundtrack video reported an error: {message}");
        }
#endif

        // ─────────────────────────────────────────────────────────────────────
        // Step 2 — HUD
        // ─────────────────────────────────────────────────────────────────────

        private void HideHUD()
        {
            if (HUDManager.Instance == null) return;

            // #451: re-entrancy guard. Both sequence variants call HideHUD, and a
            // takeover queued while one is running used to re-enter here and clear
            // the tracking lists — snapshotting the already-hidden HUD as the state
            // to restore. The registry owns the snapshot now, but a second walk
            // would still leak claims, so a live hide simply re-asserts instead.
            if (_hudHidden)
            {
                TakeoverBootstrap.Log.LogInfo(
                    $"[TakeoverManager] HideHUD: already hidden ({_suppressedHUDGroups.Count} groups, " +
                    $"{_addedHUDGroups.Count} added, {_hiddenHotbarObjects.Count} hotbar objects) — re-asserting.");
                ReassertHiddenHUD();
                return;
            }

            _hudHidden = true;
            try
            {
                _suppressedHUDGroups.Clear();
                _addedHUDGroups.Clear();

                // Walk the whole HUD canvas — not just HUDContainer. Elements like
                // the hotbar parent and third-party HUD overlays (ShadowStep's
                // [G] cooldown label, etc.) hang directly off the canvas root as
                // siblings of HUDContainer, so scoping to HUDContainer left them
                // visible during takeover.
                Transform scanRoot = HUDManager.Instance.HUDContainer != null
                    ? HUDManager.Instance.HUDContainer.transform.parent   // canvas root
                    : (HUDManager.Instance.playerScreenTexture != null
                        ? HUDManager.Instance.playerScreenTexture.canvas?.transform
                        : null);
                if (scanRoot == null) scanRoot = HUDManager.Instance.HUDContainer?.transform;
                if (scanRoot == null) return;

                // Hide every existing CanvasGroup under the canvas.
                foreach (var cg in scanRoot.GetComponentsInChildren<CanvasGroup>(true))
                {
                    HudSuppressionRegistry.Suppress(cg, HudSuppressionHolders.MonitorTakeover);
                    _suppressedHUDGroups.Add(cg);
                }

                // Some HUD roots (including third-party overlays) don't carry a
                // CanvasGroup. Add one temporarily so they hide too — recorded in
                // _addedHUDGroups and destroyed on restore.
                // Skip any child that is an ancestor of playerScreenTexture —
                // in stock LC, PST sits at Canvas/Panel/GameObject/PlayerScreen,
                // so adding a zeroed CG to 'Panel' blacks out the 3D viewport.
                Transform pstForSkip = HUDManager.Instance.playerScreenTexture != null
                    ? HUDManager.Instance.playerScreenTexture.transform : null;
                foreach (Transform child in scanRoot)
                {
                    if (child == null) continue;
                    if (child.GetComponent<CanvasGroup>() != null) continue;
                    if (pstForSkip != null && IsAncestorOrSelf(child, pstForSkip))
                    {
                        TakeoverBootstrap.Log.LogInfo(
                            $"[TakeoverManager] HideHUD: skipping '{child.name}' (ancestor of playerScreenTexture).");
                        continue;
                    }
                    var cg = child.gameObject.AddComponent<CanvasGroup>();
                    HudSuppressionRegistry.Suppress(cg, HudSuppressionHolders.MonitorTakeover);
                    _addedHUDGroups.Add(cg);
                }

                // Belt-and-suspenders for the item hotbar / toolbar: walk up from
                // itemSlotIconFrames[0] to the first ancestor with a Canvas and
                // add a zeroed CanvasGroup on it. Needed because the hotbar lives
                // on a Canvas that is not always a descendant of scanRoot
                // (varies by LC version / installed mods).
                try
                {
                    var frames = HUDManager.Instance.itemSlotIconFrames;
                    if (frames != null && frames.Length > 0)
                    {
                        foreach (var img in frames)
                        {
                            if (img == null) continue;
                            Transform walk = img.transform;
                            while (walk != null && walk.GetComponent<Canvas>() == null)
                                walk = walk.parent;
                            if (walk == null) continue;
                            // The hotbar frequently shares the same root Canvas as
                            // HUDContainer. That same Canvas also renders
                            // HUDManager.playerScreenTexture (the RawImage that
                            // displays the player camera's RenderTexture — i.e. the
                            // 3D game view). Adding a zeroed CanvasGroup to the
                            // shared root hides the entire viewport, leaving only
                            // our separately-created dialogue overlay visible.
                            // Skip when walk == scanRoot; the per-CanvasGroup pass
                            // above has already hidden the hotbar's own siblings.
                            if (walk == scanRoot)
                            {
                                TakeoverBootstrap.Log.LogInfo(
                                    "[TakeoverManager] HideHUD: hotbar canvas is the shared root — skipping to preserve player view.");
                                break;
                            }
                            HideHUDAncestor(walk);
                            break;   // one hotbar canvas is enough
                        }
                    }
                }
                catch (Exception ex2)
                {
                    TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] HideHUD hotbar: {ex2.Message}");
                }

                // Per-icon hotbar hide: toggle each item slot icon + frame
                // GameObject off individually. This is the safe way to make
                // the hotbar disappear — the canvas itself is shared with the
                // player viewport, so we can't zero it as a whole. #451: the
                // round-end report deactivates these exact GameObjects too, so
                // activeSelf is snapshotted and restored by the shared registry
                // rather than by whichever of us releases last.
                try
                {
                    _hiddenHotbarObjects.Clear();
                    var hud = HUDManager.Instance;
                    if (hud != null)
                    {
                        if (hud.itemSlotIcons != null)
                        {
                            foreach (var img in hud.itemSlotIcons)
                            {
                                if (img == null) continue;
                                var go = img.gameObject;
                                HudSuppressionRegistry.SuppressObject(go, HudSuppressionHolders.MonitorTakeover);
                                _hiddenHotbarObjects.Add(go);
                            }
                        }
                        if (hud.itemSlotIconFrames != null)
                        {
                            foreach (var img in hud.itemSlotIconFrames)
                            {
                                if (img == null) continue;
                                var go = img.gameObject;
                                HudSuppressionRegistry.SuppressObject(go, HudSuppressionHolders.MonitorTakeover);
                                _hiddenHotbarObjects.Add(go);
                            }
                        }
                        TakeoverBootstrap.Log.LogInfo(
                            $"[TakeoverManager] HideHUD: hid {_hiddenHotbarObjects.Count} hotbar icon GameObjects.");
                    }
                }
                catch (Exception ex3)
                {
                    TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] HideHUD hotbar icons: {ex3.Message}");
                }
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] HideHUD: {ex.Message}");
            }
        }

        // Re-entry path: everything we claimed is still claimed, so only the
        // hide itself needs re-applying (vanilla HUDManager lerps alphas back up
        // and re-activates hotbar icons on its own schedule).
        private void ReassertHiddenHUD()
        {
            try
            {
                foreach (var cg in _suppressedHUDGroups)
                    HudSuppressionRegistry.Suppress(cg, HudSuppressionHolders.MonitorTakeover);
                foreach (var cg in _addedHUDGroups)
                    HudSuppressionRegistry.Suppress(cg, HudSuppressionHolders.MonitorTakeover);
                foreach (var go in _hiddenHotbarObjects)
                    HudSuppressionRegistry.SuppressObject(go, HudSuppressionHolders.MonitorTakeover);
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] ReassertHiddenHUD: {ex.Message}");
            }
        }

        private static bool IsAncestorOrSelf(Transform potentialAncestor, Transform t)
        {
            for (Transform w = t; w != null; w = w.parent)
                if (w == potentialAncestor) return true;
            return false;
        }

        private static string GetTransformPath(Transform t)
        {
            if (t == null) return "<null>";
            var sb = new System.Text.StringBuilder(t.name);
            for (Transform p = t.parent; p != null; p = p.parent)
                sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        // Zero-out a CanvasGroup on the given transform, tracking it in the same
        // saved/added lists that RestoreHUD reads. Idempotent per-transform: if
        // a CG is already tracked (either saved or added), no duplicate entry.
        private void HideHUDAncestor(Transform t)
        {
            if (t == null) return;
            var existing = t.GetComponent<CanvasGroup>();
            if (existing != null)
            {
                if (_suppressedHUDGroups.Contains(existing)) return;   // already claimed
                HudSuppressionRegistry.Suppress(existing, HudSuppressionHolders.MonitorTakeover);
                _suppressedHUDGroups.Add(existing);
                TakeoverBootstrap.Log.LogInfo($"[TakeoverManager] HideHUD: hid existing CanvasGroup on '{t.name}'.");
            }
            else
            {
                var cg = t.gameObject.AddComponent<CanvasGroup>();
                HudSuppressionRegistry.Suppress(cg, HudSuppressionHolders.MonitorTakeover);
                _addedHUDGroups.Add(cg);
                TakeoverBootstrap.Log.LogInfo($"[TakeoverManager] HideHUD: added CanvasGroup to '{t.name}'.");
            }
        }

        // Unwinds unconditionally — not gated on HUDManager.Instance, because the
        // claims are direct component references and a takeover that ends across a
        // scene change must still hand every one of them back (#451).
        private void RestoreHUD()
        {
            if (!_hudHidden
                && _suppressedHUDGroups.Count == 0
                && _addedHUDGroups.Count == 0
                && _hiddenHotbarObjects.Count == 0)
                return;

            int groupsRestored = 0;
            int addedDestroyed = 0;
            int hotbarRestored = 0;
            try
            {
                foreach (var cg in _suppressedHUDGroups)
                {
                    if (HudSuppressionRegistry.Release(cg, HudSuppressionHolders.MonitorTakeover))
                        groupsRestored++;
                }
                _suppressedHUDGroups.Clear();

                // Strip the CanvasGroups we added so the underlying HUD roots are
                // left exactly as we found them — but only once nobody else holds
                // them, or the report's suppression would restore onto a destroyed
                // component.
                foreach (var cg in _addedHUDGroups)
                {
                    if (cg == null) continue;
                    HudSuppressionRegistry.Release(cg, HudSuppressionHolders.MonitorTakeover);
                    if (HudSuppressionRegistry.IsSuppressed(cg)) continue;
                    Destroy(cg);
                    addedDestroyed++;
                }
                _addedHUDGroups.Clear();

                // Restore hotbar item-slot GameObjects to their original
                // activeSelf state (we toggled them off individually in HideHUD).
                foreach (var go in _hiddenHotbarObjects)
                {
                    if (HudSuppressionRegistry.ReleaseObject(go, HudSuppressionHolders.MonitorTakeover))
                        hotbarRestored++;
                }
                _hiddenHotbarObjects.Clear();
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] RestoreHUD: {ex.Message}");
            }
            finally
            {
                // Whatever the registry still holds is somebody else's claim, so
                // the takeover is out of the picture either way.
                HudSuppressionRegistry.ReleaseAll(HudSuppressionHolders.MonitorTakeover);
                HudSuppressionRegistry.ReleaseAllObjects(HudSuppressionHolders.MonitorTakeover);
                _suppressedHUDGroups.Clear();
                _addedHUDGroups.Clear();
                _hiddenHotbarObjects.Clear();
                _hudHidden = false;
            }

            TakeoverBootstrap.Log.LogInfo(
                $"[TakeoverManager] RestoreHUD: groups restored={groupsRestored}, " +
                $"added groups destroyed={addedDestroyed}, hotbar objects restored={hotbarRestored}, " +
                $"still suppressed by others: groups={HudSuppressionRegistry.SuppressedGroupCount}, " +
                $"objects={HudSuppressionRegistry.SuppressedObjectCount}.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Step 3 — Video + Monitors
        // ─────────────────────────────────────────────────────────────────────

        private void SetupVideo()
        {
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            if (_useQuotaProgressionPayload)
            {
                var payload = Y4NGZCompany.Core.QuotaProgressionRegistry.GetCurrentPayload(DialoguePool);
                if (Y4NGZCompany.Core.QuotaProgressionRegistry.TryResolveMediaFile(payload.MediaFile, payload.MediaHash, out string path))
                {
                    string extension = System.IO.Path.GetExtension(path);
                    if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        SetupVideoForUrl(path, looping: Y4NGZCompany.Core.QuotaProgressionRegistry.LoopVideo);
                        return;
                    }
                    if (SetupImage(path)) return;
                }
            }
#endif
            SetupDefaultVideo(allowLooseFile: true);
        }

        // The takeover's own video, in the order the media is actually playable
        // (#668):
        //
        //   1. the loose y4ngz_monitor_takeover.mp4 deployed beside this plugin,
        //      played through VideoPlayer.url;
        //   2. the bundled VideoClip.
        //
        // The order is not a preference. The bundle was built with transcoding
        // off, so its clip carries no media — only the authoring machine's
        // source path — and every Prepare on a player's machine answers
        // "Cannot read file". The loose file is the media; the clip is kept
        // because on the authoring machine it still resolves, and because a
        // profile that never received the mp4 must degrade to the old behaviour
        // rather than to nothing.
        //
        // Deliberately NOT marked as a configured source: _usingConfiguredVideoSource
        // drives the watchdog's "hand this back to the bundled clip" swap, and
        // handing the DEFAULT video back to a clip that cannot decode would spend
        // a four-second timeout to arrive somewhere worse.
        private void SetupDefaultVideo(bool allowLooseFile)
        {
            string loosePath = allowLooseFile ? TakeoverBootstrap.TakeoverVideoPath : null;
            if (!string.IsNullOrEmpty(loosePath))
            {
                _looseDefaultVideoInUse = true;
                SetupVideoForUrl(loosePath, looping: true, configuredSource: false);
                return;
            }

            _looseDefaultVideoInUse = false;
            if (TakeoverBootstrap.TakeoverVideoClip == null)
            {
                TakeoverBootstrap.Log.LogWarning(
                    "[TakeoverManager] No takeover video available: neither the loose mp4 nor the bundled "
                    + "clip resolved. The sequence still runs; the monitors will carry CRT static.");
                EnsureRenderTexture();
                return;
            }

            SetupVideoFor(TakeoverBootstrap.TakeoverVideoClip, looping: true);
        }

        // The RT outlives every VideoPlayer swap. Downstream state — the GI
        // property-block lease, the MapScreenVideo blit loop, the RawImage
        // redirect and every material write — is bound to this texture by
        // identity, so replacing the player without replacing the texture
        // means a fallback needs no rebinding anywhere (#668).
        private void EnsureRenderTexture()
        {
            if (_renderTexture != null) return;

            _renderTexture = CreateMonitorRenderTexture();

            TakeoverBootstrap.Log.LogInfo(
                $"[Diag/RT] constructed: id={_renderTexture.GetInstanceID()} " +
                $"size={_renderTexture.width}x{_renderTexture.height} fmt={_renderTexture.format} name='{_renderTexture.name}'");
        }

        /// <summary>
        /// One 1080p ARGB32 monitor render texture, cleared to black.
        ///
        /// <para>Cleared up front for the reason <see cref="EnsureRenderTexture"/> has always
        /// cleared: an uncleared RT can read as bright green, and every surface bound to it can
        /// legitimately be drawn before the first decoded frame lands. That must look like a
        /// dark screen, not a fault. Shared with the #662 extra media players so a second
        /// texture cannot quietly acquire different clear behaviour.</para>
        /// </summary>
        private RenderTexture CreateMonitorRenderTexture()
        {
            var texture = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32);
            texture.Create();
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = texture;
                GL.Clear(true, true, Color.black);
            }
            catch { }
            finally { RenderTexture.active = previous; }
            return texture;
        }

        private void SetupVideoForUrl(string path, bool looping, bool configuredSource = true)
        {
            EnsureRenderTexture();
            _usingConfiguredVideoSource = configuredSource;
            _videoPlayer = gameObject.AddComponent<VideoPlayer>();
            // #861: before the source. In the native fixture a player that was given its url while
            // playOnAwake was still true started by itself and never fed the AudioSource routed
            // below, whatever was set afterwards; with playOnAwake off first, the same player is
            // heard and starts only at PrimeVideoForDisplay's Play().
            _videoPlayer.playOnAwake = false;
            _videoPlayer.source = VideoSource.Url;
            _videoPlayer.url = path;
            _videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            _videoPlayer.targetTexture = _renderTexture;
            _videoPlayer.isLooping = looping;
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            if (configuredSource && _soundtrackPlan.Kind == TakeoverSoundtrackKind.ReuseVideoTrack)
            {
                // #715: the soundtrack link and the media link are the same video. Bind this
                // player's own track to the soundtrack source instead of opening the file twice.
                _videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
                _videoPlayer.controlledAudioTrackCount = 1;
                // #861: enabled explicitly, because WatchSoundtrack confirms the soundtrack only
                // on a prepared, enabled track routed to its source.
                _videoPlayer.EnableAudioTrack(0, true);
                _videoPlayer.SetTargetAudioSource(0, EnsureSoundtrackSource());
            }
            else
            {
                // Media audio is a property of USER-CONFIGURED media. The default
                // takeover video is scored by the mumble/alarm/drone layer this class
                // already drives, and letting its own track through would double it.
                // Precedence (#715): a playing soundtrack mutes the media's own track.
                _videoPlayer.audioOutputMode =
                    configuredSource
                    && Y4NGZCompany.Core.QuotaProgressionRegistry.PlayVideoSound
                    && !_soundtrackPlan.Active
                        ? VideoAudioOutputMode.Direct
                        : VideoAudioOutputMode.None;
            }
#else
            _videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
#endif
            TakeoverBootstrap.Log.LogInfo(
                $"[TakeoverManager] Preparing {(configuredSource ? "configured" : "default")} MP4 "
                + $"'{System.IO.Path.GetFileName(path)}' through VideoPlayer.url.");
            HardenAndPrepare(_videoPlayer);
        }

        private bool SetupImage(string path)
        {
            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                if (bytes.Length > 32 * 1024 * 1024)
                {
                    TakeoverBootstrap.Log.LogWarning("[TakeoverManager] Configured image exceeds the 32 MB limit; using bundled video.");
                    return false;
                }
                _customImageTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!_customImageTexture.LoadImage(bytes, markNonReadable: false))
                    return false;
                _customImageTexture.name = "Y4NGZ_CustomTakeoverImage";
                EnsureRenderTexture();
                Graphics.Blit(_customImageTexture, _renderTexture);
                TakeoverBootstrap.Log.LogInfo($"[TakeoverManager] Loaded configured image '{System.IO.Path.GetFileName(path)}'.");
                return true;
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] Configured image failed: {ex.Message}");
                return false;
            }
        }

        // Video setup for a bundled VideoClip (the fallback when the loose mp4 is
        // missing); shares the RT/VideoPlayer plumbing with the url path.
        private void SetupVideoFor(VideoClip clip, bool looping)
        {
            if (clip == null) return;

            EnsureRenderTexture();
            _usingConfiguredVideoSource = false;

            _videoPlayer = gameObject.AddComponent<VideoPlayer>();
            // #861: before the clip, for the reason SetupVideoForUrl gives.
            _videoPlayer.playOnAwake     = false;
            _videoPlayer.clip            = clip;
            _videoPlayer.renderMode      = VideoRenderMode.RenderTexture;
            _videoPlayer.targetTexture   = _renderTexture;
            _videoPlayer.isLooping       = looping;
            _videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
            TakeoverBootstrap.Log.LogInfo(
                $"[Diag/RT] VP.targetTexture id={(_videoPlayer.targetTexture != null ? _videoPlayer.targetTexture.GetInstanceID().ToString() : "NULL")} " +
                $"match={_videoPlayer.targetTexture == _renderTexture} clip='{(_videoPlayer.clip != null ? _videoPlayer.clip.name : "null")}'");
            HardenAndPrepare(_videoPlayer);
        }

        // ── Video decode hardening (#668 follow-up) ───────────────────────────
        //
        // The takeover shipped without ever asking the VideoPlayer why it was
        // not decoding. On this profile prepare simply never completes:
        // `isPrepared=False frame=-1/121` for the whole sequence. frameCount
        // comes off the imported VideoClip asset, not from a decoder, so
        // "metadata readable, frame index still -1" means the decoder never
        // opened the media at all — it does not mean decoding is merely slow.
        //
        // Unity reports exactly that condition through `errorReceived`, which
        // nothing here was subscribed to, so the one authoritative sentence
        // about the failure was being thrown away every run. Subscribing is
        // the proof mechanism; everything else below closes the stall causes
        // that produce this same silent symptom, all of which are cheap and
        // independent of one another:
        //
        //  • a carrier GameObject that is inactive or a disabled component —
        //    Prepare() on either is a silent no-op forever;
        //  • an audio track the player is nominally driving with no output —
        //    with VideoAudioOutputMode.None the track count must be zeroed
        //    explicitly or the media open can hang waiting on an audio sink;
        //  • timeReference/waitForFirstFrame settings that make the player
        //    wait on a clock that never advances.
        private void HardenAndPrepare(VideoPlayer player)
        {
            if (player == null) return;

            try
            {
                // Never let the player block on an audio sink we do not want.
                if (player.audioOutputMode == VideoAudioOutputMode.None)
                {
                    ushort tracks = player.audioTrackCount;
                    for (ushort i = 0; i < tracks; i++)
                        player.EnableAudioTrack(i, false);
                    player.controlledAudioTrackCount = 0;
                }

                player.skipOnDrop       = true;
                player.playbackSpeed    = 1f;
                player.timeReference    = VideoTimeReference.Freerun;
                // Do not gate the first presented frame on the player's own
                // readiness handshake; we drive presentation from the RT.
                player.waitForFirstFrame = false;

                player.errorReceived    -= OnVideoErrorReceived;
                player.errorReceived    += OnVideoErrorReceived;
                player.prepareCompleted -= OnVideoPrepareCompleted;
                player.prepareCompleted += OnVideoPrepareCompleted;
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning(
                    $"[TakeoverManager] Video hardening failed: {ex.Message}");
            }

            LogVideoCarrierState("setup");
            IssuePrepare(player, "initial");
        }

        // Prepare() is only meaningful on an active carrier with an enabled
        // component. Verify — and repair — that before every issue, and say so
        // in one line when it was not true, because that is the difference
        // between "slow decoder" and "we asked a disabled component to work".
        private void IssuePrepare(VideoPlayer player, string reason)
        {
            if (player == null) return;

            try
            {
                if (!player.gameObject.activeInHierarchy)
                {
                    TakeoverBootstrap.Log.LogWarning(
                        "[TakeoverManager] Video cannot prepare: the carrier GameObject " +
                        $"'{player.gameObject.name}' is inactive. Reactivating it.");
                    player.gameObject.SetActive(true);
                }
                if (!player.enabled)
                {
                    TakeoverBootstrap.Log.LogWarning(
                        "[TakeoverManager] Video cannot prepare: the VideoPlayer component was " +
                        "disabled. Re-enabling it.");
                    player.enabled = true;
                }
                player.Prepare();
                TakeoverBootstrap.Log.LogInfo($"[TakeoverManager] Video Prepare() issued ({reason}).");
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning(
                    $"[TakeoverManager] Video Prepare() ({reason}) threw: {ex.Message}");
            }
        }

        // Unity's own words for why the media would not open. This is the line
        // that tells a codec problem apart from a state problem.
        private void OnVideoErrorReceived(VideoPlayer source, string message)
        {
            TakeoverBootstrap.Log.LogError(
                $"[TakeoverManager] VideoPlayer error: {message} " +
                $"(clip='{(source != null && source.clip != null ? source.clip.name : "null")}' " +
                $"url='{(source != null ? source.url : string.Empty)}'). " +
                "This is why the takeover has no picture; the static fallback will carry the sequence.");
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #861: when this player's track IS the soundtrack, its error is the soundtrack's too.
            if (source != null && source == _videoPlayer && _soundtrackPlan.Kind == TakeoverSoundtrackKind.ReuseVideoTrack)
                FailSoundtrack($"the takeover video it shares reported an error: {message}");
#endif
        }

        private void OnVideoPrepareCompleted(VideoPlayer source)
        {
            TakeoverBootstrap.Log.LogInfo(
                $"[TakeoverManager] Video prepared: {source?.width}x{source?.height} " +
                $"frames={source?.frameCount} audioTracks={source?.audioTrackCount}.");
        }

        private void LogVideoCarrierState(string site)
        {
            VideoPlayer player = _videoPlayer;
            if (player == null)
            {
                TakeoverBootstrap.Log.LogInfo($"[Diag/Video] carrier ({site}): no VideoPlayer.");
                return;
            }

            GameObject carrier = player.gameObject;
            VideoClip clip = player.clip;
            TakeoverBootstrap.Log.LogInfo(
                $"[Diag/Video] carrier ({site}): go='{carrier.name}' activeSelf={carrier.activeSelf} " +
                $"activeInHierarchy={carrier.activeInHierarchy} hideFlags={carrier.hideFlags} " +
                $"scene='{carrier.scene.name}' componentEnabled={player.enabled} " +
                $"players-on-carrier={carrier.GetComponents<VideoPlayer>().Length} " +
                $"source={player.source} audioOut={player.audioOutputMode} " +
                $"audioTracks={player.audioTrackCount} controlledTracks={player.controlledAudioTrackCount} " +
                $"clip='{(clip != null ? clip.name : "null")}' clipLen={(clip != null ? clip.length : 0d):F2} " +
                // Dimensions matter here, not just for completeness. The bundled
                // takeover clip is 1916x1080 — a width that is not a multiple of
                // 16 — and the bundle is built with enableTranscoding = false, so
                // that odd-width H.264 elementary stream is handed to the platform
                // decoder untouched. If prepare never completes, this pair of
                // numbers plus any 'VideoPlayer error' line above is the evidence.
                $"clipSize={(clip != null ? clip.width : 0)}x{(clip != null ? clip.height : 0)} " +
                $"clipFps={(clip != null ? clip.frameRate : 0f):F2} " +
                $"clipPath='{(clip != null ? clip.originalPath : string.Empty)}'");
        }

        // Short courtesy wait only. SetupVideo now runs at the head of the
        // sequence, so by the time we get here the decoder has already had the
        // dim-lights ramp to work in; this grace exists so a nearly-ready
        // player still hands over a decoded first frame.
        private const float VideoPrimeGraceSeconds = 1.0f;

        // How long a user-configured source gets to decode before the bundled
        // clip takes the screen. This runs in the background — it is not, and
        // must never again become, a gate on the sequence.
        private const float ConfiguredVideoFallbackSeconds = 4f;

        // Prime the VideoPlayer for display WITHOUT holding the sequence
        // hostage to its prepare timeout.
        //
        // #669: the previous version blocked here for up to two full 4s
        // timeouts, so the dialogue, the mumble loop and the monitor handoff
        // all started ~7s late whenever the decoder was slower than 4s — and
        // the comment at the call site already recorded that prepare "can take
        // ~4.5s", i.e. longer than the timeout it was measured against.
        //
        // #668: worse, that timeout path ended in `yield break` with Play()
        // never called, so a slow decoder produced a permanently black
        // RenderTexture. Every downstream surface — vanilla materials, the
        // MapScreenVideo blit and GeneralImprovements' property-block lease
        // alike — was then faithfully painting black. Play() is now
        // unconditional: it implies Prepare(), so an unprepared player still
        // starts decoding and the picture appears the moment it is ready.
        private IEnumerator PrimeVideoForDisplay()
        {
            if (_videoPlayer == null) yield break;

            float elapsed = 0f;
            while (!_videoPlayer.isPrepared && elapsed < VideoPrimeGraceSeconds)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            bool prepared = _videoPlayer.isPrepared;
            _videoPlayer.Play();

            if (prepared)
            {
                // WaitForEndOfFrame so the render pipeline has flushed the
                // first decoded frame before the RT reaches monitor materials.
                yield return new WaitForEndOfFrame();
            }
            else
            {
                TakeoverBootstrap.Log.LogInfo(
                    "[TakeoverManager] Video is still decoding at monitor-handoff time; the sequence continues " +
                    "and the picture appears as soon as the first frame lands.");
            }

            if (_videoFallbackCoroutine != null) StopCoroutine(_videoFallbackCoroutine);
            _videoFallbackCoroutine = StartCoroutine(WatchVideoDecode(elapsed));
        }

#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
        // Longest a HoldForWholeVideo hold waits for the configured
        // source to prepare before giving up and leaving the configured
        // Takeover Duration in place. Generous because a cached link is read
        // off disk by the same decoder that PrimeVideoForDisplay stopped
        // waiting on after one second, and short against the 30-minute clamp.
        private const float MediaLengthPrepareGraceSeconds = 30f;

        // Upper bound on a media-length hold (#661): 30 minutes.
        private const float MediaLengthMaxSeconds = 1800f;

        // Resolve the hold length from the configured media (#661).
        //
        // Two things make this a coroutine rather than the inline test the
        // branch first wrote. VideoPlayer.length is 0 until isPrepared, and
        // since #669 nothing in the sequence waits for prepare; and the
        // watchdog may hand the screen to the bundled clip, whose length must
        // never become the hold. Watching _usingConfiguredVideoSource covers
        // both: SwapToDefaultVideo clears it, so a fallback leaves the
        // configured Takeover Duration standing.
        private IEnumerator AdoptConfiguredMediaLength()
        {
            _mediaLengthPending = true;
            _mediaLengthSeconds = 0d;

            float waited = 0f;
            while (_takeoverActive
                   && _usingConfiguredVideoSource
                   && _videoPlayer != null
                   && !_videoPlayer.isPrepared
                   && waited < MediaLengthPrepareGraceSeconds)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            if (_takeoverActive
                && _usingConfiguredVideoSource
                && _videoPlayer != null
                && _videoPlayer.isPrepared
                && _videoPlayer.length > 0.5d)
            {
                _mediaLengthSeconds = Mathf.Clamp((float)_videoPlayer.length, 3f, MediaLengthMaxSeconds);
                TakeoverBootstrap.Log.LogInfo(
                    $"[TakeoverManager] HoldForWholeVideo: holding for the configured video's "
                    + $"{_mediaLengthSeconds:F1}s (reported {_videoPlayer.length:F1}s after {waited:F1}s of prepare).");
            }
            else
            {
                TakeoverBootstrap.Log.LogInfo(
                    "[TakeoverManager] HoldForWholeVideo: no usable length from the configured video after "
                    + $"{waited:F1}s ({(_usingConfiguredVideoSource ? "still the configured source" : "fell back to the default video")}); "
                    + "keeping the configured Takeover Duration.");
            }

            _mediaLengthPending = false;
        }
#endif

        // How long the whole decode gets before the monitors are given visible
        // content that does not depend on a decoder at all.
        private const float SignalLostAfterSeconds = 6f;

        // Spacing of the Prepare() re-issues. A player that silently declined
        // the first request usually declines every one of them — but re-issuing
        // is nearly free, and it is the only thing that rescues the genuine
        // "asked before the carrier was ready" case.
        private const float PrepareRetrySeconds = 2f;

        // Always-on decode watchdog. It never blocks the sequence; it escalates
        // in three steps, and every step is visible in the log:
        //   1. a configured source that has not decoded hands over to the
        //      bundled clip (the RT is reused, so nothing needs rebinding);
        //   2. Prepare() is re-issued, with the carrier verified active and
        //      enabled first, and the carrier state dumped alongside;
        //   3. if nothing has decoded at all, the monitors go to CRT static
        //      rather than staying a black wall — a takeover that looks
        //      hijacked is a far better failure than one that looks broken.
        private IEnumerator WatchVideoDecode(float alreadyWaited)
        {
            float elapsed = alreadyWaited;
            float nextRetryAt = elapsed + PrepareRetrySeconds;
            bool swapped = false;

            while (_takeoverActive && elapsed < SignalLostAfterSeconds)
            {
                if (_videoPlayer != null && _videoPlayer.isPrepared)
                {
                    // Decoded after all. Make sure it is actually running and
                    // that no static is covering it.
                    if (!_videoPlayer.isPlaying) _videoPlayer.Play();
                    StopSignalLostStatic("the video decoded");
                    _videoFallbackCoroutine = null;
                    yield break;
                }

                if (!swapped
                    && _usingConfiguredVideoSource
                    && elapsed >= ConfiguredVideoFallbackSeconds)
                {
                    TakeoverBootstrap.Log.LogWarning(
                        "[TakeoverManager] Configured video did not prepare in time; using the default takeover video.");
                    SwapToDefaultVideo();
                    swapped = true;
                    nextRetryAt = elapsed + PrepareRetrySeconds;
                }
                else if (elapsed >= nextRetryAt)
                {
                    LogVideoCarrierState("retry");
                    IssuePrepare(_videoPlayer, "watchdog retry");
                    if (_videoPlayer != null && !_videoPlayer.isPlaying) _videoPlayer.Play();
                    nextRetryAt = elapsed + PrepareRetrySeconds;
                }

                elapsed += Time.deltaTime;
                yield return null;
            }

            _videoFallbackCoroutine = null;
            if (!_takeoverActive) yield break;
            if (_videoPlayer != null && _videoPlayer.isPrepared) yield break;

            LogVideoCarrierState("signal-lost");
            TakeoverBootstrap.Log.LogError(
                $"[TakeoverManager] No video frame decoded within {SignalLostAfterSeconds:F0}s. The monitors " +
                "are switching to CRT static so the takeover is not a black wall. If a 'VideoPlayer error' " +
                "line appears above, that is the authoritative reason; if none does, the decoder accepted the " +
                "media and never produced a frame.");
            StartSignalLostStatic();
        }


        // ───── #662 — Extra media players ─────
        //
        // Everything below is additive and reached only when a resolved plan is
        // active. The primary player keeps every asymmetric privilege it had:
        // it alone gets SwapToDefaultVideo, the media-length hold, the signal-
        // lost static, the map-screen blit, the bridge handoff and any media
        // audio. An extra player is a picture source and nothing else.

        /// <summary>
        /// Opens the extra decoders for a resolved plan. Player 0 is the primary that
        /// <see cref="SetupVideo"/> already built from the quota's own media, so the pool fills
        /// players 1..n-1 - a deliberate departure from the sketch that had the pool own player
        /// 0 as well. Rebinding the primary would move the media-length hold (#661), the
        /// soundtrack's ReuseVideoTrack case (#715) and the configured-source watchdog (#668)
        /// onto a different file, which is exactly the regression this feature must not have.
        /// </summary>
        private void SetupAdditionalMediaPlayers(TakeoverMediaPlan plan, bool looping)
        {
            if (!plan.Active) return;

            // The cap counts decoders, and the primary is a decoder, so the pool may open at
            // most cap-1 of them. A cap of 1 therefore opens none, which is the documented
            // "cap lower than pool" outcome rather than a special case.
            int extras = Math.Min(Math.Max(0, plan.MaxPlayers - 1), plan.Sources.Count);
            for (int index = 0; index < extras; index++)
                CreateMediaPlayer(index + 1, plan.Sources[index], looping);

            TakeoverBootstrap.Log.LogInfo(
                $"[TakeoverManager] Per-monitor media: opened {_extraMediaPlayers.Count} extra player(s) "
                + $"from a pool of {plan.Sources.Count} resolved source(s) under a cap of {plan.MaxPlayers}, "
                + $"seed {plan.Seed}.");
        }

        private void CreateMediaPlayer(int index, string source, bool looping)
        {
            var entry = new TakeoverExtraMediaPlayer
            {
                Index = index,
                Source = source,
                Texture = CreateMonitorRenderTexture(),
            };
            entry.Texture.name = $"Y4NGZ_TakeoverMedia{index}";
            _extraMediaPlayers.Add(entry);

            string extension = System.IO.Path.GetExtension(source ?? string.Empty);
            if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                // A still costs no decoder at all: one blit and the texture is finished.
                if (TryBlitImageInto(source, entry)) return;
                TakeoverBootstrap.Log.LogWarning(
                    $"[TakeoverManager] Per-monitor media source '{System.IO.Path.GetFileName(source)}' "
                    + "could not be loaded as an image; that surface mirrors the primary instead.");
                entry.MirrorLoop = StartCoroutine(MirrorPrimaryLoop(entry));
                return;
            }

            var player = gameObject.AddComponent<VideoPlayer>();
            // #861: before the url, for the reason SetupVideoForUrl gives.
            player.playOnAwake = false;
            player.source = VideoSource.Url;
            player.url = source;
            player.renderMode = VideoRenderMode.RenderTexture;
            player.targetTexture = entry.Texture;
            player.isLooping = looping;
            // Unconditional. The media-audio toggle and the #715 soundtrack are properties of
            // THE takeover's media, of which there is exactly one; six simultaneous audio tracks
            // would be noise, not a feature.
            player.audioOutputMode = VideoAudioOutputMode.None;
            entry.Player = player;
            HardenAndPrepare(player);
            // Play() implies Prepare(), and nothing waits on this player - the picture appears
            // on its surface the moment the decoder produces one (#668).
            player.Play();
            entry.Watchdog = StartCoroutine(WatchMediaPlayerDecode(entry));
        }

        private bool TryBlitImageInto(string path, TakeoverExtraMediaPlayer entry)
        {
            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                if (bytes.Length > 32 * 1024 * 1024) return false;
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(bytes, markNonReadable: false))
                {
                    Destroy(texture);
                    return false;
                }
                texture.name = $"Y4NGZ_TakeoverMediaImage{entry.Index}";
                entry.ImageTexture = texture;
                Graphics.Blit(texture, entry.Texture);
                return true;
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning(
                    $"[TakeoverManager] Per-monitor media image load failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The extra players' watchdog. Deliberately NOT <see cref="WatchVideoDecode"/>: that one
        /// owns the primary and escalates to <see cref="SwapToDefaultVideo"/>, and an extra
        /// player must never spin up a second bundled-clip decoder - that would spend a decoder
        /// slot to show the content some other surface is already showing. It mirrors the
        /// primary instead, so the surface stays lit and its texture identity never changes.
        /// </summary>
        private IEnumerator WatchMediaPlayerDecode(TakeoverExtraMediaPlayer entry)
        {
            float elapsed = 0f;
            while (_takeoverActive && elapsed < ConfiguredVideoFallbackSeconds)
            {
                if (entry.Player == null) { entry.Watchdog = null; yield break; }
                if (entry.Player.isPrepared)
                {
                    if (!entry.Player.isPlaying) entry.Player.Play();
                    entry.Watchdog = null;
                    yield break;
                }
                elapsed += Time.deltaTime;
                yield return null;
            }

            entry.Watchdog = null;
            if (!_takeoverActive) yield break;
            if (entry.Player != null && entry.Player.isPrepared) yield break;

            TakeoverBootstrap.Log.LogWarning(
                $"[TakeoverManager] Per-monitor media source '{System.IO.Path.GetFileName(entry.Source)}' "
                + $"did not decode within {ConfiguredVideoFallbackSeconds:F0}s; its surfaces mirror the "
                + "primary video for the rest of the takeover.");
            if (entry.Player != null)
            {
                try { entry.Player.Stop(); } catch { }
                entry.Player.errorReceived -= OnVideoErrorReceived;
                entry.Player.prepareCompleted -= OnVideoPrepareCompleted;
                Destroy(entry.Player);
                entry.Player = null;
            }
            entry.MirrorLoop = StartCoroutine(MirrorPrimaryLoop(entry));
        }

        private IEnumerator MirrorPrimaryLoop(TakeoverExtraMediaPlayer entry)
        {
            while (_takeoverActive && entry.Texture != null && _renderTexture != null)
            {
                try { Graphics.Blit(_renderTexture, entry.Texture); }
                catch { }
                yield return null;
            }
            entry.MirrorLoop = null;
        }

        /// <summary>
        /// Silences the extra players for the CRT transition. Stopping the mirror loops is the
        /// point: a loop still running would repaint the primary's picture over the white / snow
        /// / black frames <see cref="SetAllMonitorTexture"/> just wrote.
        /// </summary>
        private void StopAdditionalMediaPlayback()
        {
            foreach (TakeoverExtraMediaPlayer entry in _extraMediaPlayers)
            {
                if (entry == null) continue;
                if (entry.Watchdog != null) { StopCoroutine(entry.Watchdog); entry.Watchdog = null; }
                if (entry.MirrorLoop != null) { StopCoroutine(entry.MirrorLoop); entry.MirrorLoop = null; }
                if (entry.Player != null) { try { entry.Player.Stop(); } catch { } }
            }
        }

        /// <summary>
        /// Teardown, in the one order that cannot leave a surface reading a released texture:
        /// coroutines, then the player that targets the texture, then the texture. Called from
        /// <see cref="RestoreAll"/> AFTER the bridge has ended the external takeover, for the
        /// same reason the primary's release waits for it - a released RenderTexture renders as
        /// bright green.
        /// </summary>
        private void DestroyAdditionalMediaPlayers()
        {
            foreach (TakeoverExtraMediaPlayer entry in _extraMediaPlayers)
            {
                if (entry == null) continue;
                if (entry.Watchdog != null) { StopCoroutine(entry.Watchdog); entry.Watchdog = null; }
                if (entry.MirrorLoop != null) { StopCoroutine(entry.MirrorLoop); entry.MirrorLoop = null; }
                if (entry.Player != null)
                {
                    try { entry.Player.Stop(); } catch { }
                    entry.Player.errorReceived -= OnVideoErrorReceived;
                    entry.Player.prepareCompleted -= OnVideoPrepareCompleted;
                    Destroy(entry.Player);
                    entry.Player = null;
                }
                if (entry.Texture != null)
                {
                    entry.Texture.Release();
                    Destroy(entry.Texture);
                    entry.Texture = null;
                }
                if (entry.ImageTexture != null)
                {
                    Destroy(entry.ImageTexture);
                    entry.ImageTexture = null;
                }
            }
            _extraMediaPlayers.Clear();
            _mediaPaintSites.Clear();
            _mediaPlan = default;
        }

        /// <summary>Texture for a player slot; slot 0 is always the primary.</summary>
        private Texture MediaTextureFor(int playerIndex)
        {
            if (playerIndex <= 0) return _renderTexture;
            int extra = playerIndex - 1;
            if (extra >= _extraMediaPlayers.Count) return _renderTexture;
            return _extraMediaPlayers[extra].Texture != null
                ? _extraMediaPlayers[extra].Texture
                : (Texture)_renderTexture;
        }

        // Replace the current player with the takeover's own default video,
        // keeping the RT. Reached only from the watchdog, and only for a
        // user-configured source that did not decode.
        //
        // #668: this used to go straight to the bundled clip. The default video
        // is now the loose mp4 with the clip behind it, so this defers to
        // SetupDefaultVideo and inherits the same order — except that a loose
        // file which is already the failing source is skipped, because retrying
        // the media that just timed out is how the old code spent two full
        // timeouts to arrive back where it started.
        private void SwapToDefaultVideo()
        {
            bool allowLooseFile = !_looseDefaultVideoInUse;
            if (!allowLooseFile && TakeoverBootstrap.TakeoverVideoClip == null)
            {
                TakeoverBootstrap.Log.LogWarning(
                    "[TakeoverManager] No default takeover video to fall back to; leaving the current source in place.");
                return;
            }

#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #861: the player about to be destroyed carries the soundtrack's track too.
            if (_soundtrackPlan.Kind == TakeoverSoundtrackKind.ReuseVideoTrack)
                FailSoundtrack("the takeover video it shares did not prepare in time and was replaced by the default video");
#endif

            if (_videoPlayer != null)
            {
                try { _videoPlayer.Stop(); } catch { }
                Destroy(_videoPlayer);
                _videoPlayer = null;
            }

            SetupDefaultVideo(allowLooseFile);
            // Play() rather than Prepare()-then-wait, for the reason
            // PrimeVideoForDisplay gives: waiting is what lost the picture.
            _videoPlayer?.Play();
        }

        // ── "Signal lost" static fallback ─────────────────────────────────────
        //
        // The takeover's whole premise is that something has seized the ship's
        // monitors. A wall of black reads as a broken mod; a wall of CRT snow
        // reads as the seizure it is meant to be, and it keeps the dialogue,
        // the alarm and the lighting sequence coherent even when no frame is
        // ever decoded. It writes into the SAME RenderTexture everything else
        // is already bound to, so every surface — vanilla materials, the
        // MapScreenVideo blit and GI's property-block lease — picks it up with
        // no rebinding.
        private Coroutine _signalLostCoroutine;
        private Texture2D[] _signalLostFrames;

        private void StartSignalLostStatic()
        {
            if (_signalLostCoroutine != null || _renderTexture == null) return;
            _signalLostCoroutine = StartCoroutine(SignalLostStaticLoop());
        }

        private void StopSignalLostStatic(string reason)
        {
            if (_signalLostCoroutine == null) return;
            StopCoroutine(_signalLostCoroutine);
            _signalLostCoroutine = null;
            DestroySignalLostFrames();
            TakeoverBootstrap.Log.LogInfo(
                $"[TakeoverManager] CRT static fallback stopped: {reason}.");
        }

        private void DestroySignalLostFrames()
        {
            if (_signalLostFrames == null) return;
            for (int i = 0; i < _signalLostFrames.Length; i++)
                if (_signalLostFrames[i] != null) Destroy(_signalLostFrames[i]);
            _signalLostFrames = null;
        }

        private IEnumerator SignalLostStaticLoop()
        {
            // A handful of pre-generated frames cycled on a timer: snow has to
            // move to read as snow, and regenerating noise every frame would
            // allocate a texture per frame for the length of the takeover.
            const int FrameCount = 6;
            _signalLostFrames = new Texture2D[FrameCount];
            for (int i = 0; i < FrameCount; i++)
                _signalLostFrames[i] = CRTStatic.CreateNoiseTexture(320, 180);

            var wait = new WaitForSeconds(0.05f);
            int index = 0;
            while (_takeoverActive && _renderTexture != null)
            {
                // A real frame beat us to it — stand down rather than paint over it.
                if (_videoPlayer != null && _videoPlayer.isPrepared)
                {
                    _signalLostCoroutine = null;
                    DestroySignalLostFrames();
                    TakeoverBootstrap.Log.LogInfo(
                        "[TakeoverManager] CRT static fallback stopped: the video decoded late.");
                    yield break;
                }

                Texture2D frame = _signalLostFrames[index];
                index = (index + 1) % FrameCount;
                if (frame != null)
                {
                    try { Graphics.Blit(frame, _renderTexture); }
                    catch { }
                }
                yield return wait;
            }

            _signalLostCoroutine = null;
            DestroySignalLostFrames();
        }

        private void OverrideMonitors()
        {
            // Release any narrower GI/vanilla quota lease before this method's
            // full-wall lease snapshots the monitor state it must restore.
            QuotaUnlockAnnouncement.AbortForExternalTakeover();

            if (_generalImprovementsLeaseCoroutine != null)
            {
                StopCoroutine(_generalImprovementsLeaseCoroutine);
                _generalImprovementsLeaseCoroutine = null;
            }
            _generalImprovementsMonitorLease?.Release();
            _generalImprovementsMonitorLease = null;

            _overriddenRenderers.Clear();
            _savedHDRPState.Clear();
            _topMonitorOverlayImages.Clear();
            _disabledComponents.Clear();
            _disabledMonitorGraphics.Clear();
            _mediaPaintSites.Clear();
            _videoReelRawImage = null;

            var sor = StartOfRound.Instance;
            if (sor == null || _renderTexture == null) return;

            // #599: with GeneralImprovements' UseBetterMonitors on, the vanilla
            // wall is still in the hierarchy but its MeshRenderers are disabled
            // and the visible screens are GI's own MonitorGroup(Clone) meshes.
            // Every section below either retargets onto those screens or stands
            // down with a log line — the one thing it must never do is paint the
            // hidden vanilla meshes and report a successful takeover.
            bool giMonitors = GeneralImprovementsMonitors.BetterMonitorsActive;

            // ── A) Top monitors ────────────────────────────────────────────────────
            // The top monitors are WorldSpace canvas elements. The BG Image components
            // (profitQuotaMonitorBGImage, deadlineMonitorBGImage) are RectTransforms
            // that are sized and positioned to match EXACTLY the physical screen face
            // of each monitor. We place a RawImage at the same RectTransform as each BG
            // so the video fits pixel-perfectly within the monitor boundaries.
            //
            // We do NOT override the Cube mesh mainTexture because that mesh covers the
            // full bezel/surround area, causing the video to bleed outside the screen face.
            // Under UseBetterMonitors there is no world-space quota/deadline
            // canvas on the face at all: GI renders those values into its own
            // screen materials. Both the vanilla rects and GI's clones are
            // parked off-face, so an overlay anchored to either is invisible and
            // disabling the graphics under it changes nothing. The GI screen
            // pass in section D covers these two monitors instead.
            if (giMonitors)
            {
                GeneralImprovementsMonitors.LogStandDown(
                    TakeoverBootstrap.Log,
                    "the takeover's top-monitor canvas overlay",
                    "quota/deadline are rendered into GI's screen materials, not the world-space canvas; " +
                    "the GI screen pass paints those monitors instead");
            }
            else try
            {
                // Resolve what is actually ON the monitor face. With
                // GeneralImprovements installed the visible quota/deadline
                // graphics are clones ('ProfitQuotaBG5', …), not the vanilla
                // StartOfRound references — disabling only the vanilla ones
                // left the quota counter reading through the whole takeover.
                Image profitBG            = ResolveVisibleMonitorGraphic(sor, sor.profitQuotaMonitorBGImage, "ProfitQuotaBG");
                Image deadlineBG          = ResolveVisibleMonitorGraphic(sor, sor.deadlineMonitorBGImage,    "DeadlineBG");
                TextMeshProUGUI profitTx  = ResolveVisibleMonitorGraphic(sor, sor.profitQuotaMonitorText,    "ProfitQuotaText");
                TextMeshProUGUI deadlineTx = ResolveVisibleMonitorGraphic(sor, sor.deadlineMonitorText,      "DeadlineText");

                DisableMonitorGraphic(sor.profitQuotaMonitorText);
                DisableMonitorGraphic(sor.profitQuotaMonitorBGImage);
                DisableMonitorGraphic(sor.deadlineMonitorText);
                DisableMonitorGraphic(sor.deadlineMonitorBGImage);
                DisableMonitorGraphic(profitTx);
                DisableMonitorGraphic(profitBG);
                DisableMonitorGraphic(deadlineTx);
                DisableMonitorGraphic(deadlineBG);

                _topMonitorOverlayImages.Clear();

                // Place a video RawImage exactly where each BG Image sits.
                // CopyRectTransform clones anchorMin/Max, pivot, anchoredPosition, sizeDelta.
                // Anchor to the VISIBLE graphic — the vanilla rect may be a
                // hidden off-face canvas when clones are in play.
                AddOverlayMatchingBG(profitBG);
                AddOverlayMatchingBG(deadlineBG);
                if (profitBG != sor.profitQuotaMonitorBGImage)
                    AddOverlayMatchingBG(sor.profitQuotaMonitorBGImage);
                if (deadlineBG != sor.deadlineMonitorBGImage)
                    AddOverlayMatchingBG(sor.deadlineMonitorBGImage);

                TakeoverBootstrap.Log.LogInfo(
                    $"[TakeoverManager] Top monitor overlays added: {_topMonitorOverlayImages.Count} " +
                    $"(profit='{(profitBG != null ? profitBG.name : "<none>")}', " +
                    $"deadline='{(deadlineBG != null ? deadlineBG.name : "<none>")}', " +
                    $"graphics disabled: {_disabledMonitorGraphics.Count}).");
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] Top monitor override: {ex.Message}");
            }

            // ── B) Bottom-left: pin MapScreenVideo RT to our Y4NGZ content ───────
            // Previous attempts (stopping VP, swapping its clip, redirecting a
            // RawImage) all failed — the bottom-left monitor consistently showed
            // green. Green means the RT contents never became our video.
            //
            // New approach: stop the VP entirely and Graphics.Blit our RT into
            // MapScreenVideo on every LateUpdate via a coroutine. Whatever reads
            // MapScreenVideo (RawImage, mesh instance material, etc.) will then
            // display Y4NGZ without us needing to know which path drives the mesh.
            try
            {
                _levelDescWasEnabled = SetEnabled(sor.screenLevelDescription, false);

                if (sor.screenLevelVideoReel != null)
                {
                    var vr = sor.screenLevelVideoReel;
                    _savedVideoClip    = vr.clip;
                    _savedVideoLooping = vr.isLooping;
                    _savedVideoPlaying = vr.isPlaying;

                    // Decisive discriminator for "our video never prepares".
                    // This is the GAME's own VideoPlayer on the same machine,
                    // same graphics device, same media backend. If it reports
                    // isPrepared=False too, nothing on this profile can decode
                    // video and the fault is not our clip, our carrier or our
                    // settings — the usual cause is a Windows install without
                    // Media Foundation (an N/KN edition missing the Media
                    // Feature Pack). If it reports True while ours stays False,
                    // the fault really is specific to our media or setup.
                    TakeoverBootstrap.Log.LogInfo(
                        $"[Diag/Video] vanilla screenLevelVideoReel: isPrepared={vr.isPrepared} " +
                        $"isPlaying={vr.isPlaying} frame={vr.frame}/{vr.frameCount} " +
                        $"clip='{(vr.clip != null ? vr.clip.name : "null")}' " +
                        $"audioOut={vr.audioOutputMode} enabled={vr.enabled} " +
                        $"activeInHierarchy={vr.gameObject.activeInHierarchy}");

                    // Capture the VP's target RT before stopping so we can blit into it.
                    _mapScreenRT = vr.targetTexture;
                    vr.Stop();
                }

                if (_mapScreenRT != null && _renderTexture != null)
                {
                    _mapBlitCoroutine = StartCoroutine(BlitMapScreenLoop());
                    TakeoverBootstrap.Log.LogInfo(
                        $"[TakeoverManager] Pinning MapScreenVideo RT ({_mapScreenRT.width}x{_mapScreenRT.height}) to Y4NGZ via per-frame Blit.");
                }

                // Belt-and-suspenders: if a RawImage explicitly references
                // MapScreenVideo (by name), redirect it to our RT as well.
                var mapScreenUI = GameObject.Find("Systems/GameSystems/ItemSystems/MapScreenUI");
                if (mapScreenUI != null)
                {
                    foreach (var ri in mapScreenUI.GetComponentsInChildren<RawImage>(true))
                    {
                        if (ri.texture != null && ri.texture.name == "MapScreenVideo")
                        {
                            _videoReelRawImage      = ri;
                            _savedVideoReelTexture   = ri.texture;
                            ri.texture               = _renderTexture;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] Bottom-left override: {ex.Message}");
            }

            // ── MonitorWall hierarchy dump ─────────────────────────────────────
            // Print every descendant of MonitorWall so we can verify exact names
            // of each monitor mesh (top-left/right, bottom-left/right, 5th screen).
            try
            {
                var mwDump = GameObject.Find("Environment/HangarShip/ShipModels2b/MonitorWall");
                if (mwDump != null)
                {
                    TakeoverBootstrap.Log.LogInfo("[TakeoverManager] --- MonitorWall hierarchy dump ---");
                    DumpHierarchy(mwDump.transform, 0);
                    TakeoverBootstrap.Log.LogInfo("[TakeoverManager] --- end MonitorWall dump ---");
                }
                else
                    TakeoverBootstrap.Log.LogWarning("[TakeoverManager] MonitorWall not found for hierarchy dump.");
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] MonitorWall dump: {ex.Message}");
            }

            // ── C) Bottom-left: Cube.001 mesh (mapScreen) ────────────────────────
            // The mapScreen is driven by a ManualCameraRenderer that rewrites
            // mesh material.mainTexture every frame. We must disable that MCR
            // BEFORE overriding the texture, otherwise it reassigns the camera's
            // RT on the very next Update and wins.
            try
            {
                // Under UseBetterMonitors the vanilla Cube.001 GameObject is
                // still there but its MeshRenderer is disabled, so the path
                // lookup below would resolve a mesh nothing draws. GI repoints
                // mapScreen at its BigMiddle frame, whose MScreen child is the
                // surface actually carrying the feed — paint that instead.
                MeshRenderer mapSurface = giMonitors
                    ? GeneralImprovementsMonitors.ResolveMapScreenSurface(sor?.mapScreen)
                    : null;
                var cube001 = giMonitors
                    ? (mapSurface != null ? mapSurface.gameObject : null)
                    : GameObject.Find(
                        "Environment/HangarShip/ShipModels2b/MonitorWall/Cube.001");
                if (giMonitors && cube001 == null)
                {
                    GeneralImprovementsMonitors.LogStandDown(
                        TakeoverBootstrap.Log,
                        "the takeover's map-screen override",
                        "GI's replacement map surface could not be resolved from StartOfRound.mapScreen");
                }
                if (cube001 != null)
                {
                    var mr = cube001.GetComponent<MeshRenderer>();
                    if (mr != null && GeneralImprovementsMonitors.IsHiddenByBetterMonitors(mr))
                    {
                        GeneralImprovementsMonitors.LogStandDown(
                            TakeoverBootstrap.Log,
                            $"the takeover's map-screen override on '{mr.gameObject.name}'",
                            "GI disabled that renderer, so painting it would draw nothing");
                        mr = null;
                    }
                    if (mr != null && !AlreadyOverridden(mr))
                    {
                        // Disable the mapScreen MCR (the one that drives Cube.001).
                        // Track in _disabledComponents for restore-time re-enable.
                        if (sor != null && sor.mapScreen != null)
                        {
                            _disabledComponents.Add((sor.mapScreen, sor.mapScreen.enabled));
                            sor.mapScreen.enabled = false;
                            TakeoverBootstrap.Log.LogInfo(
                                $"[TakeoverManager] Cube.001: disabled sor.mapScreen MCR on '{sor.mapScreen.gameObject.name}'.");
                        }
                        else
                        {
                            TakeoverBootstrap.Log.LogInfo("[TakeoverManager] Cube.001: sor.mapScreen null — relying on global MCR disable.");
                        }

                        if (giMonitors)
                        {
                            TakeoverBootstrap.Log.LogInfo(
                                $"[TakeoverManager] GeneralImprovements map surface '{GetHierarchyPath(mr.transform)}' delegated to the wall property-block lease.");
                        }
                        else
                        {
                            Texture origTex = mr.sharedMaterial != null ? mr.sharedMaterial.mainTexture : null;
                            _overriddenRenderers.Add((mr, origTex));
                            ApplyMonitorOverride(mr);
                            TakeoverBootstrap.Log.LogInfo("[TakeoverManager] Cube.001 overridden via HDRP write.");
                        }
                    }
                }
                else if (!giMonitors)
                    TakeoverBootstrap.Log.LogWarning("[TakeoverManager] Cube.001 not found.");
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] Bottom-left override: {ex.Message}");
            }

            // ── C2) SingleScreen by path (confirmed by MonitorDebugger) ────────────
            // This is the remaining monitor mesh in MonitorWall, identified as
            // "SingleScreen" (not Cube.002/003 as originally expected).
            try
            {
                var singleScreen = GameObject.Find(
                    "Environment/HangarShip/ShipModels2b/MonitorWall/SingleScreen");
                if (singleScreen != null)
                {
                    var mr = singleScreen.GetComponent<MeshRenderer>();
                    // GI keeps this GameObject and disables the renderer when it
                    // owns the wall. Standing down here (rather than assuming it
                    // is always hidden) keeps the pass correct for the GI configs
                    // that leave SingleScreen drawing.
                    if (mr != null && GeneralImprovementsMonitors.IsHiddenByBetterMonitors(mr))
                    {
                        GeneralImprovementsMonitors.LogStandDown(
                            TakeoverBootstrap.Log,
                            "the takeover's SingleScreen override",
                            "GI disabled that renderer, so painting it would draw nothing");
                        mr = null;
                    }
                    if (mr != null && !AlreadyOverridden(mr))
                    {
                        Texture origTex = mr.sharedMaterial != null ? mr.sharedMaterial.mainTexture : null;
                        _overriddenRenderers.Add((mr, origTex));
                        ApplyMonitorOverride(mr);
                        TakeoverBootstrap.Log.LogInfo("[TakeoverManager] SingleScreen overridden.");
                    }
                }
                else
                    TakeoverBootstrap.Log.LogWarning("[TakeoverManager] SingleScreen not found.");
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] SingleScreen override: {ex.Message}");
            }

            // ── D) MonitorWall brute-force: catches any remaining screens ────────
            // Cube, Cube.001, and SingleScreen are already handled above.
            // The top-right and bottom-left mesh faces are siblings inside MonitorWall
            // (likely Cube.002, Cube.003, etc.) — override ALL MeshRenderers there.
            // Also catches OpenBodyCams BodyCamOverlayMesh and any mod-injected overlays.
            // Gated on CfgOverrideFifthMonitor so users can disable if it grabs too much.
            try
            {
                var monitorWall = GameObject.Find(
                    "Environment/HangarShip/ShipModels2b/MonitorWall");

                if (monitorWall != null && TakeoverBootstrap.CfgOverrideFifthMonitor.Value)
                {
                    foreach (var r in monitorWall.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (r == null || AlreadyOverridden(r)) continue;
                        // GI's replacement group lives under MonitorWall too, so
                        // this sweep sees both the disabled vanilla meshes and
                        // GI's screens. Skip the former; the GI pass below owns
                        // the latter (their materials are GI's, not TerminalTex,
                        // so IsMonitorScreen would reject them anyway).
                        if (GeneralImprovementsMonitors.IsHiddenByBetterMonitors(r)) continue;
                        // #668: IsHiddenByBetterMonitors only rejects the vanilla
                        // meshes GI disabled. GI's own MonitorGroup(Clone) lives
                        // under MonitorWall too and its frames (BigLeft, BigMiddle,
                        // TopGroupL, …) carry the literal TerminalTex material, so
                        // IsMonitorScreen accepts them and this sweep was writing
                        // over GI-owned materials — the exact thing #599's
                        // property-block lease exists to avoid, and on the wrong
                        // meshes besides (the frames are bezels; the screens are
                        // their LScreen/MScreen/Screen* children). D2's lease owns
                        // every GI surface; leave the whole group to it.
                        if (giMonitors && IsUnderGeneralImprovementsMonitorGroup(r.transform)) continue;
                        if (!IsMonitorScreen(r)) continue;   // skip buttons, BodyCam overlay, etc.
                        if (!MatchesAdditionalMonitorFilter(r)) continue;
                        try
                        {
                            Texture origTex = r.sharedMaterial != null ? r.sharedMaterial.mainTexture : null;
                            _overriddenRenderers.Add((r, origTex));
                            ApplyMonitorOverride(r);
                            TakeoverBootstrap.Log.LogInfo(
                                $"[TakeoverManager] Fifth monitor override: {GetHierarchyPath(r.transform)}");
                        }
                        catch { }
                    }
                }
                else if (monitorWall != null)
                {
                    TakeoverBootstrap.Log.LogInfo("[TakeoverManager] Fifth-monitor pass skipped (CfgOverrideFifthMonitor=false).");
                }

                // ── D2) GeneralImprovements screens ──────────────────────────
                // A renderer-local property block leaves GI's material identity
                // untouched. The lease re-resolves the map plus every API screen,
                // surviving power toggles and MonitorGroup rebuilds without
                // teaching GI to persist our temporary takeover.
                if (giMonitors)
                {
                    _generalImprovementsMonitorLease = new GeneralImprovementsMonitorLease(
                        GeneralImprovementsMonitorLease.SurfaceSet.WholeWall);
                    // #662: the per-surface overload landed in Y4NGZCore 1.0.6. A profile
                    // running an older Core resolves nothing at the call site, so the call is
                    // isolated in a non-inlined helper - a MissingMethodException surfaces when
                    // THAT method is jitted, which is at the call below, rather than when this
                    // whole method is jitted where no catch could reach it.
                    IReadOnlyList<Texture> giTextures = BuildGiSurfaceTextures();
                    bool initiallyBound;
                    string leaseDetail;
                    if (giTextures != null)
                    {
                        try
                        {
                            initiallyBound = AcquireGiLeasePerSurface(giTextures, out leaseDetail);
                        }
                        catch (MissingMethodException)
                        {
                            TakeoverBootstrap.Log.LogWarning(
                                "[TakeoverManager] This profile's Y4NGZCore predates the per-surface monitor "
                                + "lease (1.0.6); the GeneralImprovements wall shows the primary source on "
                                + "every screen for this takeover.");
                            initiallyBound = _generalImprovementsMonitorLease.Acquire(
                                _renderTexture, out leaseDetail);
                        }
                    }
                    else
                    {
                        initiallyBound = _generalImprovementsMonitorLease.Acquire(
                            _renderTexture, out leaseDetail);
                    }
                    _generalImprovementsLeaseCoroutine =
                        StartCoroutine(MaintainGeneralImprovementsMonitorLease());

                    if (initiallyBound)
                        TakeoverBootstrap.Log.LogInfo($"[TakeoverManager] {leaseDetail}.");
                    else
                        TakeoverBootstrap.Log.LogWarning(
                            $"[TakeoverManager] {leaseDetail}; maintenance will bind the rebuilt wall when it appears.");
                }

                // Ship-wide RT scan REMOVED — it was hitting BodyCamOverlayMesh(Clone)
                // (an OpenBodyCams overlay) whose custom shader renders black when
                // fed our RenderTexture, and which overlays the bottom-left monitor
                // region, causing all monitors to appear black during takeover.
                // If we ever need to catch a different RT-backed overlay again, add
                // an explicit allow-list here rather than a generic "any RT" scan.
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] MonitorWall/RT scan: {ex.Message}");
            }

            // ── E) Disable ManualCameraRenderer components entirely ────────────
            // Disabling just the cameras wasn't enough — the component's
            // Update/LateUpdate keeps resetting mesh material.mainTexture to the
            // camera's targetTexture every frame, overwriting our overrides.
            // Disabling the entire MonoBehaviour stops all of that.
            // NOTE: do NOT clear _disabledComponents here — section C may already
            // have recorded sor.mapScreen with its original enabled state.
            try
            {
                int added = 0;
                foreach (var mcr in FindObjectsOfType<ManualCameraRenderer>())
                {
                    if (mcr == null) continue;
                    if (AlreadyDisabled(mcr)) { mcr.enabled = false; continue; }
                    _disabledComponents.Add((mcr, mcr.enabled));
                    mcr.enabled = false;
                    added++;
                }
                TakeoverBootstrap.Log.LogInfo(
                    $"[TakeoverManager] Disabled {_disabledComponents.Count} ManualCameraRenderer components (+{added} this pass).");
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[TakeoverManager] MCR disable: {ex.Message}");
            }

            // Post-override summary — flat list of every renderer we're driving.
            try
            {
                var names = new List<string>();
                foreach (var (r, _) in _overriddenRenderers)
                    if (r != null) names.Add(r.gameObject.name);
                TakeoverBootstrap.Log.LogInfo(
                    $"[TakeoverManager] Override summary: {string.Join(", ", names)}");
            }
            catch { }

            AssignPerMonitorMedia();

            TakeoverBootstrap.Log.LogInfo(
                $"[TakeoverManager] Override complete. MeshRenderers: {_overriddenRenderers.Count}, " +
                $"Canvas overlays: {_topMonitorOverlayImages.Count}, " +
                $"VideoReel RawImage: {(_videoReelRawImage != null ? "found" : "missing")}");

            StartCoroutine(VerifyOverridesAfterDelay());
        }

        private IEnumerator MaintainGeneralImprovementsMonitorLease()
        {
            while (_takeoverActive && _generalImprovementsMonitorLease?.IsActive == true)
            {
                _generalImprovementsMonitorLease.Maintain();
                yield return GiLeaseRefresh;
            }
            _generalImprovementsLeaseCoroutine = null;
        }

        /// <summary>
        /// Isolated so its <see cref="MissingMethodException"/> is catchable against an older
        /// Y4NGZCore. Must not be inlined into <see cref="OverrideMonitors"/>: the missing method
        /// is resolved when the containing method is jitted, and an inlined call would throw
        /// before OverrideMonitors' own try block existed.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool AcquireGiLeasePerSurface(IReadOnlyList<Texture> textures, out string detail) =>
            _generalImprovementsMonitorLease.Acquire(_renderTexture, textures, out detail);

        /// <summary>
        /// Per-binding textures for the GeneralImprovements wall (#662), or null when there is
        /// nothing to split. Binding 0 is the lease's map screen and is pinned to the primary:
        /// the map surface is also what the bottom-left blit loop and the bridge scope drive, so
        /// giving it a different source would make one physical screen disagree with itself.
        /// </summary>
        private IReadOnlyList<Texture> BuildGiSurfaceTextures()
        {
            int playerCount = ActiveMediaPlayerCount;
            if (!_mediaPlan.Active || playerCount < 2) return null;

            int[] permutation = TakeoverMediaAssignment.BuildPermutation(_mediaPlan.Seed, playerCount);
            var textures = new List<Texture>(GiSurfaceTextureSlots) { _renderTexture };
            for (int index = 1; index < GiSurfaceTextureSlots; index++)
            {
                Texture texture = MediaTextureFor(
                    TakeoverMediaAssignment.PlayerForSurface(index, playerCount, permutation));
                textures.Add(texture != null ? texture : _renderTexture);
            }
            return textures;
        }

        /// <summary>
        /// Deals the claimed vanilla surfaces to the open media players (#662).
        ///
        /// <para>Runs after every discovery pass rather than inside them, because the assignment
        /// is defined over the SORTED surface list: two peers can enumerate the same wall in
        /// different orders, and assigning during traversal would hand them different videos.
        /// Sites assigned to player 0 are left exactly as the passes painted them, so the
        /// primary's surfaces are never written twice.</para>
        /// </summary>
        private void AssignPerMonitorMedia()
        {
            if (!_mediaPlan.Active || _mediaPaintSites.Count == 0) return;

            var keys = new List<TakeoverMediaSurfaceKey>(_mediaPaintSites.Count);
            foreach (TakeoverMediaPaintSite site in _mediaPaintSites) keys.Add(site.Key);
            TakeoverMediaAssignment.SortDeterministically(keys);

            int playerCount = TakeoverMediaAssignment.ClampPlayerCount(
                ActiveMediaPlayerCount, _mediaPlan.Sources.Count, keys.Count);
            if (playerCount < 2) return;
            int[] permutation = TakeoverMediaAssignment.BuildPermutation(_mediaPlan.Seed, playerCount);

            int reassigned = 0;
            foreach (TakeoverMediaPaintSite site in _mediaPaintSites)
            {
                int player = TakeoverMediaAssignment.PlayerForSurface(
                    keys.IndexOf(site.Key), playerCount, permutation);
                if (player == 0) continue;
                Texture texture = MediaTextureFor(player);
                if (texture == null || texture == _renderTexture) continue;
                if (site.Overlay != null) site.Overlay.texture = texture;
                else PaintScreenMaterial(site.Material, texture);
                reassigned++;
            }

            TakeoverBootstrap.Log.LogInfo(
                $"[TakeoverManager] Per-monitor media: {_mediaPaintSites.Count} surfaces across "
                + $"{playerCount} player(s) from a pool of {_mediaPlan.Sources.Count} source(s), "
                + $"seed {_mediaPlan.Seed} ({reassigned} moved off the primary).");
        }

        private IEnumerator DiagVideoStateLoop()
        {
            if (_videoPlayer == null) yield break;
            while (_takeoverActive && _videoPlayer != null)
            {
                TakeoverBootstrap.Log.LogInfo(
                    $"[Diag/Video] isPrepared={_videoPlayer.isPrepared} isPlaying={_videoPlayer.isPlaying} " +
                    $"isPaused={_videoPlayer.isPaused} frame={_videoPlayer.frame}/{_videoPlayer.frameCount} " +
                    $"time={_videoPlayer.time:F2} clip='{(_videoPlayer.clip != null ? _videoPlayer.clip.name : "null")}' " +
                    $"url='{_videoPlayer.url}' targetTex-id={(_videoPlayer.targetTexture != null ? _videoPlayer.targetTexture.GetInstanceID().ToString() : "NULL")} " +
                    $"rt-id={(_renderTexture != null ? _renderTexture.GetInstanceID().ToString() : "NULL")}");
                yield return new WaitForSeconds(1f);
            }
        }

        private IEnumerator DiagRTReadbackAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (_renderTexture == null)
            {
                TakeoverBootstrap.Log.LogInfo("[Diag/RT] readback: _renderTexture is null");
                yield break;
            }
            yield return new WaitForEndOfFrame();
            try
            {
                var prev = RenderTexture.active;
                RenderTexture.active = _renderTexture;
                var sample = new Texture2D(4, 4, TextureFormat.RGB24, false);
                sample.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                sample.Apply();
                RenderTexture.active = prev;

                Color avg = Color.black;
                var pixels = sample.GetPixels();
                foreach (var p in pixels) avg += p;
                avg /= pixels.Length;
                Destroy(sample);

                string diagnosis =
                    (avg.r > 0.9f && avg.g > 0.9f && avg.b > 0.9f) ? "WHITE (RT empty — video not decoding)" :
                    (avg.r < 0.05f && avg.g < 0.05f && avg.b < 0.05f) ? "BLACK (RT cleared/uninitialized)" :
                    "HAS COLOR (video decoding into RT)";
                TakeoverBootstrap.Log.LogInfo(
                    $"[Diag/RT] readback at t={delay}s: avg=({avg.r:F3},{avg.g:F3},{avg.b:F3}) — {diagnosis}");
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning($"[Diag/RT] readback failed: {ex.Message}");
            }
        }

        private IEnumerator VerifyOverridesAfterDelay()
        {
            yield return new WaitForSeconds(2.0f);
            if (_generalImprovementsMonitorLease?.IsActive == true)
            {
                _generalImprovementsMonitorLease.Maintain(force: true);
                TakeoverBootstrap.Log.LogInfo(
                    $"[Diag/Post] GeneralImprovements property-block lease owns {_generalImprovementsMonitorLease.BoundCount} surface(s).");
            }
            foreach (var (r, _) in _overriddenRenderers)
            {
                if (r == null) continue;
                Material mat;
                try { mat = r.material; } catch { continue; }
                if (mat == null) continue;
                Texture bc = mat.HasProperty("_BaseColorMap") ? mat.GetTexture("_BaseColorMap") : null;
                Texture em = mat.HasProperty("_EmissiveColorMap") ? mat.GetTexture("_EmissiveColorMap") : null;
                Texture mt = mat.mainTexture;
                bool bcIsOurs = bc == _renderTexture;
                bool emIsOurs = em == _renderTexture;
                TakeoverBootstrap.Log.LogInfo(
                    $"[Diag/Post] '{r.gameObject.name}' shader='{mat.shader.name}' " +
                    $"_BaseColorMap={(bc != null ? bc.name : "null")} (ours={bcIsOurs}) " +
                    $"_EmissiveColorMap={(em != null ? em.name : "null")} (ours={emIsOurs}) " +
                    $"mainTex={(mt != null ? mt.name : "null")}");
            }
        }

        // Switch a quota/deadline graphic off for the takeover, remembering its
        // previous state. Ignores duplicates so passing both the vanilla
        // reference and a clone that resolved to the same object is harmless.
        private void DisableMonitorGraphic(Behaviour graphic)
        {
            if (graphic == null) return;
            for (int i = 0; i < _disabledMonitorGraphics.Count; i++)
                if (_disabledMonitorGraphics[i].graphic == graphic) return;

            _disabledMonitorGraphics.Add((graphic, graphic.enabled));
            graphic.enabled = false;
        }

        // Mirrors ShipMonitorStyler.ResolveDisplaySources: prefer the visible
        // monitor-wall clone (GeneralImprovements names them 'ProfitQuotaBG5',
        // 'DeadlineText6', …) over the vanilla StartOfRound reference, falling
        // back to the vanilla one when no clone exists. Kept local rather than
        // calling into ShipSystems.Layout so the takeover stays reflection-only
        // coupled to that assembly, like ShipSystemsTakeoverBridge.
        private static T ResolveVisibleMonitorGraphic<T>(StartOfRound sor, T fallback, string namePrefix)
            where T : Behaviour
        {
            try
            {
                Transform container = sor != null && sor.profitQuotaMonitorBGImage != null
                    ? sor.profitQuotaMonitorBGImage.transform.parent
                    : null;
                if (container == null) return fallback;

                T best = null;
                int bestScore = int.MinValue;
                foreach (var candidate in container.GetComponentsInChildren<T>(true))
                {
                    if (candidate == null || candidate == fallback ||
                        !candidate.name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int score = 0;
                    if (candidate.gameObject.activeInHierarchy) score += 4;
                    if (candidate.gameObject.activeSelf)        score += 2;
                    if (candidate.enabled)                      score += 1;
                    if (score > bestScore) { best = candidate; bestScore = score; }
                }

                return best != null ? best : fallback;
            }
            catch (Exception ex)
            {
                TakeoverBootstrap.Log.LogWarning(
                    $"[TakeoverManager] Monitor clone resolve for '{namePrefix}' failed: {ex.Message}");
                return fallback;
            }
        }

        // Creates a RawImage child positioned and sized to exactly match a source
        // Image component's RectTransform — used to overlay video on each top monitor
        // face without bleeding onto the bezel.
        private void AddOverlayMatchingBG(Image bgImage)
        {
            if (bgImage == null || _renderTexture == null) return;
            var srcRect = bgImage.rectTransform;

            var overlayGO = new GameObject("Y4NGZ_MonitorOverlay");
            overlayGO.transform.SetParent(srcRect.parent, false);

            var rt = overlayGO.AddComponent<RectTransform>();
            rt.anchorMin        = srcRect.anchorMin;
            rt.anchorMax        = srcRect.anchorMax;
            rt.pivot            = srcRect.pivot;
            rt.anchoredPosition = srcRect.anchoredPosition;
            rt.sizeDelta        = srcRect.sizeDelta;
            rt.localScale       = Vector3.one;

            var ri = overlayGO.AddComponent<RawImage>();
            ri.texture = _renderTexture;
            ri.color   = Color.white;
            _topMonitorOverlayImages.Add(ri);
            // #662: painted with the primary here, exactly as it always was, and repainted by
            // AssignPerMonitorMedia only when a plan is active. The key is the SOURCE graphic's
            // hierarchy path, not the overlay's - the overlay was created a line ago and its
            // name is identical on every face, whereas the BG it matches is unique and is the
            // same path on every peer.
            if (_mediaPlan.Active)
                _mediaPaintSites.Add(new TakeoverMediaPaintSite(
                    new TakeoverMediaSurfaceKey("overlay:" + GetHierarchyPath(bgImage.transform), -1),
                    null,
                    ri));
        }

        // Bind the takeover RT to every property a monitor material might sample
        // from. The ship's monitors use HDRP/Lit, which reads _BaseColorMap (not
        // _MainTex). We also write emissive so the screens self-illuminate in
        // dim lighting — unlit HDRP albedo reads near-black in the dimmed ship.
        //
        // Saves originals the first time per renderer into _savedHDRPState so
        // RestoreAll can revert cleanly.
        // <paramref name="dedicatedScreenMesh"/> says the renderer is a screen
        // quad with no bezel triangles sharing its material — true for
        // GeneralImprovements' replacement monitors, which carry exactly one
        // material. The HDRP/Lit skip below exists only to protect the vanilla
        // Cube meshes' shared bezel submesh; applying it to a GI screen would
        // leave that screen unpainted, which is the #599 failure mode.
        private void ApplyMonitorOverride(MeshRenderer mr, bool dedicatedScreenMesh = false)
        {
            if (mr == null || _renderTexture == null) return;

            // Override every material slot — this is what gives us 10/10 monitor
            // coverage on Cube.001 (multi-screen mesh). Slot-filtering by name
            // breaks coverage because the other screen submeshes don't share
            // the literal "TerminalTex" name on their material.
            Material[] mats;
            try { mats = mr.materials; } catch { return; }
            if (mats == null || mats.Length == 0) return;

            // ── Diagnostic: dump per-slot material + main texture identity. ──
            // Non-destructive. Used to figure out which slots are screens vs.
            // chassis/frame, so we can write a precise filter for the white-
            // mesh issue without losing monitor coverage.
            try
            {
                var shared = mr.sharedMaterials;
                TakeoverBootstrap.Log.LogInfo($"[Diag/Slots] '{mr.name}' slotCount={mats.Length}");
                for (int i = 0; i < mats.Length; i++)
                {
                    string smName = (shared != null && i < shared.Length && shared[i] != null) ? shared[i].name : "<null>";
                    var m = mats[i];
                    string mName = m != null ? m.name : "<null>";
                    string shader = (m != null && m.shader != null) ? m.shader.name : "<null>";
                    Texture origMain = null;
                    try { if (shared != null && i < shared.Length && shared[i] != null) origMain = shared[i].mainTexture; } catch { }
                    Texture origBC = null;
                    try { if (shared != null && i < shared.Length && shared[i] != null && shared[i].HasProperty("_BaseColorMap")) origBC = shared[i].GetTexture("_BaseColorMap"); } catch { }
                    string mainName = origMain != null ? $"{origMain.GetType().Name}:{origMain.name}" : "<none>";
                    string bcName   = origBC   != null ? $"{origBC.GetType().Name}:{origBC.name}"     : "<none>";
                    TakeoverBootstrap.Log.LogInfo($"[Diag/Slots]   slot[{i}] sharedMat='{smName}' instMat='{mName}' shader='{shader}' mainTex={mainName} _BaseColorMap={bcName}");
                }
            }
            catch (Exception ex) { TakeoverBootstrap.Log.LogWarning($"[Diag/Slots] dump failed for '{mr.name}': {ex.Message}"); }

            if (!_savedHDRPState.ContainsKey(mr))
            {
                var saved = new (bool, Texture, Texture, Texture, Texture, Color, bool, bool)[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    // Slots 1/2 (HDRP/Unlit camera screens) have their own
                    // mainTexture (mapTexture / shipScreen1 / shipScreen2) —
                    // we must save it per-slot so RestoreAll can rebind the
                    // correct camera RT instead of reverting them all to the
                    // slot-0 sharedMaterial texture.
                    Texture origMain = null;
                    try { origMain = m.mainTexture; } catch { }
                    Texture origBC  = m.HasProperty("_BaseColorMap")     ? m.GetTexture("_BaseColorMap")     : null;
                    Texture origUC  = m.HasProperty("_UnlitColorMap")    ? m.GetTexture("_UnlitColorMap")    : null;
                    Texture origEm  = m.HasProperty("_EmissiveColorMap") ? m.GetTexture("_EmissiveColorMap") : null;
                    Color   origEmC = m.HasProperty("_EmissiveColor")    ? m.GetColor("_EmissiveColor")      : Color.black;
                    bool hadEmission    = m.IsKeywordEnabled("_EMISSION");
                    bool hadEmissiveMap = m.IsKeywordEnabled("_EMISSIVE_COLOR_MAP");
                    saved[i] = (true, origMain, origBC, origUC, origEm, origEmC, hadEmission, hadEmissiveMap);
                }
                _savedHDRPState[mr] = saved;
            }

            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;

                // The white frame surrounding the monitors comes from bezel
                // triangles in slot 0 (HDRP/Lit "TerminalTex") sampling bright
                // pixels of the Y4NGZ video. The bezel and the small in-frame
                // monitors share that submesh, so we can't skip slot 0
                // entirely without losing coverage.
                //
                // Trick: on HDRP/Lit slots, leave _BaseColorMap (the surface's
                // visible albedo) UNTOUCHED — that's what carries the chassis/
                // bezel art. We only paint the video into _EmissiveColorMap
                // with a moderate emissive multiplier. Result: bezel stays
                // looking like vanilla ship metal, and the small TerminalTex
                // screens still glow with the Y4NGZ feed via emissive.
                //
                // HDRP/Unlit slots (slots 1/2 on Cube.001 / Cube / SingleScreen)
                // are the actual large monitor TV screens — those still get the
                // full mainTexture + _BaseColorMap + bright emissive override
                // so they remain crisp and bright.
                string shaderName = (m.shader != null) ? m.shader.name : string.Empty;
                bool isHDRPLit = !dedicatedScreenMesh &&
                    shaderName.IndexOf("HDRP/Lit", StringComparison.Ordinal) >= 0;
                bool isShipSystemsPowerMaterial = IsShipSystemsPowerMaterial(m);

                if (isHDRPLit && !isShipSystemsPowerMaterial)
                {
                    // Skip HDRP/Lit slot 0 entirely — that submesh contains
                    // both the bezel/chassis triangles AND the small Terminal-
                    // Tex monitor screens, sharing one material. Any emissive
                    // we write here makes the bezel UVs bloom white because
                    // they sample bright pixels of the Y4NGZ video. The unlit
                    // slots (slots 1/2 of Cube/Cube.001/SingleScreen) carry
                    // the actual large-monitor TV screens, and the top-
                    // monitor RawImage overlays cover the remaining screens.
                    // Net result: clean vanilla bezel, no white frame.
                    //
                    // saved[i].overridden was set true above; flip it false
                    // so RestoreAll doesn't try to write back state we never
                    // touched (it'd be a no-op but keeps the audit clean).
                    if (_savedHDRPState.TryGetValue(mr, out var savedSlots) &&
                        savedSlots != null && i < savedSlots.Length)
                    {
                        var s = savedSlots[i];
                        s.overridden = false;
                        savedSlots[i] = s;
                    }
                }
                else
                {
                    // Unlit (large monitor) — full takeover.
                    PaintScreenMaterial(m, _renderTexture);
                    // #662: this slot is assignable. Recorded, not yet reassigned - the plan
                    // needs every claimed surface before it can sort them, and sorting is the
                    // only thing that makes host and client agree.
                    if (_mediaPlan.Active)
                        _mediaPaintSites.Add(new TakeoverMediaPaintSite(
                            new TakeoverMediaSurfaceKey(GetHierarchyPath(mr.transform), i), m, null));
                }
            }
        }

        /// <summary>
        /// The screen-material write, in one place so a per-monitor reassignment paints exactly
        /// what the first pass painted, only with a different texture.
        /// </summary>
        private static void PaintScreenMaterial(Material m, Texture texture)
        {
            if (m == null || texture == null) return;
            try { m.mainTexture = texture; } catch { }
            if (m.HasProperty("_BaseColorMap"))     m.SetTexture("_BaseColorMap", texture);
            if (m.HasProperty("_UnlitColorMap"))    m.SetTexture("_UnlitColorMap", texture);
            if (m.HasProperty("_EmissiveColorMap")) m.SetTexture("_EmissiveColorMap", texture);
            if (m.HasProperty("_EmissiveColor"))    m.SetColor("_EmissiveColor", Color.white * 1.5f);
            m.EnableKeyword("_EMISSION");
            m.EnableKeyword("_EMISSIVE_COLOR_MAP");
        }

        private bool AlreadyOverridden(MeshRenderer r)
        {
            foreach (var (existing, _) in _overriddenRenderers)
                if (existing == r) return true;
            return false;
        }

        private bool AlreadyDisabled(Behaviour b)
        {
            foreach (var (existing, _) in _disabledComponents)
                if (existing == b) return true;
            return false;
        }

        // True only for renderers that actually look like monitor screens —
        // i.e. their shared material is "TerminalTex" (the shared LC monitor
        // material). This excludes the red/grey buttons, their wood trim cubes,
        // and the OpenBodyCams BodyCamOverlayMesh(Clone).
        private static bool IsShipSystemsPowerMaterial(Material material)
        {
            if (material == null) return false;

            string materialName = material.name ?? string.Empty;
            if (materialName.IndexOf("LGUShipPower", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            Texture main = null;
            try { main = material.mainTexture; } catch { }
            if (IsShipSystemsPowerTexture(main)) return true;

            if (material.HasProperty("_BaseColorMap") && IsShipSystemsPowerTexture(material.GetTexture("_BaseColorMap")))
                return true;
            if (material.HasProperty("_UnlitColorMap") && IsShipSystemsPowerTexture(material.GetTexture("_UnlitColorMap")))
                return true;
            if (material.HasProperty("_EmissiveColorMap") && IsShipSystemsPowerTexture(material.GetTexture("_EmissiveColorMap")))
                return true;

            return false;
        }

        private static bool IsShipSystemsPowerTexture(Texture texture)
        {
            return texture != null
                && string.Equals(texture.name, "LGUShipSystems_PowerMonitorRT", StringComparison.Ordinal);
        }

        // #610: the list itself lives with the materials it names, in
        // ShipSystemsMonitorClaims. It was here only because the takeover was its
        // first consumer, and keeping it here made the answer depend on the
        // takeover module being enabled — which the ceremony's wall scan is not
        // entitled to assume. This delegate stays so the takeover's own screen
        // detection below reads the same way it always has.
        //
        // #613 task 2.1: ShipSystemsMonitorClaims moved to Y4NGZShipSystems.dll, so this can no
        // longer name it. It goes through the Core probe registry the claim list was published
        // to for exactly this reason — ShipSystemsBootstrap calls
        // ShipSystemsMonitorClaims.EnsureProbeRegistered() as soon as Ship Systems is known to be
        // enabled, and the probe is the same predicate this used to call.
        //
        // Behaviour change, deliberate and in the safe direction: with Ship Systems NOT installed
        // no probe is registered and this reads false, where before the split the predicate was
        // always compiled in and answered from the material's name alone. False is correct — with
        // no Ship Systems there are no LGUShipPower / LGUShipSystems_MonitorRowFace materials on
        // the wall to recognise, so nothing is claim-owned and the takeover's brute-force screen
        // pass is free to use every screen it finds, which is what it did before Ship Systems
        // existed.
        internal static bool IsShipSystemsClaimOwnedMaterial(Material material) =>
            Y4NGZCore.Modules.Monitor.MonitorClaimProbes.IsClaimed(material);

        private static bool IsMonitorScreen(MeshRenderer r)
        {
            if (r == null) return false;
            var mat = r.sharedMaterial;
            if (mat == null) return false;

            // These face-only renderers are painted by the ShipSystems claim
            // handoff. Returning false here is an explicit recognition path,
            // preventing the brute-force pass from saving or overriding them a
            // second time.
            if (IsShipSystemsClaimOwnedMaterial(mat)) return false;

            var name = r.gameObject.name;
            if (name.Contains("BodyCam") || name.Contains("Button") ||
                name.Contains("Overlay")) return false;
            if (IsShipSystemsPowerMaterial(mat)) return true;
            // Material instances get an "(Instance)" suffix; StartsWith handles both.
            return mat.name.StartsWith("TerminalTex", StringComparison.Ordinal);
        }

        private static bool MatchesAdditionalMonitorFilter(MeshRenderer renderer)
        {
            string configured = TakeoverBootstrap.CfgAdditionalMonitorNameTokens?.Value;
            if (string.IsNullOrWhiteSpace(configured)) return true;

            string path = GetHierarchyPath(renderer.transform);
            string[] tokens = configured.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (token.Length > 0 && path.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static void DumpHierarchy(Transform t, int depth)
        {
            if (t == null || depth > 6) return;
            string indent = new string(' ', depth * 2);
            var mr = t.GetComponent<MeshRenderer>();
            string texName = "<none>";
            string texType = "";
            if (mr != null && mr.sharedMaterial != null && mr.sharedMaterial.mainTexture != null)
            {
                texName = mr.sharedMaterial.mainTexture.name;
                texType = " " + mr.sharedMaterial.mainTexture.GetType().Name;
            }
            TakeoverBootstrap.Log.LogInfo(
                $"[TakeoverManager]   {indent}{t.name}  (MR={(mr != null ? "Y" : "N")} tex={texName}{texType})");
            for (int i = 0; i < t.childCount; i++)
                DumpHierarchy(t.GetChild(i), depth + 1);
        }

        private static string GetHierarchyPath(Transform t)
        {
            if (t == null) return "<null>";
            var stack = new List<string>();
            while (t != null) { stack.Add(t.name); t = t.parent; }
            stack.Reverse();
            return string.Join("/", stack);
        }

        // True when this transform sits anywhere inside GeneralImprovements'
        // replacement wall. GI instantiates "MonitorGroup(Clone)" under the
        // vanilla MonitorWall, which is why the same ancestor-name test
        // Y4NGZCore's GI bridge uses is the right one here: it catches the
        // frames as well as the screens, and neither belongs to any pass but
        // the property-block lease.
        private static bool IsUnderGeneralImprovementsMonitorGroup(Transform transform)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (current.name != null &&
                    current.name.StartsWith("MonitorGroup", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Step 3b — MapScreenVideo RT pinning
        // ─────────────────────────────────────────────────────────────────────

        // Every end-of-frame, blit our Y4NGZ RT into the game's MapScreenVideo RT
        // so the bottom-left monitor mesh (and any RawImage referencing the RT)
        // shows Y4NGZ content. Runs until the takeover ends; VP is stopped, so
        // nothing else is writing to MapScreenVideo during the blit window.
        private IEnumerator BlitMapScreenLoop()
        {
            var eof = new WaitForEndOfFrame();
            bool logged = false;
            while (_takeoverActive && _mapScreenRT != null && _renderTexture != null)
            {
                yield return eof;
                if (!logged)
                {
                    TakeoverBootstrap.Log.LogInfo(
                        $"[Diag/Blit] first blit: src=_renderTexture(id={_renderTexture.GetInstanceID()} {_renderTexture.width}x{_renderTexture.height}) " +
                        $"dst=_mapScreenRT(id={_mapScreenRT.GetInstanceID()} {_mapScreenRT.width}x{_mapScreenRT.height})");
                    logged = true;
                }
                try { Graphics.Blit(_renderTexture, _mapScreenRT); }
                catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Step 4 — Mumble Audio
        // ─────────────────────────────────────────────────────────────────────

        // Starts the voice loop at most once per takeover. RestoreAll clears the handle.
        private void StartMumble()
        {
            if (_mumbleCoroutine != null || _mumbleSource != null) return;
            _mumbleCoroutine = StartCoroutine(PlayMumbleSequence());
        }

        private IEnumerator PlayMumbleSequence()
        {
            // Pool is chosen once per takeover: an override that finishes
            // decoding mid-sequence waits for the next takeover rather than
            // swapping voices halfway through this one.
            AudioClip[] mumbleClips = TakeoverBootstrap.MumbleClips;
            string mumbleSource = "bundle";
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #861: the voices this takeover's audio decision captured. A partial pool plays
            // only its ready files; the bundled voices play only when none was ready.
            if (_useQuotaProgressionPayload && _audio.Voices.Length > 0)
            {
                mumbleClips = _audio.Voices;
                mumbleSource = "config";
            }
#endif

            TakeoverBootstrap.Log.LogInfo(
                $"[TakeoverManager] PlayMumbleSequence: entered. MumbleClips.Length={mumbleClips.Length} (source={mumbleSource})");
            if (mumbleClips.Length == 0)
            {
                TakeoverBootstrap.Log.LogWarning("[TakeoverManager] PlayMumbleSequence: no mumble clips loaded — yielding break.");
                yield break;
            }

            var sor = StartOfRound.Instance;
            if (sor == null)
                TakeoverBootstrap.Log.LogWarning("[TakeoverManager] PlayMumbleSequence: StartOfRound.Instance is null — position will default.");

            // Dedicated AudioSource — separate from speakerAudioSource to avoid
            // competing with the game's orbital PA announcements.
            var mumbleGO = new GameObject("Y4NGZ_MumbleAudio");
            DontDestroyOnLoad(mumbleGO);
            _mumbleSource = mumbleGO.AddComponent<AudioSource>();
            if (_mumbleSource == null)
            {
                TakeoverBootstrap.Log.LogWarning("[TakeoverManager] PlayMumbleSequence: AddComponent<AudioSource> returned null — abort.");
                yield break;
            }

            // Position at the ship speaker mount so it radiates from the ceiling.
            if (sor?.speakerAudioSource != null)
                mumbleGO.transform.position = sor.speakerAudioSource.transform.position;
            else if (sor != null)
                mumbleGO.transform.position = sor.transform.position;

            _mumbleSource.spatialBlend = 0f;    // 2D — heard equally throughout ship
            // Boost volume to compensate for the filtering that follows.
            _mumbleSource.volume       = Mathf.Min(TakeoverBootstrap.CfgMumbleVolume.Value * 1.6f, 1f);
            _mumbleSource.loop         = false;
            _mumbleSource.playOnAwake  = false;

            // ── Degraded-speaker filter chain ─────────────────────────────────
            // Together these recreate the sound of audio pushed through a cheap,
            // damaged ship intercom: narrow frequency band + light overdrive.

            // 1. High-pass at 300 Hz — old/small speakers can't reproduce deep bass.
            //    Removes the low-end weight that makes audio sound "clean".
            var hpf = mumbleGO.AddComponent<AudioHighPassFilter>();
            hpf.cutoffFrequency     = 300f;
            hpf.highpassResonanceQ  = 1.0f;

            // 2. Low-pass at 2200 Hz — cuts the top end down to telephone/intercom range.
            //    The default was 3500 Hz (AM radio quality); 2200 Hz is more degraded.
            var lpf = mumbleGO.AddComponent<AudioLowPassFilter>();
            lpf.cutoffFrequency    = 2200f;
            lpf.lowpassResonanceQ  = 1.5f;  // slight resonance peak adds "buzz"

            // 3. Distortion at 0.3 — light overdrive gives speaker crackle.
            //    Keep this low (< 0.4) or the voice becomes unintelligible.
            var dist = mumbleGO.AddComponent<AudioDistortionFilter>();
            dist.distortionLevel   = 0.3f;

            TakeoverBootstrap.Log.LogInfo(
                $"[TakeoverManager] PlayMumbleSequence: source ready. spatial={_mumbleSource.spatialBlend:F2} " +
                $"vol={_mumbleSource.volume:F2} clips={mumbleClips.Length} (source={mumbleSource}). Entering loop.");

            // Keep playing random clips until the takeover sequence ends.
            // Short random gaps between clips simulate natural speech cadence.
            while (_takeoverActive && _mumbleSource != null && mumbleClips.Length > 0)
            {
                var clip = mumbleClips[UnityEngine.Random.Range(0, mumbleClips.Length)];
                if (clip == null)
                {
                    TakeoverBootstrap.Log.LogWarning("[TakeoverManager] PlayMumbleSequence: picked null clip — skipping frame.");
                    yield return null;
                    continue;
                }

                _mumbleSource.clip = clip;
                _mumbleSource.Play();
                TakeoverBootstrap.Log.LogInfo(
                    $"[TakeoverManager] PlayMumbleSequence: playing '{clip.name}' ({clip.length:F2}s) isPlaying={_mumbleSource.isPlaying}");

                // Wait for the clip to finish (bail early if sequence ended)
                float elapsed = 0f;
                while (elapsed < clip.length && _takeoverActive)
                {
                    elapsed += Time.deltaTime;
                    yield return null;
                }

                if (!_takeoverActive || _mumbleSource == null) break;

                // Brief pause between clips (0.4–1.2 s)
                float gap = UnityEngine.Random.Range(0.4f, 1.2f);
                float gapElapsed = 0f;
                while (gapElapsed < gap && _takeoverActive)
                {
                    gapElapsed += Time.deltaTime;
                    yield return null;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Step 5 — Dialogue Typewriter
        // ─────────────────────────────────────────────────────────────────────

        private IEnumerator ShowDialogue(string passage)
        {
            _dialogueCanvasGO = new GameObject("Y4NGZ_DialogueCanvas");
            DontDestroyOnLoad(_dialogueCanvasGO);

            var canvas = _dialogueCanvasGO.AddComponent<Canvas>();
            canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;

            var scaler = _dialogueCanvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution  = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight   = 1f;

            _dialogueCanvasGO.AddComponent<GraphicRaycaster>();

            var textGO = new GameObject("Y4NGZ_DialogueText");
            textGO.transform.SetParent(_dialogueCanvasGO.transform, false);

            var tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.fontSize           = 28f;
            tmp.color              = new Color(0f, 1f, 0.255f, 1f);   // #00FF41
            tmp.alignment          = TextAlignmentOptions.Bottom;
            tmp.enableWordWrapping  = true;
            tmp.richText            = false;
            tmp.overflowMode       = TextOverflowModes.Overflow;
            tmp.text               = "";
            AssignVanillaFont(tmp);

            var rect = tmp.rectTransform;
            rect.anchorMin = new Vector2(0.1f, 0.02f);
            rect.anchorMax = new Vector2(0.9f, 0.30f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            float charDelay = TakeoverBootstrap.CfgTypewriterSpeed.Value;

            // Typewriter tick sound — short procedural click for each character.
            // Fixed at 0.8 volume independent of mumble volume: at the previous
            // setting (≤0.55 × mumble), the tick was getting swallowed by the
            // degraded-speaker mumble and atmospheric drone.
            var tickClip = TypewriterAudio.CreateTick();
            var tickGO   = new GameObject("Y4NGZ_TypewriterTick");
            tickGO.transform.SetParent(_dialogueCanvasGO.transform); // destroyed with canvas
            var tickSrc  = tickGO.AddComponent<AudioSource>();
            tickSrc.spatialBlend = 0f;
            tickSrc.volume       = 0.8f;
            tickSrc.playOnAwake  = false;

            string built = "";
            foreach (char c in passage)
            {
                if (tmp == null) yield break;
                built    += c;
                tmp.text  = built;
                if (!char.IsWhiteSpace(c))
                {
                    if (tickSrc != null)
                        tickSrc.PlayOneShot(tickClip);
                    yield return new WaitForSeconds(charDelay);
                }
            }

            // After the full passage is revealed, the text stays on screen for the
            // remainder of the takeover — no mid-sequence fade. RestoreAll() destroys
            // the canvas at the end of the sequence, so the dialogue and the sequence
            // end together rather than leaving dead air after a single passage fades.
            // The coroutine simply holds here until the canvas is destroyed externally.
            while (_dialogueCanvasGO != null)
                yield return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Step 6 — CRT Channel-Change Transition
        // ─────────────────────────────────────────────────────────────────────

        // Classic old-TV channel change: kill audio/dialogue → white phosphor
        // flash → static snow → black → then RestoreAll brings back the original
        // monitor content. Monitors stay black during the 1.5s light restore,
        // then pop back on — like the tube warming up again.
        private IEnumerator CRTTransition()
        {
            // Kill audio and dialogue — the "TV" is shutting off
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #861: closed first, so a soundtrack failure from here on recovers nothing.
            _audioBedOpen = false;
#endif
            if (_dialogueCanvasGO != null) { Destroy(_dialogueCanvasGO); _dialogueCanvasGO = null; }
            // The handle stays set until RestoreAll, so nothing can start the loop again.
            if (_mumbleCoroutine != null) StopCoroutine(_mumbleCoroutine);
            if (_mumbleSource != null) _mumbleSource.Stop();
            if (_alarmSource != null) _alarmSource.Stop();
            if (_droneSource != null) _droneSource.Stop();
            // #662: before the first SetAllMonitorTexture below. A mirror loop still running
            // would blit the primary's picture back over the white/snow/black frames.
            StopAdditionalMediaPlayback();
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            StopSoundtrack("CRT transition");
#endif

            var white = CRTStatic.CreateSolidTexture(Color.white);
            var black = CRTStatic.CreateSolidTexture(Color.black);
            var noise = CRTStatic.CreateNoiseTexture(256, 256);

            // Static burst audio — TV snow sound
            PlayStaticBurst();

            // White phosphor flash
            SetAllMonitorTexture(white);
            yield return new WaitForSeconds(0.06f);

            // Static / snow
            SetAllMonitorTexture(noise);
            yield return new WaitForSeconds(0.25f);

            // Black (tube off)
            SetAllMonitorTexture(black);
            yield return new WaitForSeconds(0.15f);

            // Delayed cleanup — textures stay valid through RestoreAll
            Destroy(white, 5f);
            Destroy(black, 5f);
            Destroy(noise, 5f);
        }

        // Sets every overridden monitor surface to a given texture.
        private void SetAllMonitorTexture(Texture tex)
        {
            ShipSystemsTakeoverBridge.UpdateExternalMonitorTakeoverTexture(tex);
            _generalImprovementsMonitorLease?.UpdateTexture(tex);

            foreach (var img in _topMonitorOverlayImages)
                if (img != null) img.texture = tex;

            if (_videoReelRawImage != null)
                _videoReelRawImage.texture = tex;

            foreach (var (r, _) in _overriddenRenderers)
            {
                try
                {
                    if (r == null) continue;
                    // Iterate all material slots — slot 0 is the HDRP/Lit
                    // bezel/TerminalTex submesh, slots 1+ are the unlit large
                    // monitor screens. r.material only updates slot 0; we need
                    // every slot to flash white/snow/black for the CRT effect.
                    var mats = r.materials;
                    if (mats == null) continue;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;
                        try { m.mainTexture = tex; } catch { }
                        if (m.HasProperty("_BaseColorMap"))     m.SetTexture("_BaseColorMap", tex);
                        if (m.HasProperty("_UnlitColorMap"))    m.SetTexture("_UnlitColorMap", tex);
                        if (m.HasProperty("_EmissiveColorMap")) m.SetTexture("_EmissiveColorMap", tex);
                    }
                }
                catch { }
            }
        }

        // Short white-noise burst — the crackle of a TV between channels.
        private void PlayStaticBurst()
        {
            var clip = CRTStatic.CreateBurstClip();

            var go  = new GameObject("Y4NGZ_StaticBurst");
            var src = go.AddComponent<AudioSource>();
            src.spatialBlend = 0f;
            src.volume       = 0.5f;
            src.PlayOneShot(clip);
            Destroy(go, 1.5f);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Step 7 — Restore
        // ─────────────────────────────────────────────────────────────────────

        private IEnumerator RestoreCoroutine()
        {
            if (TakeoverBootstrap.CfgDimLights.Value && _savedLights.Count > 0)
                yield return StartCoroutine(RestoreLights());
            RestoreAll(immediate: false);
        }

        private IEnumerator RestoreLights()
        {
            var snapshot = new List<(Light l, float curI, Color curC, float targI, Color targC)>();
            foreach (var (l, origI, origC) in _savedLights)
            {
                if (l != null)
                    snapshot.Add((l, l.intensity, l.color, origI, origC));
            }

            float duration = 1.5f, elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float s = Mathf.SmoothStep(0f, 1f, elapsed / duration);
                foreach (var (l, cI, cC, tI, tC) in snapshot)
                {
                    if (l == null) continue;
                    l.intensity = Mathf.Lerp(cI, tI, s);
                    l.color     = Color.Lerp(cC, tC, s);
                }
                yield return null;
            }
            foreach (var (l, _, _, tI, tC) in snapshot)
            {
                if (l != null) { l.intensity = tI; l.color = tC; }
            }
            _savedLights.Clear();
        }

        private void RestoreAll(bool immediate)
        {
            var sor = StartOfRound.Instance;

            // Restore HUD (alpha-based — no animator triggered)
            RestoreHUD();

            // Restore top monitor UI components. Not gated on StartOfRound —
            // the list holds direct component references (vanilla + clones), so
            // it must unwind even if the round object has already gone away.
            try
            {
                foreach (var (graphic, wasEnabled) in _disabledMonitorGraphics)
                    SetEnabled(graphic, wasEnabled);
            }
            catch { }
            _disabledMonitorGraphics.Clear();

            // Restore video reel
            if (sor != null)
            {
                try
                {
                    var vr = sor.screenLevelVideoReel;
                    if (vr != null)
                    {
                        vr.Stop();
                        vr.clip      = _savedVideoClip;
                        vr.isLooping = _savedVideoLooping;
                        if (_savedVideoPlaying && _savedVideoClip != null) vr.Play();
                    }
                    SetEnabled(sor.screenLevelDescription, _levelDescWasEnabled);
                }
                catch { }
            }

            // Restore the MapScreenVideo RawImage
            if (_videoReelRawImage != null)
            {
                try { _videoReelRawImage.texture = _savedVideoReelTexture; }
                catch { }
                _videoReelRawImage    = null;
                _savedVideoReelTexture = null;
            }

            // Detach top-monitor overlays synchronously. Object.Destroy is
            // deferred until end-of-frame; leaving an enabled RawImage bound
            // to the soon-to-be-released takeover RT produces the bright-green
            // rectangles seen during restore.
            foreach (var img in _topMonitorOverlayImages)
            {
                if (img == null) continue;
                try
                {
                    img.enabled = false;
                    img.texture = null;
                    img.gameObject.SetActive(false);
                    Destroy(img.gameObject);
                }
                catch { }
            }
            _topMonitorOverlayImages.Clear();

            if (_generalImprovementsLeaseCoroutine != null)
            {
                StopCoroutine(_generalImprovementsLeaseCoroutine);
                _generalImprovementsLeaseCoroutine = null;
            }
            _generalImprovementsMonitorLease?.Release();
            _generalImprovementsMonitorLease = null;

            // Re-enable ManualCameraRenderer components BEFORE restoring textures.
            // Order matters: the MCR's OnEnable/Update sets material.mainTexture back to
            // its camera's RT. If we restore textures first, the MCR's next frame
            // overwrites them anyway. Re-enabling first, then force-restoring
            // non-MCR textures below, gives MCR-driven meshes their live camera feed
            // back and leaves other meshes with their original textures.
            foreach (var (component, wasEnabled) in _disabledComponents)
            {
                try { if (component != null) component.enabled = wasEnabled; }
                catch { }
            }
            _disabledComponents.Clear();

            // Restore all monitor materials (runs after MCR re-enable so MCR-owned
            // meshes get a final pass from the live camera, not our saved stub).
            foreach (var (r, origTex) in _overriddenRenderers)
            {
                try
                {
                    if (r == null) continue;
                    var mats = r.materials;
                    if (r.sharedMaterial != null) r.sharedMaterial.mainTexture = origTex;

                    _savedHDRPState.TryGetValue(r, out var savedSlots);
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;
                        // Only restore slots we actually overrode. Frame/chassis
                        // slots were skipped on apply — leave them alone.
                        if (savedSlots == null || i >= savedSlots.Length) continue;
                        var s = savedSlots[i];
                        if (!s.overridden) continue;

                        // Restore the per-slot mainTexture (camera RT for slot
                        // 1/2 — mapTexture / shipScreen1 / shipScreen2). Using
                        // the renderer-level origTex here would clobber slot
                        // 1/2 with the slot-0 ComputerTerminalTexture and
                        // leave the camera screens scrambled after restore.
                        try { m.mainTexture = s.mainTex; } catch { }
                        if (m.HasProperty("_BaseColorMap"))     m.SetTexture("_BaseColorMap",     s.baseColor);
                        if (m.HasProperty("_UnlitColorMap"))    m.SetTexture("_UnlitColorMap",    s.unlitColor);
                        if (m.HasProperty("_EmissiveColorMap")) m.SetTexture("_EmissiveColorMap", s.emissive);
                        if (m.HasProperty("_EmissiveColor"))    m.SetColor("_EmissiveColor",      s.emissiveColor);
                        if (s.hadEmissionKW)    m.EnableKeyword("_EMISSION");    else m.DisableKeyword("_EMISSION");
                        if (s.hadEmissiveMapKW) m.EnableKeyword("_EMISSIVE_COLOR_MAP"); else m.DisableKeyword("_EMISSIVE_COLOR_MAP");
                    }
                    r.SetPropertyBlock(null);
                }
                catch { }
            }
            _overriddenRenderers.Clear();
            _savedHDRPState.Clear();

            // Snap lights if immediate
            if (immediate)
            {
                foreach (var (l, origI, origC) in _savedLights)
                {
                    try { if (l != null) { l.intensity = origI; l.color = origC; } }
                    catch { }
                }
                _savedLights.Clear();
            }

            // Restore speaker volume
            if (_speakerVolumeSaved && sor?.speakerAudioSource != null)
            {
                sor.speakerAudioSource.volume = _savedSpeakerVolume;
                _speakerVolumeSaved = false;
            }

            // MCR re-enable already happened before texture restore (see above).

            // Stop flash if still running (safety — normally stopped before CRT)
            if (_flashCoroutine != null) { StopCoroutine(_flashCoroutine); _flashCoroutine = null; }

            // Stop MapScreenVideo pin-blit so the VP can resume naturally
            if (_mapBlitCoroutine != null) { StopCoroutine(_mapBlitCoroutine); _mapBlitCoroutine = null; }
            _mapScreenRT = null;

            // Destroy temp objects
            if (_videoFallbackCoroutine != null) { StopCoroutine(_videoFallbackCoroutine); _videoFallbackCoroutine = null; }
            if (_signalLostCoroutine != null) { StopCoroutine(_signalLostCoroutine); _signalLostCoroutine = null; }
            DestroySignalLostFrames();
            _usingConfiguredVideoSource = false;
            if (_videoPlayer != null)
            {
                _videoPlayer.errorReceived    -= OnVideoErrorReceived;
                _videoPlayer.prepareCompleted -= OnVideoPrepareCompleted;
                Destroy(_videoPlayer);
                _videoPlayer = null;
            }
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #861: closed before anything below can raise a player event, so no failure from
            // here on starts a bed layer: ForceRestore never revives audio.
            _audioBedOpen = false;
            _droneSuppressed = false;
            _mumbleSuppressed = false;
            _soundtrackConfirmed = false;
            // #715. Order matters: the hidden player must let go of the dummy RT before the RT
            // is released, and the AudioSource must outlive the player that targets it.
            if (_soundtrackCoroutine != null) { StopCoroutine(_soundtrackCoroutine); _soundtrackCoroutine = null; }
            DisposeSoundtrackPlayer();
            if (_soundtrackSource != null) { Destroy(_soundtrackSource.gameObject); _soundtrackSource = null; }
            _soundtrackPlan = default;
#endif
            if (_mumbleCoroutine != null) { StopCoroutine(_mumbleCoroutine); _mumbleCoroutine = null; }
            if (_alarmSource  != null) { Destroy(_alarmSource.gameObject); _alarmSource = null; }
            if (_droneSource  != null) { Destroy(_droneSource.gameObject); _droneSource = null; }
            if (_mumbleSource != null) { Destroy(_mumbleSource.gameObject); _mumbleSource = null; }
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #861: nothing plays this takeover's clips any more; the next preparation may evict
            // them.
            _audio = TakeoverAudioSnapshot.Empty;
            TakeoverAudioOverrides.ReleaseCapture();
#endif
            if (_dialogueCanvasGO != null) { Destroy(_dialogueCanvasGO); _dialogueCanvasGO = null; }
            // Restore OpenBodyCams first, preserving its original ordering
            // relative to the physical-surface bridge. Both must detach from
            // takeover state before that render texture is released.
            OpenBodyCamsCompat.Restore();
            // Claim-owned ticker/fuel surfaces must restore their saved feed
            // bindings before the takeover RT is released. A released RT is
            // rendered as bright green by Unity.
            ShipSystemsTakeoverBridge.EndExternalMonitorTakeover();
            // #662: after the bridge has let go, for the same reason the primary RT below waits
            // for it - a released RenderTexture renders as bright green on anything still
            // bound to it.
            DestroyAdditionalMediaPlayers();
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Destroy(_renderTexture);
                _renderTexture = null;
            }
            if (_customImageTexture != null)
            {
                Destroy(_customImageTexture);
                _customImageTexture = null;
            }
            _savedVideoClip = null;

        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static bool SetEnabled(Behaviour component, bool enabled)
        {
            if (component == null) return false;
            bool was = component.enabled;
            component.enabled = enabled;
            return was;
        }

        private static void AssignVanillaFont(TextMeshProUGUI tmp)
        {
            try
            {
                if (HUDManager.Instance?.clockNumber?.font != null)
                {
                    tmp.font = HUDManager.Instance.clockNumber.font;
                    return;
                }
            }
            catch { }

            try
            {
                var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                foreach (var f in fonts)
                {
                    if (f.name.Contains("3270") || f.name.Contains("edunline") ||
                        f.name.Contains("EdgeOf") || f.name.Contains("Consolas"))
                    {
                        tmp.font = f;
                        return;
                    }
                }
            }
            catch { }
        }
    }
}
