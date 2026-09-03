using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCore.Modules;
using Y4NGZCore.Modules.Quota;
using Y4NGZCompany.ShipSystems.Takeover;
namespace Y4NGZCompany.Core
{
    public readonly struct QuotaTokenGrantRequest
    {
        public readonly int CompletedQuota;
        public readonly int TokensPerPlayer;
        public readonly string ActionId;

        /// <summary>
        /// True when this request re-delivers a milestone that already landed in the host ledger,
        /// for example after a late joiner connects. Providers must treat replays as idempotent.
        /// Additive since token-grant protocol v1; older providers simply ignore the field.
        /// </summary>
        public readonly bool IsReplay;

        public QuotaTokenGrantRequest(int completedQuota, int tokensPerPlayer, string actionId)
            : this(completedQuota, tokensPerPlayer, actionId, false)
        {
        }

        public QuotaTokenGrantRequest(int completedQuota, int tokensPerPlayer, string actionId, bool isReplay)
        {
            CompletedQuota = completedQuota;
            TokensPerPlayer = tokensPerPlayer;
            ActionId = actionId ?? string.Empty;
            IsReplay = isReplay;
        }
    }

    public static class QuotaProgressionApi
    {
        /// <summary>
        /// Version of the token-grant contract: the shape of <see cref="QuotaTokenGrantRequest"/> and
        /// the handler registration surface. Providers read this reflectively and refuse to register on
        /// mismatch. A provider that cannot find this constant is talking to a pre-handshake build and
        /// must treat it as version 1. Bump only on a breaking change (removed/renamed/retyped field).
        /// </summary>
        public const int TokenGrantProtocolVersion = 1;

        private static Func<QuotaTokenGrantRequest, bool> _tokenGrantHandler;

        public static void RegisterTokenGrantHandler(Func<QuotaTokenGrantRequest, bool> handler) => _tokenGrantHandler = handler;
        public static void UnregisterTokenGrantHandler(Func<QuotaTokenGrantRequest, bool> handler)
        {
            if (_tokenGrantHandler == handler) _tokenGrantHandler = null;
        }

        internal static bool TryGrantTokens(QuotaTokenGrantRequest request) => _tokenGrantHandler != null && _tokenGrantHandler(request);
        public static bool IsTokenGrantCapabilityAvailable => _tokenGrantHandler != null;
    }

    /// <summary>
    /// How a configured takeover soundtrack relates to the built-in audio bed (#715).
    /// <c>Off</c> is the default and must leave the takeover byte-for-byte as it was before
    /// this feature existed.
    /// </summary>
    internal enum TakeoverSoundtrackMode
    {
        Off,
        Replace,
        Mix
    }

    /// <summary>
    /// The host's soundtrack decision for the current takeover, as it travels on the wire (#715).
    ///
    /// <para>This is deliberately NOT part of <see cref="QuotaTakeoverPayload"/>. Appending
    /// fields to <c>Y4NGZCompany.QuotaTakeoverPayload.v2</c> is not additive-safe — a new client
    /// reading an old host's shorter buffer overruns — and renaming it to v3 would strip sync
    /// for every 1.x peer. It rides its own named message instead, so a peer that never
    /// registered that handler simply drops it and keeps today's audio bed.</para>
    /// </summary>
    internal readonly struct TakeoverSoundtrackPlanWire
    {
        internal readonly TakeoverSoundtrackMode Mode;
        internal readonly string Source;
        internal readonly string Hash;
        internal readonly float Volume;

        internal TakeoverSoundtrackPlanWire(TakeoverSoundtrackMode mode, string source, string hash, float volume)
        {
            Mode = mode;
            Source = source ?? string.Empty;
            Hash = hash ?? string.Empty;
            Volume = volume;
        }

        internal bool IsOff => Mode == TakeoverSoundtrackMode.Off || string.IsNullOrWhiteSpace(Source);
    }

    /// <summary>
    /// The host's per-monitor media decision for the current takeover, as it travels on the
    /// wire (#662).
    ///
    /// <para>A sidecar for exactly the reason <see cref="TakeoverSoundtrackPlanWire"/> is one:
    /// appending to <c>Y4NGZCompany.QuotaTakeoverPayload.v2</c> is not additive-safe, and the
    /// pool does not fit its <c>FixedString512Bytes</c> media field in any case. A peer that
    /// never registered the handler drops the message and runs the ordinary single-source
    /// takeover, so <c>MonitorTakeoverProtocolRevision</c> stays 1.</para>
    ///
    /// <para><see cref="Pool"/> and <see cref="Hashes"/> are index-aligned; a hash is empty for
    /// a YouTube entry, which every peer verifies through its own download of the same
    /// normalized link rather than through the host's bytes.</para>
    /// </summary>
    internal readonly struct TakeoverMediaPlanWire
    {
        internal readonly int Seed;
        internal readonly int MaxPlayers;
        internal readonly string[] Pool;
        internal readonly string[] Hashes;

        internal TakeoverMediaPlanWire(int seed, int maxPlayers, string[] pool, string[] hashes)
        {
            Seed = seed;
            MaxPlayers = maxPlayers;
            Pool = pool ?? Array.Empty<string>();
            Hashes = hashes ?? Array.Empty<string>();
        }

        /// <summary>
        /// True when the host published a pool worth splitting across monitors. One entry is a
        /// published plan that says "everyone on the same source", which is the ordinary
        /// takeover and is handled without any of the extra machinery.
        /// </summary>
        internal bool HasMultipleSources => Pool != null && Pool.Length > 1;
    }

    internal readonly struct QuotaTakeoverPayload
    {
        internal readonly int Quota;
        internal readonly string Dialogue;
        internal readonly string MediaFile;
        internal readonly string MediaHash;
        internal readonly int SelectionId;
        // Loose-file audio overrides for the quota takeover only. Both list
        // fields are comma-joined and index-aligned; an entry is only present
        // when the host resolved and hashed the file itself.
        internal readonly string AlarmAudioFile;
        internal readonly string AlarmAudioHash;
        internal readonly string MumbleAudioFiles;
        internal readonly string MumbleAudioHashes;

        internal QuotaTakeoverPayload(int quota, string dialogue, string mediaFile, string mediaHash, int selectionId)
            : this(quota, dialogue, mediaFile, mediaHash, selectionId, string.Empty, string.Empty, string.Empty, string.Empty)
        {
        }

        internal QuotaTakeoverPayload(
            int quota,
            string dialogue,
            string mediaFile,
            string mediaHash,
            int selectionId,
            string alarmAudioFile,
            string alarmAudioHash,
            string mumbleAudioFiles,
            string mumbleAudioHashes)
        {
            Quota = quota;
            Dialogue = dialogue ?? string.Empty;
            MediaFile = mediaFile ?? string.Empty;
            MediaHash = mediaHash ?? string.Empty;
            SelectionId = selectionId;
            AlarmAudioFile = alarmAudioFile ?? string.Empty;
            AlarmAudioHash = alarmAudioHash ?? string.Empty;
            MumbleAudioFiles = mumbleAudioFiles ?? string.Empty;
            MumbleAudioHashes = mumbleAudioHashes ?? string.Empty;
        }

        internal QuotaTakeoverPayload WithDialogue(string dialogue, int selectionId) =>
            new QuotaTakeoverPayload(Quota, dialogue, MediaFile, MediaHash, selectionId,
                AlarmAudioFile, AlarmAudioHash, MumbleAudioFiles, MumbleAudioHashes);
    }

    // A takeover audio file that has been resolved to an absolute path inside
    // the takeover media directory and verified against the host's hash.
    internal readonly struct TakeoverAudioOverrideFile
    {
        internal readonly string Path;
        internal readonly string Hash;

        internal TakeoverAudioOverrideFile(string path, string hash)
        {
            Path = path ?? string.Empty;
            Hash = hash ?? string.Empty;
        }

        internal bool IsValid => !string.IsNullOrEmpty(Path);
        internal string CacheKey => (Path ?? string.Empty) + "|" + (Hash ?? string.Empty);
    }

    internal sealed class QuotaMilestoneConfig
    {
        internal readonly int Quota;
        internal readonly ConfigEntry<bool> Enable;
        internal readonly ConfigEntry<string> Dialogue;
        internal readonly ConfigEntry<string> DialogueFile;
        internal readonly ConfigEntry<string> MediaFile;
        internal readonly ConfigEntry<string> UnlockContracts;
        internal readonly ConfigEntry<string> UnlockShipUpgradePurchases;
        internal readonly ConfigEntry<string> UnlockConstellations;
        internal readonly ConfigEntry<string> UnlockSuits;
        internal readonly ConfigEntry<string> UnlockStoreItems;
        internal readonly ConfigEntry<int> GrantTokensPerPlayer;
        internal readonly ConfigEntry<int> GrantGroupCredits;
        internal readonly ConfigEntry<float> GrantShipFuel;

        internal QuotaMilestoneConfig(ConfigFile config, int quota)
        {
            Quota = quota;
            // #714: plain "Quota N". MonitorTakeoverConfigSchemaMigration owns the name so the
            // migration that carries a pre-#714 profile's per-milestone values cannot drift from
            // the section those values have to land in.
            string section = MonitorTakeoverConfigSchemaMigration.QuotaSection(quota);
            Enable = config.Bind(section, "Enable", true, "Enable this quota milestone.");
            Dialogue = config.Bind(section, "Dialogue", string.Empty, "Inline takeover dialogue. Use \\n for line breaks; blank uses built-in dialogue.");
            DialogueFile = config.Bind(section, "DialogueFile", string.Empty, "Optional UTF-8 .txt file under BepInEx/config/Y4NGZCompany/MonitorTakeovers.");
            MediaFile = config.Bind(section, "MediaFile", string.Empty, "Optional PNG, JPG, or MP4 filename in BepInEx/config/Y4NGZCompany/MonitorTakeovers used for this quota's takeover, or a full YouTube link (watch, Shorts, Live, Embed, or youtu.be). Files need an identical copy on every player or that player falls back to the built-in media; a YouTube link is downloaded and cached by each player in the background instead.");
            UnlockContracts = config.Bind(section, "UnlockContracts", string.Empty, "Comma-separated stable contract IDs that begin rolling at this quota.");
            UnlockShipUpgradePurchases = config.Bind(section, "UnlockShipUpgradePurchases", string.Empty, "Comma-separated stable ship-upgrade IDs unlocked for purchase at this quota.");
            UnlockConstellations = config.Bind(section, "UnlockConstellations", string.Empty, "Comma-separated constellation names unlocked at this quota.");
            UnlockSuits = config.Bind(section, "UnlockSuits", string.Empty, "Comma-separated suit unlockable names (as in StartOfRound.unlockablesList, e.g. Green suit) hidden from the suit rack until this quota.");
            UnlockStoreItems = config.Bind(section, "UnlockStoreItems", string.Empty, "Comma-separated store item names (as in Terminal.buyableItemsList, e.g. Pro-flashlight) unavailable for purchase until this quota.");
            GrantTokensPerPlayer = config.Bind(section, "GrantTokensPerPlayer", 3,
                new ConfigDescription("Tokens granted once per eligible player through Y4NGZUpgrades.", new AcceptableValueRange<int>(0, 1000)));
            GrantGroupCredits = config.Bind(section, "GrantGroupCredits", 0,
                new ConfigDescription("Group credits granted once when this quota completes.", new AcceptableValueRange<int>(0, 100000)));
            GrantShipFuel = config.Bind(section, "GrantShipFuel", 0f,
                new ConfigDescription("Ship fuel granted once when this quota completes.", new AcceptableValueRange<float>(0f, 1000f)));
        }

        internal IEnumerable<string> Contracts => Split(UnlockContracts.Value);
        internal IEnumerable<string> Upgrades => Split(UnlockShipUpgradePurchases.Value);
        internal IEnumerable<string> Constellations => Split(UnlockConstellations.Value);
        internal IEnumerable<string> Suits => Split(UnlockSuits.Value);
        internal IEnumerable<string> StoreItems => Split(UnlockStoreItems.Value);

        private static IEnumerable<string> Split(string value) => (value ?? string.Empty)
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0);
    }

    internal static class QuotaProgressionRegistry
    {
        private const string LedgerSaveKey = "Y4NGZCompany.QuotaProgressionLedger.v1";
        // v2 added the loose-file audio override fields. The identifier is bumped
        // rather than extended in place so a v1 peer never reads a v2 buffer.
        private const string PayloadMessage = "Y4NGZCompany.QuotaTakeoverPayload.v2";
        private const string GateSnapshotMessage = "Y4NGZCompany.QuotaProgressionSnapshot.v2";
        // #715. Additive by construction: a peer that never registered this handler drops the
        // message and keeps today's audio bed (== SoundtrackMode=Off). The v2 payload is
        // unchanged, so MonitorTakeoverProtocolRevision stays 1.
        private const string SoundtrackMessage = "Y4NGZCompany.QuotaTakeoverSoundtrack.v1";
        // #662. Same additive construction, same reason: a peer that never registered this
        // handler drops the message and runs the single-source takeover it always did. The v2
        // payload is untouched, so MonitorTakeoverProtocolRevision stays 1.
        private const string MediaPlanMessage = "Y4NGZCompany.QuotaTakeoverMediaPlan.v1";
        // Never more entries than the cap could ever open decoders for, and a joined length
        // that leaves headroom inside FixedString4096Bytes for the separators.
        private const int MaxPerMonitorPoolEntries = 6;
        private const int MaxPerMonitorPoolChars = 3000;
        private static readonly Dictionary<int, QuotaMilestoneConfig> Milestones = new Dictionary<int, QuotaMilestoneConfig>();
        private static readonly string[] MediaExtensions = { ".png", ".jpg", ".jpeg", ".mp4" };
        private static readonly string[] AudioExtensions = { ".mp3", ".wav", ".ogg" };
        // Per-filename truncation matches the media field; the count and joined
        // caps keep both index-aligned lists inside their payload fields so a
        // truncation on the wire can never shift a hash onto the wrong file.
        private const int MaxOverrideFileNameChars = 120;
        // A YouTube link travels in the same FixedString512Bytes media field as a
        // filename but is longer than any sane filename; it gets its own cap just
        // under the field's 509-byte capacity instead of the filename cap (#661).
        private const int MaxYoutubeUrlChars = 500;
        private const int MaxMumbleOverrideClips = 12;
        private const int MaxMumbleOverrideNameChars = 1000;
        private const int MaxMumbleOverrideHashChars = 1000;
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<bool> _retryPending;
        private static ConfigEntry<bool> _randomizeDialogue;
        private static ConfigEntry<bool> _randomizeWithoutReplacement;
        private static ConfigEntry<string> _defaultMediaFile;
        private static ConfigEntry<string> _mumbleAudioFiles;
        private static ConfigEntry<string> _alarmAudioFile;
        private static ConfigEntry<string> _soundtrackSource;
        private static ConfigEntry<TakeoverSoundtrackMode> _soundtrackMode;
        private static ConfigEntry<float> _soundtrackVolume;
        private static ConfigEntry<bool> _loopMedia;
        private static ConfigEntry<bool> _enableMediaAudio;
        private static ConfigEntry<bool> _useMediaLengthAsDuration;
        private static ConfigEntry<bool> _randomPerMonitorMedia;
        private static ConfigEntry<string> _perMonitorMediaPool;
        private static ConfigEntry<int> _maxConcurrentMediaPlayers;
        private static ConfigEntry<bool> _enableUnlockAnnouncement;
        private static ConfigEntry<float> _unlockAnnouncementSeconds;
        private static ConfigEntry<bool> _announceOnBothMonitors;
        private static ConfigEntry<bool> _rewardsCeremonyEnabled;
        private static ConfigEntry<bool> _ceremonyMirrorBothMonitors;
        private static ConfigEntry<float> _ceremonyTypewriterSecondsPerChar;
        private static ConfigEntry<bool> _ceremonyLineConfirmSting;
        private static ConfigEntry<bool> _ceremonyRestoreStatic;
        private static ManualLogSource _log;
        // #435: registration is latched on the CustomMessagingManager - NGO makes a new one for
        // every StartHost/StartClient while NetworkManager.Singleton persists, so a
        // NetworkManager-keyed guard skips registration in a session's second lobby. The
        // NetworkManager itself is still tracked so the OnClientConnectedCallback subscription
        // can be detached from the manager it was attached to.
        private static CustomMessagingManager _registeredMessaging;
        private static NetworkManager _registeredNetworkManager;
        private static QuotaTakeoverPayload _currentPayload;
        private static bool _hasCurrentPayload;
        // #715. Default is Off, which is exactly what a peer that never received the sidecar
        // message must see, so no "has published" flag is needed.
        private static TakeoverSoundtrackPlanWire _soundtrackPlan;
        // #662. Default is an empty pool, which is exactly what a peer that never received the
        // sidecar must see: no per-monitor plan, one shared source.
        private static TakeoverMediaPlanWire _mediaPlan;
        private static bool _hasAuthoritativeUpgradeSnapshot;
        private static readonly HashSet<string> AuthoritativeUnlockedUpgrades = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> AuthoritativeConstellationUnlockQuotas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> AuthoritativeSuitUnlockQuotas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> AuthoritativeStoreItemUnlockQuotas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static int _authoritativeQuota;
        private static float _nextSnapshotAt;
        private static string _activeSaveName = string.Empty;
        private static string _cachedLedgerSave = string.Empty;
        private static HashSet<string> _cachedLedger;
        private static Action<ulong> _clientConnectedHandler;
        private static float _nextTokenReplayAt;
        private static int _tokenReplayAttemptsRemaining;
        private const float TokenReplayFirstDelaySeconds = 6f;
        private const float TokenReplayIntervalSeconds = 6f;
        private const int TokenReplayAttempts = 3;

        internal static string MediaDirectory => Path.Combine(Y4NGZCompanyPaths.LocalDataDir, "MonitorTakeovers");
        internal static bool RandomizeDialogue => _randomizeDialogue?.Value == true;
        internal static bool LoopMedia => _loopMedia?.Value != false;
        internal static bool EnableMediaAudio => _enableMediaAudio?.Value == true;
        internal static bool UseMediaLengthAsDuration => _useMediaLengthAsDuration?.Value == true;
        internal static bool EnableUnlockAnnouncement => _enableUnlockAnnouncement?.Value != false;
        internal static float UnlockAnnouncementSeconds => _unlockAnnouncementSeconds?.Value ?? 4.5f;
        internal static bool AnnounceOnBothMonitors => _announceOnBothMonitors?.Value == true;
        internal static bool RewardsCeremonyEnabled => _rewardsCeremonyEnabled?.Value != false;
        internal static bool CeremonyMirrorBothMonitors => _ceremonyMirrorBothMonitors?.Value != false;
        internal static float CeremonyTypewriterSecondsPerChar =>
            Mathf.Clamp(_ceremonyTypewriterSecondsPerChar?.Value ?? 0.03f, 0.005f, 0.2f);
        internal static bool CeremonyLineConfirmSting => _ceremonyLineConfirmSting?.Value != false;
        internal static bool CeremonyRestoreStatic => _ceremonyRestoreStatic?.Value != false;
        internal static TakeoverSoundtrackMode SoundtrackMode => _soundtrackMode?.Value ?? TakeoverSoundtrackMode.Off;
        internal static float SoundtrackVolume => Mathf.Clamp01(_soundtrackVolume?.Value ?? 0.8f);
        // #662 per-monitor media. Off is the shipped default and must leave the takeover
        // byte-for-byte as it was: one player, one RenderTexture, one source on every face.
        internal static bool RandomPerMonitorMedia => _randomPerMonitorMedia?.Value == true;
        internal static int MaxConcurrentMediaPlayers =>
            Mathf.Clamp(_maxConcurrentMediaPlayers?.Value ?? 3, 1, 6);
        internal static string[] PerMonitorMediaPool() => SplitSemicolonList(_perMonitorMediaPool?.Value);

        internal static void Initialize(ConfigFile config, ManualLogSource log)
        {
            _log = log;
            _enabled = config.Bind(MonitorTakeoverConfigSchemaMigration.GeneralSection, "EnableQuotaProgression", true,
                "Enable quota-based unlock gates, rewards, and takeover presentation.");
            _retryPending = config.Bind(MonitorTakeoverConfigSchemaMigration.GeneralSection, "RetryPendingOptionalRewards", true,
                "Retry optional rewards such as tokens when their provider becomes available later.");
            _randomizeDialogue = config.Bind(MonitorTakeoverConfigSchemaMigration.PresentationSection, "RandomizeDialogue", false,
                "Choose configured dialogue from any enabled quota instead of attaching dialogue to its quota.");
            _randomizeWithoutReplacement = config.Bind(MonitorTakeoverConfigSchemaMigration.PresentationSection, "RandomizeWithoutReplacement", true,
                "When randomizing, use every available passage once before the deterministic pool repeats.");
            _defaultMediaFile = config.Bind(MonitorTakeoverConfigSchemaMigration.MediaSection, "DefaultMediaFile", string.Empty,
                "Optional PNG, JPG, or MP4 filename in BepInEx/config/Y4NGZCompany/MonitorTakeovers used when a quota has no MediaFile, or a full YouTube link (watch, Shorts, Live, Embed, or youtu.be). Files need an identical copy on every player or that player falls back to the built-in media; a YouTube link is downloaded and cached by each player in the background instead.");
            _mumbleAudioFiles = config.Bind(MonitorTakeoverConfigSchemaMigration.AudioSection, "MumbleAudioFiles", string.Empty,
                "Comma-separated MP3, WAV, or OGG filenames in BepInEx/config/Y4NGZCompany/MonitorTakeovers that replace the quota takeover's mumble voice clips. Every player in the lobby needs an identical copy or that player falls back to the built-in audio.");
            _alarmAudioFile = config.Bind(MonitorTakeoverConfigSchemaMigration.AudioSection, "AlarmAudioFile", string.Empty,
                "Single MP3, WAV, or OGG filename in BepInEx/config/Y4NGZCompany/MonitorTakeovers that replaces the quota takeover's alarm sound. Every player in the lobby needs an identical copy or that player falls back to the built-in audio.");
            // #715 takeover soundtrack. Born in Audio; default Off so an untouched profile keeps
            // the built-in bed exactly as it was.
            _soundtrackSource = config.Bind(MonitorTakeoverConfigSchemaMigration.AudioSection, "SoundtrackSource", string.Empty,
                "Optional MP3, WAV, or OGG filename in BepInEx/config/Y4NGZCompany/MonitorTakeovers, or a full YouTube link, played as the quota takeover's soundtrack. Files need an identical copy on every player; a YouTube link is downloaded and cached by each player in the background. Ignored when SoundtrackMode is Off.");
            _soundtrackMode = config.Bind(MonitorTakeoverConfigSchemaMigration.AudioSection, "SoundtrackMode", TakeoverSoundtrackMode.Off,
                "Off keeps the built-in takeover audio. Replace silences the power-down whine, the alarm, the drone bed, and the mumble voices for the whole takeover and plays only the soundtrack. Mix layers the soundtrack over them.");
            _soundtrackVolume = config.Bind(MonitorTakeoverConfigSchemaMigration.AudioSection, "SoundtrackVolume", 0.8f,
                new ConfigDescription("Soundtrack volume (0 = silent, 1 = full). Independent of Mumble Volume.", new AcceptableValueRange<float>(0f, 1f)));
            _loopMedia = config.Bind(MonitorTakeoverConfigSchemaMigration.MediaSection, "LoopMedia", true,
                "Loop configured MP4 media for the duration of the takeover.");
            _enableMediaAudio = config.Bind(MonitorTakeoverConfigSchemaMigration.MediaSection, "EnableMediaAudio", false,
                "Play the configured MP4 audio track. Disabled by default so it does not compete with takeover speech.");
            _useMediaLengthAsDuration = config.Bind(MonitorTakeoverConfigSchemaMigration.MediaSection, "UseMediaLengthAsDuration", false,
                "Hold the takeover for the configured video's full length instead of Takeover Duration. Applies only when the takeover media is a video (local MP4 or YouTube link); clamped to 30 minutes.");
            // #662. Born in Media, default off, so an untouched profile keeps the single-source
            // takeover exactly as it is. The pool is semicolon-separated because a YouTube link
            // may legally contain a comma, which the other list keys here use as a separator.
            _randomPerMonitorMedia = config.Bind(MonitorTakeoverConfigSchemaMigration.MediaSection, "RandomPerMonitorMedia", false,
                "Play different media on different ship monitors during the quota takeover. Off keeps every monitor on one shared video.");
            _perMonitorMediaPool = config.Bind(MonitorTakeoverConfigSchemaMigration.MediaSection, "PerMonitorMediaPool", string.Empty,
                "Semicolon-separated list of PNG/JPG/MP4 filenames in BepInEx/config/Y4NGZCompany/MonitorTakeovers and/or full YouTube links used when RandomPerMonitorMedia is on. Empty falls back to the quota's MediaFile and DefaultMediaFile. Files need an identical copy on every player; links are cached by each player. Example: clip1.mp4;https://youtu.be/abc;poster.png");
            _maxConcurrentMediaPlayers = config.Bind(MonitorTakeoverConfigSchemaMigration.MediaSection, "MaxConcurrentMediaPlayers", 3,
                new ConfigDescription(
                    "Maximum simultaneous video decoders during a per-monitor takeover. Extra monitors share the players in a fixed rotation.",
                    new AcceptableValueRange<int>(1, 6)));
            _enableUnlockAnnouncement = config.Bind(MonitorTakeoverConfigSchemaMigration.RewardsCeremonySection, "EnableUnlockAnnouncement", true,
                "After the takeover ends, show the quota's new unlocks and grants on the left large ship monitor.");
            _unlockAnnouncementSeconds = config.Bind(MonitorTakeoverConfigSchemaMigration.RewardsCeremonySection, "UnlockAnnouncementSeconds", 4.5f,
                new ConfigDescription("Seconds the unlock announcement stays on the monitor.", new AcceptableValueRange<float>(2f, 15f)));
            _announceOnBothMonitors = config.Bind(MonitorTakeoverConfigSchemaMigration.RewardsCeremonySection, "AnnounceOnBothMonitors", false,
                "Also show the unlock announcement on the right large ship monitor.");
            _rewardsCeremonyEnabled = config.Bind(MonitorTakeoverConfigSchemaMigration.RewardsCeremonySection, "RewardsCeremonyEnabled", true,
                "Present the unlock announcement as a ceremony: each reward line types out with a tick per step and a confirm when it lands, and the display hold starts after typing finishes.");
            _ceremonyMirrorBothMonitors = config.Bind(MonitorTakeoverConfigSchemaMigration.RewardsCeremonySection, "CeremonyMirrorBothMonitors", true,
                "During the rewards ceremony, mirror the announcement onto the right large ship monitor as well.");
            _ceremonyTypewriterSecondsPerChar = config.Bind(MonitorTakeoverConfigSchemaMigration.RewardsCeremonySection, "CeremonyTypewriterSecondsPerChar", 0.03f,
                new ConfigDescription("Seconds each typed character takes during the rewards ceremony.", new AcceptableValueRange<float>(0.005f, 0.2f)));
            _ceremonyLineConfirmSting = config.Bind(MonitorTakeoverConfigSchemaMigration.RewardsCeremonySection, "CeremonyLineConfirmSting", true,
                "Play a confirm sting as each reward line finishes typing.");
            _ceremonyRestoreStatic = config.Bind(MonitorTakeoverConfigSchemaMigration.RewardsCeremonySection, "CeremonyRestoreStatic", true,
                "End the rewards ceremony with a brief CRT static burst before the monitors return to their normal views instead of a hard cut.");

            Directory.CreateDirectory(MediaDirectory);
            Milestones.Clear();
            for (int quota = 1; quota <= 9; quota++)
                Milestones[quota] = new QuotaMilestoneConfig(config, quota);
            // Every peer prefetches its own configured YouTube links at startup so a
            // modpack-shared config has the video cached long before the first
            // takeover; a link only the host knows still prefetches on the payload
            // broadcast at rollover (#661).
            if (_enabled.Value)
            {
                YoutubeVideoResolver.Prefetch(_defaultMediaFile?.Value);
                // A soundtrack link is the same kind of background download and gets the same
                // head start; Prefetch ignores anything that is not a YouTube link (#715).
                YoutubeVideoResolver.Prefetch(_soundtrackSource?.Value);
                foreach (QuotaMilestoneConfig milestone in Milestones.Values)
                    if (milestone.Enable.Value)
                        YoutubeVideoResolver.Prefetch(milestone.MediaFile.Value);
                // #662: every pool link gets the same head start, so a per-monitor takeover is
                // not the first thing that discovers a link has never been downloaded.
                foreach (string entry in PerMonitorMediaPool())
                    YoutubeVideoResolver.Prefetch(entry);
                WarnOnCommaSeparatedMediaPool();
            }
            WriteEffectiveAudit();
            EnsureModuleWiring();
        }

        // -- Y4NGZCore module wiring (#611 task 1.3) --------------------------

        private static ModuleGate _moduleGate;
        private static ModulePresentationPolicy _modulePresentationPolicy;
        private static bool _rolloverSubscribed;

        /// <summary>
        /// Publishes this module's neutral surfaces and subscribes to the one host-authoritative
        /// rollover event. Idempotent, and safe to call before any lobby exists.
        /// </summary>
        internal static void EnsureModuleWiring()
        {
            if (_moduleGate == null)
            {
                _moduleGate = new ModuleGate();
                _modulePresentationPolicy = new ModulePresentationPolicy();
                ModuleCapabilityRegistry.Register(ModuleCapabilities.QuotaUnlocks, _moduleGate);
                ModuleCapabilityRegistry.Register(
                    ModuleCapabilities.QuotaPresentationPolicy, _modulePresentationPolicy);
            }

            if (_rolloverSubscribed)
                return;
            _rolloverSubscribed = true;

            // Milestone evaluation is a subscriber now, not a patch-site call. The publisher
            // guarantees one delivery per rollover after the game has applied it, so this runs
            // exactly once and reads a payload that cannot race timesFulfilledQuota.
            QuotaRolloverNotifications.QuotaRolloverFinalized += OnQuotaRolloverFinalized;

            // A provider appearing is the other way a deferred reward gets paid. The periodic
            // retry pass would find it within five seconds anyway; this makes turning the fuel
            // system on feel immediate rather than eventual, and costs one ledger read.
            QuotaRewardBroker.RewardProviderAvailable += OnRewardProviderAvailable;
        }

        /// <summary>
        /// Milestone evaluation for one rollover. Host-only, once per rollover.
        ///
        /// <para>Reads <see cref="QuotaRolloverFinalized.CompletedQuotaCount"/> from the payload
        /// rather than <c>TimeOfDay.timesFulfilledQuota</c>: the publisher captured it after the
        /// game applied the rollover, and a subscriber re-reading live state is how the same
        /// milestone used to be evaluated twice.</para>
        /// </summary>
        private static void OnQuotaRolloverFinalized(QuotaRolloverFinalized rollover)
        {
            if (_enabled?.Value != true || NetworkManager.Singleton?.IsServer != true) return;

            int quota = Math.Max(0, rollover.CompletedQuotaCount);
            _currentPayload = BuildPayload(quota);
            _hasCurrentPayload = true;
            SendPayload(_currentPayload);
            SendSoundtrack(BuildSoundtrackPlan());
            SendMediaPlan(BuildMediaPlan(_currentPayload));
            ApplyRewardsThroughQuota(quota);
            QuotaProgressionSuitGate.OnUnlockStateMaybeChanged(quota);
        }

        private static void OnRewardProviderAvailable(string rewardKind)
        {
            if (_enabled?.Value != true || NetworkManager.Singleton?.IsServer != true) return;
            int completedQuota = TimeOfDay.Instance != null ? Math.Max(0, TimeOfDay.Instance.timesFulfilledQuota) : 0;
            if (completedQuota < 1) return;
            ApplyRewardsThroughQuota(completedQuota);
        }

        /// <summary>
        /// This module's <see cref="IQuotaUnlockGate"/>. Every member forwards to the same
        /// per-category logic the typed methods used, so the gate and the direct calls can never
        /// disagree.
        /// </summary>
        private sealed class ModuleGate : IQuotaUnlockGate
        {
            public bool IsFeatureUnlocked(string category, string stableId, int completedQuotas)
            {
                switch (category)
                {
                    case QuotaUnlockCategories.Assignment:
                        return IsContractUnlocked(stableId, completedQuotas);
                    case QuotaUnlockCategories.ShipUpgrade:
                        return IsShipUpgradePurchaseUnlocked(stableId, completedQuotas);
                    case QuotaUnlockCategories.Constellation:
                        return IsConstellationUnlocked(stableId, completedQuotas);
                    case QuotaUnlockCategories.Suit:
                        return IsSuitUnlocked(stableId);
                    case QuotaUnlockCategories.StoreItem:
                        return IsStoreItemUnlocked(stableId);
                    // An unknown category gates nothing, matching the contract's rule for an
                    // unknown ID: the configuration lists what is held back, and silence means
                    // "not gated".
                    default:
                        return true;
                }
            }

            public bool TryGetUnlockQuota(string category, string stableId, out int quota)
            {
                if (category == QuotaUnlockCategories.Constellation)
                    return TryGetConstellationUnlockQuota(stableId, out quota);

                quota = 0;
                return false;
            }
        }

        /// <summary>
        /// The one presentation-policy read the ship monitor row makes of this module.
        /// </summary>
        private sealed class ModulePresentationPolicy : IQuotaPresentationPolicy
        {
            public bool RewardsCeremonyEnabled => QuotaProgressionRegistry.RewardsCeremonyEnabled;
        }

        internal static void Tick()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || nm.CustomMessagingManager == null) return;
            string saveName = CurrentSaveName();
            if (!string.Equals(_activeSaveName, saveName, StringComparison.Ordinal))
            {
                _activeSaveName = saveName;
                _hasCurrentPayload = false;
                _soundtrackPlan = default;
                _mediaPlan = default;
                _hasAuthoritativeUpgradeSnapshot = false;
                AuthoritativeUnlockedUpgrades.Clear();
                AuthoritativeConstellationUnlockQuotas.Clear();
                AuthoritativeSuitUnlockQuotas.Clear();
                AuthoritativeStoreItemUnlockQuotas.Clear();
                _authoritativeQuota = 0;
                _cachedLedger = null;
                _cachedLedgerSave = string.Empty;
                _tokenReplayAttemptsRemaining = 0;

                // #611 task 1.3: this is the one place that already knows a run's identity
                // changed, so it is where the rollover publisher's single-publish latch is
                // cleared. Without it, save B's quota 1 is suppressed as a duplicate of save
                // A's quota 9 -- the publisher latches the highest count it has seen and has
                // no idea a different save is now loaded. It also discards any objective
                // outcome staged by the previous run.
                QuotaRolloverNotifications.ResetRunState();
            }
            CustomMessagingManager messaging = nm.CustomMessagingManager;
            if (!ReferenceEquals(messaging, _registeredMessaging))
            {
                if (_registeredMessaging != null)
                {
                    try { _registeredMessaging.UnregisterNamedMessageHandler(PayloadMessage); } catch { }
                    try { _registeredMessaging.UnregisterNamedMessageHandler(GateSnapshotMessage); } catch { }
                    try { _registeredMessaging.UnregisterNamedMessageHandler(SoundtrackMessage); } catch { }
                    try { _registeredMessaging.UnregisterNamedMessageHandler(MediaPlanMessage); } catch { }
                }
                if (_registeredNetworkManager != null && _clientConnectedHandler != null)
                {
                    try { _registeredNetworkManager.OnClientConnectedCallback -= _clientConnectedHandler; } catch { }
                }
                messaging.RegisterNamedMessageHandler(PayloadMessage, OnPayloadMessage);
                messaging.RegisterNamedMessageHandler(GateSnapshotMessage, OnGateSnapshotMessage);
                messaging.RegisterNamedMessageHandler(SoundtrackMessage, OnSoundtrackMessage);
                messaging.RegisterNamedMessageHandler(MediaPlanMessage, OnMediaPlanMessage);
                // A new lobby has published nothing yet, and a stale plan from the previous one
                // would silence this lobby's bed under Replace before any takeover ran.
                _soundtrackPlan = default;
                // #662: and would hand this lobby a pool the new host may not even have.
                _mediaPlan = default;
                _clientConnectedHandler = OnClientConnected;
                try { nm.OnClientConnectedCallback += _clientConnectedHandler; }
                catch (Exception ex) { _log?.LogWarning($"Quota progression client-connect hook failed: {ex.Message}"); }
                _registeredMessaging = messaging;
                _registeredNetworkManager = nm;
                _nextSnapshotAt = 0f;
                _tokenReplayAttemptsRemaining = 0;
                if (!nm.IsServer) _hasAuthoritativeUpgradeSnapshot = false;
            }

            if (nm.IsServer && Time.realtimeSinceStartup >= _nextSnapshotAt)
            {
                _nextSnapshotAt = Time.realtimeSinceStartup + 5f;
                SendGateSnapshot();
                // A deferred reward is owed, not optional, so it retries regardless of the
                // optional-reward retry config; that config still governs everything else.
                if (TimeOfDay.Instance != null && TimeOfDay.Instance.timesFulfilledQuota > 0 &&
                    QuotaRewardLedger.ShouldRetryRewards(_retryPending?.Value == true, LoadLedger()))
                    ApplyRewardsThroughQuota(TimeOfDay.Instance.timesFulfilledQuota);
            }

            if (nm.IsServer && _tokenReplayAttemptsRemaining > 0 && Time.realtimeSinceStartup >= _nextTokenReplayAt)
            {
                _tokenReplayAttemptsRemaining--;
                _nextTokenReplayAt = Time.realtimeSinceStartup + TokenReplayIntervalSeconds;
                ReplayLedgeredTokenGrants();
            }
        }

        /// <summary>
        /// Host-side late-joiner hook. A milestone that already landed in the ledger will never be
        /// re-offered by <see cref="ApplyRewardsThroughQuota"/>, so a player who joins after that
        /// milestone completed would never see its token grant. Schedule a replay pass; the provider
        /// is responsible for de-duplicating per player save.
        /// </summary>
        private static void OnClientConnected(ulong clientId)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (clientId == nm.LocalClientId) return;
            if (_enabled?.Value != true) return;

            _tokenReplayAttemptsRemaining = TokenReplayAttempts;
            _nextTokenReplayAt = Time.realtimeSinceStartup + TokenReplayFirstDelaySeconds;
        }

        private static void ReplayLedgeredTokenGrants()
        {
            if (_enabled?.Value != true || NetworkManager.Singleton?.IsServer != true) return;
            if (!QuotaProgressionApi.IsTokenGrantCapabilityAvailable) return;

            int completedQuota = TimeOfDay.Instance != null ? Math.Max(0, TimeOfDay.Instance.timesFulfilledQuota) : 0;
            if (completedQuota < 1) return;

            HashSet<string> ledger = LoadLedger();
            int replayed = 0;
            foreach (QuotaMilestoneConfig milestone in Milestones.Values
                .Where(m => m.Enable.Value && m.Quota <= completedQuota && m.GrantTokensPerPlayer.Value > 0)
                .OrderBy(m => m.Quota))
            {
                string tokenId = QuotaRewardLedger.RewardId(milestone.Quota, "tokens");
                // Only rewards the ledger records as paid are replayed; a deferred
                // reward is still owed and is handled by the normal reward pass.
                if (!QuotaRewardLedger.IsPaid(ledger, tokenId)) continue;
                if (QuotaProgressionApi.TryGrantTokens(
                        new QuotaTokenGrantRequest(milestone.Quota, milestone.GrantTokensPerPlayer.Value, tokenId, true)))
                    replayed++;
            }

            if (replayed > 0)
                _log?.LogInfo($"Replayed {replayed} ledgered quota token grant(s) for connected players.");
        }

        /// <summary>
        /// Debug entry point that forces milestone evaluation for the current quota.
        ///
        /// <para>The <em>real</em> path is <see cref="OnQuotaRolloverFinalized"/>, driven by the
        /// single host-authoritative rollover event. This remains only for the debug controller,
        /// which asks for a milestone to be replayed without a rollover having happened; it
        /// reads live game state precisely because there is no payload to read.</para>
        /// </summary>
        internal static void OnQuotaCompleted()
        {
            if (_enabled?.Value != true || NetworkManager.Singleton?.IsServer != true) return;
            int quota = TimeOfDay.Instance != null ? Math.Max(0, TimeOfDay.Instance.timesFulfilledQuota) : 0;
            _currentPayload = BuildPayload(quota);
            _hasCurrentPayload = true;
            SendPayload(_currentPayload);
            SendSoundtrack(BuildSoundtrackPlan());
            SendMediaPlan(BuildMediaPlan(_currentPayload));
            ApplyRewardsThroughQuota(quota);
            QuotaProgressionSuitGate.OnUnlockStateMaybeChanged(quota);
        }

        internal static QuotaTakeoverPayload GetCurrentPayload(string[] builtInDialogue)
        {
            int quota = TimeOfDay.Instance != null ? Math.Max(0, TimeOfDay.Instance.timesFulfilledQuota) : 0;
            QuotaTakeoverPayload payload = _hasCurrentPayload && _currentPayload.Quota == quota ? _currentPayload : BuildPayload(quota);
            if (string.IsNullOrWhiteSpace(payload.Dialogue) && builtInDialogue != null && builtInDialogue.Length > 0)
            {
                int index = RandomizeDialogue
                    ? GetRandomizedIndex(builtInDialogue.Length, quota, "built-in")
                    : Math.Max(0, quota - 1) % builtInDialogue.Length;
                payload = payload.WithDialogue(builtInDialogue[index], index);
            }
            return payload;
        }

        internal static void PrepareHostTakeoverPayload(string[] builtInDialogue)
        {
            if (NetworkManager.Singleton?.IsServer != true) return;
            QuotaTakeoverPayload payload = GetCurrentPayload(builtInDialogue);
            _currentPayload = payload;
            _hasCurrentPayload = true;
            SendPayload(payload);
            // Re-sent at the head of every BeginTakeover, which is what covers a late joiner:
            // the sidecar has no snapshot of its own.
            SendSoundtrack(BuildSoundtrackPlan());
            // #662: the seed is rolled here too, so the layout is fixed for the takeover that is
            // about to start rather than carried over from the rollover that armed it.
            SendMediaPlan(BuildMediaPlan(payload));
        }

        // -- Neutral unlock gates (#611 task 1.3) -----------------------------
        // These used to be typed on MoonContractType and ShipUpgradeId, which gave the quota
        // milestone owner -- this file -- a compile-time dependency on both the Contracted and
        // the Ship Systems feature assemblies. They are keyed on (category, stable string) now.
        // The strings are the enum member names, which is already exactly what the milestone
        // config file contains, so no configuration and no meaning changed.

        internal static bool IsContractUnlocked(string contractId, int completedQuotas)
        {
            return IsNamedUnlockSatisfied(contractId, completedQuotas, milestone => milestone.Contracts);
        }

        internal static bool IsShipUpgradePurchaseUnlocked(string upgradeId, int completedQuotas)
        {
            if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer && _hasAuthoritativeUpgradeSnapshot)
                return AuthoritativeUnlockedUpgrades.Contains(Normalize(upgradeId));
            return IsNamedUnlockSatisfied(upgradeId, completedQuotas, milestone => milestone.Upgrades);
        }

        internal static bool IsConstellationUnlocked(string name, int completedQuotas)
        {
            return IsNamedUnlockSatisfied(name, completedQuotas, milestone => milestone.Constellations);
        }

        internal static bool TryGetConstellationUnlockQuota(string name, out int quota)
        {
            quota = 0;
            if (_enabled?.Value != true || string.IsNullOrWhiteSpace(name)) return false;
            if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer && _hasAuthoritativeUpgradeSnapshot)
                return AuthoritativeConstellationUnlockQuotas.TryGetValue(Normalize(name), out quota);
            foreach (QuotaMilestoneConfig milestone in Milestones.Values.Where(m => m.Enable.Value).OrderBy(m => m.Quota))
            {
                if (!milestone.Constellations.Any(item => Normalize(item) == Normalize(name))) continue;
                quota = milestone.Quota;
                return true;
            }
            return false;
        }

        internal static bool IsSuitUnlocked(string name)
        {
            return IsRuleUnlocked(name, AuthoritativeSuitUnlockQuotas, milestone => milestone.Suits);
        }

        internal static bool IsStoreItemUnlocked(string name)
        {
            return IsRuleUnlocked(name, AuthoritativeStoreItemUnlockQuotas, milestone => milestone.StoreItems);
        }

        private static bool IsRuleUnlocked(string name, Dictionary<string, int> authoritative, Func<QuotaMilestoneConfig, IEnumerable<string>> selector)
        {
            if (_enabled?.Value != true || string.IsNullOrWhiteSpace(name)) return true;
            if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer && _hasAuthoritativeUpgradeSnapshot)
                return !authoritative.TryGetValue(Normalize(name), out int required) || _authoritativeQuota >= required;
            int completedQuotas = TimeOfDay.Instance != null ? Math.Max(0, TimeOfDay.Instance.timesFulfilledQuota) : 0;
            return IsNamedUnlockSatisfied(name, completedQuotas, selector);
        }

        internal sealed class QuotaMilestoneSummary
        {
            internal int Quota;
            internal bool Enabled;
            internal IReadOnlyList<string> Contracts;
            internal IReadOnlyList<string> ShipUpgrades;
            internal IReadOnlyList<string> Constellations;
            internal IReadOnlyList<string> Suits;
            internal IReadOnlyList<string> StoreItems;
            internal int TokensPerPlayer;
            internal int GroupCredits;
            internal float ShipFuel;
        }

        internal static QuotaMilestoneSummary GetMilestoneSummary(int quota)
        {
            if (!Milestones.TryGetValue(quota, out QuotaMilestoneConfig milestone) || milestone == null) return null;
            return new QuotaMilestoneSummary
            {
                Quota = milestone.Quota,
                Enabled = milestone.Enable.Value,
                Contracts = milestone.Contracts.ToArray(),
                ShipUpgrades = milestone.Upgrades.ToArray(),
                Constellations = milestone.Constellations.ToArray(),
                Suits = milestone.Suits.ToArray(),
                StoreItems = milestone.StoreItems.ToArray(),
                TokensPerPlayer = milestone.GrantTokensPerPlayer.Value,
                GroupCredits = milestone.GrantGroupCredits.Value,
                ShipFuel = milestone.GrantShipFuel.Value
            };
        }

        private static bool IsNamedUnlockSatisfied(string name, int completedQuotas, Func<QuotaMilestoneConfig, IEnumerable<string>> selector)
        {
            if (_enabled?.Value != true || string.IsNullOrWhiteSpace(name)) return true;
            int? required = null;
            foreach (QuotaMilestoneConfig milestone in Milestones.Values.Where(m => m.Enable.Value).OrderBy(m => m.Quota))
            {
                if (selector(milestone).Any(item => Normalize(item) == Normalize(name)))
                {
                    required = milestone.Quota;
                    break;
                }
            }
            return !required.HasValue || completedQuotas >= required.Value;
        }

        private static QuotaTakeoverPayload BuildPayload(int quota)
        {
            QuotaMilestoneConfig selected = Milestones.TryGetValue(quota, out QuotaMilestoneConfig exact) && exact.Enable.Value ? exact : null;
            List<QuotaMilestoneConfig> dialoguePool = Milestones.Values
                .Where(item => item.Enable.Value && !string.IsNullOrWhiteSpace(ReadDialogue(item)))
                .OrderBy(item => item.Quota)
                .ToList();
            if (RandomizeDialogue && dialoguePool.Count > 0)
            {
                int index = GetRandomizedIndex(dialoguePool.Count, quota, "configured");
                selected = dialoguePool[index];
            }
            string dialogue = selected != null ? ReadDialogue(selected) : string.Empty;
            string media = Milestones.TryGetValue(quota, out QuotaMilestoneConfig mediaMilestone) && mediaMilestone.Enable.Value
                ? mediaMilestone.MediaFile.Value
                : string.Empty;
            if (string.IsNullOrWhiteSpace(media)) media = _defaultMediaFile?.Value ?? string.Empty;
            // A YouTube link is resolved by every peer independently, and yt-dlp/FFmpeg
            // output is not byte-stable across machines, so it travels without a hash;
            // each client trusts only its own download of the same normalized link (#661).
            YoutubeVideoResolver.Prefetch(media);
            string mediaHash = !YoutubeVideoResolver.IsYoutubeUrl(media)
                && TryResolveMediaFile(media, out string mediaPath) ? ComputeSha256(mediaPath) : string.Empty;
            BuildAudioOverrideFields(
                out string alarmAudio, out string alarmAudioHash,
                out string mumbleAudio, out string mumbleAudioHashes);
            return new QuotaTakeoverPayload(quota, dialogue, media, mediaHash, selected?.Quota ?? 0,
                alarmAudio, alarmAudioHash, mumbleAudio, mumbleAudioHashes);
        }

        // Host-side only: an override is published solely when this machine can
        // resolve and hash the file, so a client never receives a name with an
        // empty hash it would have to trust blindly.
        private static void BuildAudioOverrideFields(
            out string alarmAudio, out string alarmAudioHash,
            out string mumbleAudio, out string mumbleAudioHashes)
        {
            alarmAudio = string.Empty;
            alarmAudioHash = string.Empty;
            string configuredAlarm = TruncateFixed((_alarmAudioFile?.Value ?? string.Empty).Trim(), MaxOverrideFileNameChars);
            if (configuredAlarm.Length > 0 && TryResolveAudioFile(configuredAlarm, out string alarmPath))
            {
                string hash = ComputeSha256(alarmPath);
                if (hash.Length > 0) { alarmAudio = configuredAlarm; alarmAudioHash = hash; }
            }

            var names = new List<string>();
            var hashes = new List<string>();
            int nameChars = 0;
            int hashChars = 0;
            foreach (string entry in SplitList(_mumbleAudioFiles?.Value))
            {
                if (names.Count >= MaxMumbleOverrideClips) break;
                string name = TruncateFixed(entry, MaxOverrideFileNameChars);
                if (!TryResolveAudioFile(name, out string clipPath)) continue;
                string hash = ComputeSha256(clipPath);
                if (hash.Length == 0) continue;
                int nextNameChars = nameChars + name.Length + (names.Count > 0 ? 1 : 0);
                int nextHashChars = hashChars + hash.Length + (hashes.Count > 0 ? 1 : 0);
                if (nextNameChars > MaxMumbleOverrideNameChars || nextHashChars > MaxMumbleOverrideHashChars) break;
                names.Add(name);
                hashes.Add(hash);
                nameChars = nextNameChars;
                hashChars = nextHashChars;
            }
            mumbleAudio = string.Join(",", names);
            mumbleAudioHashes = string.Join(",", hashes);
        }

        private static string ReadDialogue(QuotaMilestoneConfig milestone)
        {
            if (milestone == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(milestone.DialogueFile.Value) && TryResolveFile(milestone.DialogueFile.Value, ".txt", out string path))
            {
                try
                {
                    if (new FileInfo(path).Length > 64 * 1024)
                    {
                        _log?.LogWarning($"Quota {milestone.Quota} dialogue file exceeds 64 KB; using inline/built-in dialogue.");
                    }
                    else return File.ReadAllText(path).Trim();
                }
                catch (Exception ex) { _log?.LogWarning($"Quota {milestone.Quota} dialogue file failed: {ex.Message}"); }
            }
            return (milestone.Dialogue.Value ?? string.Empty).Replace("\\n", "\n").Trim();
        }

        internal static bool TryResolveMediaFile(string configured, out string path)
        {
            if (YoutubeVideoResolver.IsYoutubeUrl(configured))
                return YoutubeVideoResolver.TryGetCachedVideo(configured, out path);
            return TryResolveFile(configured, MediaExtensions, out path);
        }

        internal static bool TryResolveMediaFile(string configured, string expectedHash, out string path)
        {
            // YouTube media is verified by each peer's own download of the same
            // normalized link, never by the host's hash — see BuildPayload (#661).
            if (YoutubeVideoResolver.IsYoutubeUrl(configured))
                return YoutubeVideoResolver.TryGetCachedVideo(configured, out path);
            return TryResolveVerifiedFile(configured, MediaExtensions, expectedHash, "media", out path);
        }

        internal static bool TryResolveAudioFile(string configured, out string path)
        {
            return TryResolveFile(configured, AudioExtensions, out path);
        }

        internal static bool TryResolveAudioFile(string configured, string expectedHash, out string path)
        {
            return TryResolveVerifiedFile(configured, AudioExtensions, expectedHash, "audio", out path);
        }

        private static bool TryResolveVerifiedFile(string configured, string[] extensions, string expectedHash, string kind, out string path)
        {
            if (!TryResolveFile(configured, extensions, out path)) return false;
            if (string.IsNullOrWhiteSpace(expectedHash)) return true;
            if (string.Equals(ComputeSha256(path), expectedHash, StringComparison.OrdinalIgnoreCase)) return true;
            _log?.LogWarning($"Configured takeover {kind} hash mismatch for '{Path.GetFileName(path)}'; using bundled fallback.");
            path = null;
            return false;
        }

        private static bool TryResolveFile(string configured, string extension, out string path) =>
            TryResolveFile(configured, new[] { extension }, out path);

        private static bool TryResolveFile(string configured, string[] extensions, out string path)
        {
            path = null;
            if (string.IsNullOrWhiteSpace(configured)) return false;
            string root = Path.GetFullPath(MediaDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(root, configured.Trim()));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)) return false;
            if (!extensions.Contains(Path.GetExtension(candidate), StringComparer.OrdinalIgnoreCase)) return false;
            long maxBytes = Path.GetExtension(candidate).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                ? 512L * 1024L * 1024L
                : 32L * 1024L * 1024L;
            if (new FileInfo(candidate).Length > maxBytes)
            {
                _log?.LogWarning($"Configured takeover media '{Path.GetFileName(candidate)}' exceeds the size limit.");
                return false;
            }
            path = candidate;
            return true;
        }

        // ── Loose-file takeover audio overrides ───────────────────────────────
        // Same sandbox as the image/video override: names resolve inside the
        // takeover media directory only, and a client accepts a file only when
        // its SHA-256 matches the hash the host published with the payload.

        internal static TakeoverAudioOverrideFile ResolveTakeoverAlarmAudio(QuotaTakeoverPayload payload)
        {
            if (string.IsNullOrWhiteSpace(payload.AlarmAudioFile)) return default;
            if (TryResolveAudioFile(payload.AlarmAudioFile, payload.AlarmAudioHash, out string path))
                return new TakeoverAudioOverrideFile(path, payload.AlarmAudioHash);
            _log?.LogWarning($"Takeover alarm audio override '{payload.AlarmAudioFile}' is missing or does not match the host copy; using the built-in alarm.");
            return default;
        }

        internal static List<TakeoverAudioOverrideFile> ResolveTakeoverMumbleAudio(QuotaTakeoverPayload payload)
        {
            var resolved = new List<TakeoverAudioOverrideFile>();
            string[] names = SplitList(payload.MumbleAudioFiles);
            string[] hashes = SplitList(payload.MumbleAudioHashes);
            if (names.Length == 0) return resolved;
            int rejected = 0;
            for (int index = 0; index < names.Length; index++)
            {
                string hash = index < hashes.Length ? hashes[index] : string.Empty;
                if (TryResolveAudioFile(names[index], hash, out string path))
                    resolved.Add(new TakeoverAudioOverrideFile(path, hash));
                else rejected++;
            }
            if (rejected > 0)
                _log?.LogWarning($"{rejected} of {names.Length} takeover mumble audio overrides are missing or do not match the host copies; those slots use the built-in clips.");
            return resolved;
        }

        private static string[] SplitList(string value) => (value ?? string.Empty)
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToArray();

        /// <summary>
        /// The per-monitor pool separator (#662). Semicolon, not comma: a YouTube link may
        /// legally carry a comma in a query parameter, and splitting on one would cut a link
        /// in half and drop both pieces as unresolvable.
        /// </summary>
        internal static string[] SplitSemicolonList(string value) => (value ?? string.Empty)
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToArray();

        /// <summary>
        /// The hash half of the per-monitor pool, split so that index <c>i</c> still belongs to
        /// pool entry <c>i</c>. Empty entries are kept deliberately: a YouTube entry publishes
        /// no hash, and dropping its empty slot would shift every later hash onto the wrong file.
        /// </summary>
        internal static string[] SplitSemicolonListPreservingEmpty(string value)
        {
            string raw = value ?? string.Empty;
            if (raw.Length == 0) return Array.Empty<string>();
            return raw.Split(';').Select(item => item.Trim()).ToArray();
        }

        /// <summary>
        /// The other list keys in this file are comma-separated, so a comma-separated pool is
        /// the obvious mistake to make. It is not an error — a single filename containing a
        /// comma is legal — so this is one warning, not a rejection.
        /// </summary>
        private static void WarnOnCommaSeparatedMediaPool()
        {
            string configured = _perMonitorMediaPool?.Value ?? string.Empty;
            if (configured.IndexOf(';') >= 0 || configured.IndexOf(',') < 0) return;
            if (SplitList(configured).Length < 2) return;
            _log?.LogWarning(
                "[QuotaProgression] PerMonitorMediaPool looks comma-separated; this key uses ';' so that a "
                + "YouTube link containing a comma stays intact. The whole value is being read as one entry.");
        }

        /// <summary>
        /// The ledger kind for ship fuel.
        ///
        /// <para><b>This is deliberately not the same string as the Y4NGZCore reward kind, and
        /// must not become one.</b> The ledger writes <c>q3:fuel</c> into the save under
        /// <c>Y4NGZCompany.QuotaProgressionLedger.v1</c>, and every existing save, every
        /// deferred marker and the #608 heal marker's fuel detector match that exact literal.
        /// Y4NGZCore names the same reward <c>ship_fuel</c> so a future facility-generator fuel
        /// cannot collide with it. Renaming either to match the other orphans one side; the
        /// mapping lives here, in one place, instead.</para>
        /// </summary>
        private const string FuelLedgerKind = "fuel";

        /// <summary>The ledger kind for group credits -- identical to the Y4NGZCore kind.</summary>
        private const string CreditsLedgerKind = "credits";

        private static void ApplyRewardsThroughQuota(int completedQuota)
        {
            HashSet<string> ledger = LoadLedger();
            // The retry pass runs every few seconds while anything is owed, and its steady
            // state changes nothing, so the ledger is only written when it actually moved.
            bool dirty = false;
            foreach (QuotaMilestoneConfig milestone in Milestones.Values.Where(m => m.Enable.Value && m.Quota <= completedQuota).OrderBy(m => m.Quota))
            {
                // Credits stay here: the purse is a vanilla Terminal field, so paying it needs
                // no feature assembly, and giving it a provider would add a registration that
                // can be missing for no gain.
                dirty |= ApplyReward(ledger, milestone, CreditsLedgerKind, milestone.GrantGroupCredits.Value, true, amount => GrantCredits((int)amount));

                // Ship fuel is Ship Systems'. Offering it through the broker is what keeps the
                // three outcomes distinguishable: a full tank answers NoHeadroom and a
                // switched-off fuel system answers Deferred, and neither consumes the ledger
                // action -- only fuel that actually landed does.
                dirty |= ApplyBrokeredReward(
                    ledger, milestone, FuelLedgerKind, QuotaRewardKinds.ShipFuel, milestone.GrantShipFuel.Value);

                // Tokens stay reflection-based: the provider is the separately shipped
                // Y4NGZUpgrades mod, which talks to QuotaProgressionApi's versioned handshake
                // and knows nothing about Y4NGZCore. Moving it is a companion-repo change.
                dirty |= ApplyReward(ledger, milestone, "tokens", milestone.GrantTokensPerPlayer.Value,
                    QuotaProgressionApi.IsTokenGrantCapabilityAvailable,
                    _ => QuotaProgressionApi.TryGrantTokens(new QuotaTokenGrantRequest(
                        milestone.Quota,
                        milestone.GrantTokensPerPlayer.Value,
                        QuotaRewardLedger.RewardId(milestone.Quota, "tokens"))));
            }
            if (dirty) SaveLedger(ledger);
        }

        /// <summary>
        /// Offers one milestone reward through <see cref="QuotaRewardBroker"/> and applies the
        /// #608 ledger rule to the result. Returns whether the ledger changed.
        ///
        /// <para>The ledger action ID keeps the pre-split <paramref name="ledgerKind"/>; only
        /// the offer uses <paramref name="coreRewardKind"/>. That is what makes this change
        /// invisible to a save written by an older build.</para>
        ///
        /// <para>The bridge from an offer result to the ledger is
        /// <see cref="Y4NGZCore.Modules.Quota.QuotaRewardOutcome.Consumes"/> and nothing else: <c>Granted</c> is paid,
        /// <c>NoHeadroom</c> and <c>Deferred</c> are both still owed. Comparing the enum at
        /// this call site instead is the exact bug the contract exists to prevent.</para>
        /// </summary>
        private static bool ApplyBrokeredReward(
            HashSet<string> ledger,
            QuotaMilestoneConfig milestone,
            string ledgerKind,
            string coreRewardKind,
            float amount)
        {
            string rewardId = QuotaRewardLedger.RewardId(milestone.Quota, ledgerKind);
            bool wasDeferred = QuotaRewardLedger.IsDeferred(ledger, rewardId);
            QuotaRewardResult observed = QuotaRewardResult.Deferred;

            QuotaRewardOutcome outcome = QuotaRewardLedger.Apply(
                ledger,
                rewardId,
                amount,
                // The broker already answers Deferred for an absent provider, so there is no
                // separate availability probe here to drift out of step with the offer.
                providerAvailable: true,
                grant: offered =>
                {
                    observed = QuotaRewardBroker.Offer(new QuotaRewardGrant(
                        coreRewardKind, rewardId, milestone.Quota, offered, wasDeferred));
                    return Y4NGZCore.Modules.Quota.QuotaRewardOutcome.Consumes(observed);
                },
                ledgerChanged: out bool ledgerChanged);

            if (outcome == QuotaRewardOutcome.Deferred && !wasDeferred)
            {
                _log?.LogInfo(
                    $"Quota {milestone.Quota} {ledgerKind} reward is owed but nothing could be " +
                    $"granted yet ({observed}); it stays unpaid and retries.");
            }
            else if (outcome == QuotaRewardOutcome.Granted && wasDeferred)
            {
                _log?.LogInfo($"Deferred quota {milestone.Quota} {ledgerKind} reward granted.");
            }

            return ledgerChanged;
        }

        /// <summary>Returns whether the ledger changed and therefore needs saving.</summary>
        private static bool ApplyReward(
            HashSet<string> ledger,
            QuotaMilestoneConfig milestone,
            string kind,
            float amount,
            bool providerAvailable,
            Func<float, bool> apply)
        {
            string id = QuotaRewardLedger.RewardId(milestone.Quota, kind);
            bool wasDeferred = QuotaRewardLedger.IsDeferred(ledger, id);
            QuotaRewardOutcome outcome = QuotaRewardLedger.Apply(ledger, id, amount, providerAvailable, apply, out bool ledgerChanged);
            // Only the state transitions are logged; this pass repeats every few seconds
            // while a reward stays owed.
            if (outcome == QuotaRewardOutcome.Deferred && !wasDeferred)
                _log?.LogInfo($"Quota {milestone.Quota} {kind} reward is owed but nothing could be granted yet; it stays unpaid and retries.");
            else if (outcome == QuotaRewardOutcome.Granted && wasDeferred)
                _log?.LogInfo($"Deferred quota {milestone.Quota} {kind} reward granted.");
            return ledgerChanged;
        }

        private static bool GrantCredits(int amount)
        {
            Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
            if (terminal == null) return false;
            terminal.groupCredits = Math.Max(0, terminal.groupCredits + amount);
            terminal.SyncGroupCreditsServerRpc(terminal.groupCredits, terminal.numberOfItemsInDropship);
            return true;
        }

        private static HashSet<string> LoadLedger()
        {
            string saveName = CurrentSaveName();
            if (_cachedLedger != null && string.Equals(_cachedLedgerSave, saveName, StringComparison.Ordinal))
                return _cachedLedger;
            bool loadFailed = false;
            try
            {
                string raw = ES3.Load<string>(LedgerSaveKey, saveName, string.Empty);
                _cachedLedger = new HashSet<string>((raw ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                loadFailed = true;
                _cachedLedger = new HashSet<string>(StringComparer.Ordinal);
                _log?.LogWarning($"Quota progression ledger load failed: {ex.Message}");
            }
            _cachedLedgerSave = saveName;
            // Never heal a ledger that failed to load: the empty set is a read failure, not a
            // history, and writing the heal marker over it would destroy the real one.
            if (!loadFailed && QuotaRewardLedger.HealFalsePaidFuelRewards(_cachedLedger))
            {
                _log?.LogInfo("Quota progression ledger healed: fuel milestones recorded as paid before the grant predicate was fixed are owed again.");
                SaveLedger(_cachedLedger);
            }
            return _cachedLedger;
        }

        private static void SaveLedger(HashSet<string> ledger)
        {
            _cachedLedger = ledger;
            _cachedLedgerSave = CurrentSaveName();
            try { ES3.Save(LedgerSaveKey, string.Join(";", ledger.OrderBy(item => item, StringComparer.Ordinal)), _cachedLedgerSave); }
            catch (Exception ex) { _log?.LogWarning($"Quota progression ledger save failed: {ex.Message}"); }
        }

        private static void SendPayload(QuotaTakeoverPayload payload)
        {
            CustomMessagingManager manager = NetworkManager.Singleton?.CustomMessagingManager;
            if (manager == null) return;
            using (var writer = new FastBufferWriter(20480, Allocator.Temp))
            {
                writer.WriteValueSafe(payload.Quota);
                writer.WriteValueSafe(payload.SelectionId);
                FixedString4096Bytes dialogue = TruncateFixed(payload.Dialogue, 1000);
                FixedString512Bytes media = TruncateFixed(payload.MediaFile,
                    YoutubeVideoResolver.IsYoutubeUrl(payload.MediaFile) ? MaxYoutubeUrlChars : MaxOverrideFileNameChars);
                FixedString128Bytes mediaHash = payload.MediaHash;
                FixedString512Bytes alarmAudio = TruncateFixed(payload.AlarmAudioFile, MaxOverrideFileNameChars);
                FixedString128Bytes alarmAudioHash = payload.AlarmAudioHash;
                FixedString4096Bytes mumbleAudio = TruncateFixed(payload.MumbleAudioFiles, MaxMumbleOverrideNameChars);
                FixedString4096Bytes mumbleAudioHashes = TruncateFixed(payload.MumbleAudioHashes, MaxMumbleOverrideHashChars);
                writer.WriteValueSafe(dialogue);
                writer.WriteValueSafe(media);
                writer.WriteValueSafe(mediaHash);
                writer.WriteValueSafe(alarmAudio);
                writer.WriteValueSafe(alarmAudioHash);
                writer.WriteValueSafe(mumbleAudio);
                writer.WriteValueSafe(mumbleAudioHashes);
                manager.SendNamedMessageToAll(PayloadMessage, writer);
            }
        }

        private static void OnPayloadMessage(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId) return;
            reader.ReadValueSafe(out int quota);
            reader.ReadValueSafe(out int selectionId);
            reader.ReadValueSafe(out FixedString4096Bytes dialogue);
            reader.ReadValueSafe(out FixedString512Bytes media);
            reader.ReadValueSafe(out FixedString128Bytes mediaHash);
            reader.ReadValueSafe(out FixedString512Bytes alarmAudio);
            reader.ReadValueSafe(out FixedString128Bytes alarmAudioHash);
            reader.ReadValueSafe(out FixedString4096Bytes mumbleAudio);
            reader.ReadValueSafe(out FixedString4096Bytes mumbleAudioHashes);
            _currentPayload = new QuotaTakeoverPayload(quota, dialogue.ToString(), media.ToString(), mediaHash.ToString(), selectionId,
                alarmAudio.ToString(), alarmAudioHash.ToString(), mumbleAudio.ToString(), mumbleAudioHashes.ToString());
            _hasCurrentPayload = true;
            // Kick off the background download now so a first-time YouTube link has
            // the whole quota cycle to cache before its takeover plays (#661).
            YoutubeVideoResolver.Prefetch(_currentPayload.MediaFile);
        }

        // ── Takeover soundtrack sidecar (#715) ────────────────────────────────

        /// <summary>
        /// The soundtrack plan the current peer should use. Off until the host publishes one,
        /// which is also what an old host that never sends the sidecar leaves in place.
        /// </summary>
        internal static TakeoverSoundtrackPlanWire GetSoundtrackPlan() => _soundtrackPlan;

        /// <summary>
        /// Host-side. A local file is published only when this machine can resolve and hash it,
        /// exactly as <see cref="BuildAudioOverrideFields"/> does for the alarm; a YouTube link
        /// is published in normalized form with no hash, because each peer trusts only its own
        /// download of the same link.
        /// </summary>
        private static TakeoverSoundtrackPlanWire BuildSoundtrackPlan()
        {
            TakeoverSoundtrackMode mode = SoundtrackMode;
            string configured = (_soundtrackSource?.Value ?? string.Empty).Trim();
            if (mode == TakeoverSoundtrackMode.Off || configured.Length == 0)
                return default;

            float volume = SoundtrackVolume;
            if (YoutubeVideoResolver.IsYoutubeUrl(configured))
            {
                YoutubeVideoResolver.Prefetch(configured);
                string normalized = YoutubeVideoResolver.TryNormalizeYoutubeUrl(configured, out string url)
                    ? url
                    : configured;
                return new TakeoverSoundtrackPlanWire(
                    mode, TruncateFixed(normalized, MaxYoutubeUrlChars), string.Empty, volume);
            }

            string name = TruncateFixed(configured, MaxOverrideFileNameChars);
            if (TryResolveAudioFile(name, out string path))
            {
                string hash = ComputeSha256(path);
                if (hash.Length > 0)
                    return new TakeoverSoundtrackPlanWire(mode, name, hash, volume);
            }

            _log?.LogWarning(
                $"[QuotaProgression] Takeover soundtrack '{name}' is missing or unreadable on the host; the takeover uses the built-in audio.");
            return default;
        }

        private static void SendSoundtrack(TakeoverSoundtrackPlanWire plan)
        {
            // The host plays from the same struct it publishes, so a host-only failure is
            // reflected on the host's own screen rather than only on the clients'.
            _soundtrackPlan = plan;
            CustomMessagingManager manager = NetworkManager.Singleton?.CustomMessagingManager;
            if (manager == null) return;
            using (var writer = new FastBufferWriter(2048, Allocator.Temp))
            {
                writer.WriteValueSafe((int)plan.Mode);
                FixedString512Bytes source = TruncateFixed(plan.Source,
                    YoutubeVideoResolver.IsYoutubeUrl(plan.Source) ? MaxYoutubeUrlChars : MaxOverrideFileNameChars);
                FixedString128Bytes hash = plan.Hash;
                writer.WriteValueSafe(source);
                writer.WriteValueSafe(hash);
                writer.WriteValueSafe(plan.Volume);
                manager.SendNamedMessageToAll(SoundtrackMessage, writer);
            }
        }

        private static void OnSoundtrackMessage(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId) return;
            reader.ReadValueSafe(out int mode);
            reader.ReadValueSafe(out FixedString512Bytes source);
            reader.ReadValueSafe(out FixedString128Bytes hash);
            reader.ReadValueSafe(out float volume);
            // An unknown mode from a newer host degrades to Off rather than guessing.
            TakeoverSoundtrackMode resolved = mode == (int)TakeoverSoundtrackMode.Replace
                ? TakeoverSoundtrackMode.Replace
                : mode == (int)TakeoverSoundtrackMode.Mix
                    ? TakeoverSoundtrackMode.Mix
                    : TakeoverSoundtrackMode.Off;
            _soundtrackPlan = new TakeoverSoundtrackPlanWire(
                resolved, source.ToString(), hash.ToString(), Mathf.Clamp01(volume));
            // Same reason the media link prefetches here: the whole quota cycle is headroom
            // for a first-time download.
            YoutubeVideoResolver.Prefetch(_soundtrackPlan.Source);
        }

        // ── Per-monitor media plan sidecar (#662) ─────────────────────────────

        /// <summary>
        /// The per-monitor media plan the current peer should use. An empty pool until the host
        /// publishes one, which is also what an old host that never sends the sidecar leaves in
        /// place — and what <c>RandomPerMonitorMedia = false</c> publishes.
        /// </summary>
        internal static TakeoverMediaPlanWire GetMediaPlan() => _mediaPlan;

        /// <summary>
        /// Host-side. The pool is published in configured order — the shuffle is the seed's job,
        /// and doing it here would make the wire order the assignment and lose reproducibility.
        /// Only entries this machine can resolve are published, exactly as
        /// <see cref="BuildAudioOverrideFields"/> does: a client is never handed a name the host
        /// itself could not find. A local file travels with its SHA-256; a link travels
        /// normalized and unhashed, because each peer trusts only its own download.
        /// </summary>
        private static TakeoverMediaPlanWire BuildMediaPlan(QuotaTakeoverPayload payload)
        {
            if (!RandomPerMonitorMedia) return default;

            int seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            int maxPlayers = MaxConcurrentMediaPlayers;
            var names = new List<string>();
            var hashes = new List<string>();
            int nameChars = 0;

            foreach (string entry in PerMonitorMediaPool())
            {
                if (names.Count >= MaxPerMonitorPoolEntries) break;
                bool isLink = YoutubeVideoResolver.IsYoutubeUrl(entry);
                string published;
                string hash;
                if (isLink)
                {
                    YoutubeVideoResolver.Prefetch(entry);
                    published = TruncateFixed(
                        YoutubeVideoResolver.TryNormalizeYoutubeUrl(entry, out string url) ? url : entry,
                        MaxYoutubeUrlChars);
                    hash = string.Empty;
                }
                else
                {
                    published = TruncateFixed(entry, MaxOverrideFileNameChars);
                    if (!TryResolveMediaFile(published, out string path)) continue;
                    hash = ComputeSha256(path);
                    if (hash.Length == 0) continue;
                }
                int nextNameChars = nameChars + published.Length + (names.Count > 0 ? 1 : 0);
                if (nextNameChars > MaxPerMonitorPoolChars) break;
                names.Add(published);
                hashes.Add(hash);
                nameChars = nextNameChars;
            }

            // An empty (or entirely unresolvable) pool still publishes a plan, carrying the one
            // source this takeover would have used anyway. That keeps "the toggle is on" and
            // "the pool is configured" separate questions, and the peer-side resolve turns a
            // one-entry pool into the ordinary single-source takeover on its own.
            if (names.Count == 0)
            {
                string single = payload.MediaFile ?? string.Empty;
                if (single.Length == 0) return new TakeoverMediaPlanWire(seed, maxPlayers, null, null);
                names.Add(TruncateFixed(single,
                    YoutubeVideoResolver.IsYoutubeUrl(single) ? MaxYoutubeUrlChars : MaxOverrideFileNameChars));
                hashes.Add(payload.MediaHash ?? string.Empty);
            }

            return new TakeoverMediaPlanWire(seed, maxPlayers, names.ToArray(), hashes.ToArray());
        }

        private static void SendMediaPlan(TakeoverMediaPlanWire plan)
        {
            // The host runs from the same struct it publishes, so every peer including the host
            // derives its layout from one seed and one pool.
            _mediaPlan = plan;
            CustomMessagingManager manager = NetworkManager.Singleton?.CustomMessagingManager;
            if (manager == null) return;
            using (var writer = new FastBufferWriter(20480, Allocator.Temp))
            {
                writer.WriteValueSafe(plan.Seed);
                writer.WriteValueSafe(plan.MaxPlayers);
                FixedString4096Bytes pool = TruncateFixed(
                    string.Join(";", plan.Pool ?? Array.Empty<string>()), MaxPerMonitorPoolChars);
                FixedString4096Bytes hashes = TruncateFixed(
                    string.Join(";", plan.Hashes ?? Array.Empty<string>()), MaxPerMonitorPoolChars);
                writer.WriteValueSafe(pool);
                writer.WriteValueSafe(hashes);
                manager.SendNamedMessageToAll(MediaPlanMessage, writer);
            }
        }

        private static void OnMediaPlanMessage(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId) return;
            reader.ReadValueSafe(out int seed);
            reader.ReadValueSafe(out int maxPlayers);
            reader.ReadValueSafe(out FixedString4096Bytes pool);
            reader.ReadValueSafe(out FixedString4096Bytes hashes);
            string[] names = SplitSemicolonList(pool.ToString());
            _mediaPlan = new TakeoverMediaPlanWire(
                seed, Mathf.Clamp(maxPlayers, 1, 6), names, SplitSemicolonListPreservingEmpty(hashes.ToString()));
            // Same headroom argument as the payload's media link: start the downloads now.
            foreach (string entry in names)
                YoutubeVideoResolver.Prefetch(entry);
        }

        private static void SendGateSnapshot()
        {
            CustomMessagingManager manager = NetworkManager.Singleton?.CustomMessagingManager;
            if (manager == null || NetworkManager.Singleton?.IsServer != true) return;
            int quota = TimeOfDay.Instance != null ? Math.Max(0, TimeOfDay.Instance.timesFulfilledQuota) : 0;
            // The ship-upgrade ID list belongs to the owning module and arrives as a
            // Y4NGZCore catalogue. With Ship Systems absent the catalogue is empty and the
            // snapshot gates no upgrades, which is the right answer for a profile with none.
            string unlocked = string.Join(",", QuotaUnlockCatalogs
                .GetStableIds(QuotaUnlockCategories.ShipUpgrade)
                .Where(id => IsNamedUnlockSatisfied(id, quota, milestone => milestone.Upgrades))
                .Select(Normalize));
            string constellationRules = BuildRuleString(milestone => milestone.Constellations);
            string suitRules = BuildRuleString(milestone => milestone.Suits);
            string storeItemRules = BuildRuleString(milestone => milestone.StoreItems);
            using (var writer = new FastBufferWriter(17408, Allocator.Temp))
            {
                writer.WriteValueSafe(quota);
                FixedString4096Bytes values = TruncateFixed(unlocked, 1000);
                FixedString4096Bytes constellationValues = TruncateFixed(constellationRules, 1000);
                FixedString4096Bytes suitValues = TruncateFixed(suitRules, 1000);
                FixedString4096Bytes storeItemValues = TruncateFixed(storeItemRules, 1000);
                writer.WriteValueSafe(values);
                writer.WriteValueSafe(constellationValues);
                writer.WriteValueSafe(suitValues);
                writer.WriteValueSafe(storeItemValues);
                manager.SendNamedMessageToAll(GateSnapshotMessage, writer);
            }
            QuotaProgressionSuitGate.OnUnlockStateMaybeChanged(quota);
        }

        private static string BuildRuleString(Func<QuotaMilestoneConfig, IEnumerable<string>> selector)
        {
            return string.Join(";", Milestones.Values
                .Where(m => m.Enable.Value)
                .OrderBy(m => m.Quota)
                .SelectMany(m => selector(m).Select(name => Normalize(name) + "=" + m.Quota))
                .GroupBy(entry => entry.Substring(0, entry.IndexOf('=')), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()));
        }

        private static void ParseRuleString(string rules, Dictionary<string, int> target)
        {
            target.Clear();
            foreach (string entry in (rules ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = entry.IndexOf('=');
                if (separator <= 0 || !int.TryParse(entry.Substring(separator + 1), out int requiredQuota)) continue;
                target[Normalize(entry.Substring(0, separator))] = requiredQuota;
            }
        }

        private static void OnGateSnapshotMessage(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId) return;
            reader.ReadValueSafe(out int quota);
            reader.ReadValueSafe(out FixedString4096Bytes values);
            reader.ReadValueSafe(out FixedString4096Bytes constellationValues);
            reader.ReadValueSafe(out FixedString4096Bytes suitValues);
            reader.ReadValueSafe(out FixedString4096Bytes storeItemValues);
            AuthoritativeUnlockedUpgrades.Clear();
            foreach (string value in values.ToString().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                AuthoritativeUnlockedUpgrades.Add(Normalize(value));
            ParseRuleString(constellationValues.ToString(), AuthoritativeConstellationUnlockQuotas);
            ParseRuleString(suitValues.ToString(), AuthoritativeSuitUnlockQuotas);
            ParseRuleString(storeItemValues.ToString(), AuthoritativeStoreItemUnlockQuotas);
            _authoritativeQuota = Math.Max(0, quota);
            _hasAuthoritativeUpgradeSnapshot = true;
            QuotaProgressionSuitGate.OnUnlockStateMaybeChanged(_authoritativeQuota);
        }

        private static string Normalize(string value) => new string((value ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        private static string CurrentSaveName() => GameNetworkManager.Instance?.currentSaveFileName ?? "unsaved";
        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in value ?? string.Empty) hash = hash * 31 + c;
                return hash == int.MinValue ? 0 : hash;
            }
        }

        private static void WriteEffectiveAudit()
        {
            try
            {
                string path = Y4NGZCompanyPaths.LocalDataFile("quota-progression-effective.tsv");
                var lines = new List<string>
                {
                    "quota\tenabled\tdialogue\tmedia\tcontracts\tupgrades\tconstellations\tsuits\tstore_items\ttokens_per_player\tgroup_credits\tship_fuel"
                };
                foreach (QuotaMilestoneConfig milestone in Milestones.Values.OrderBy(m => m.Quota))
                {
                    string dialogueSource = !string.IsNullOrWhiteSpace(milestone.DialogueFile.Value)
                        ? "file:" + milestone.DialogueFile.Value
                        : (!string.IsNullOrWhiteSpace(milestone.Dialogue.Value) ? "inline" : "built-in");
                    lines.Add(string.Join("\t", new[]
                    {
                        milestone.Quota.ToString(),
                        milestone.Enable.Value.ToString(),
                        EscapeAudit(dialogueSource),
                        EscapeAudit(milestone.MediaFile.Value),
                        EscapeAudit(string.Join(",", milestone.Contracts)),
                        EscapeAudit(string.Join(",", milestone.Upgrades)),
                        EscapeAudit(string.Join(",", milestone.Constellations)),
                        EscapeAudit(string.Join(",", milestone.Suits)),
                        EscapeAudit(string.Join(",", milestone.StoreItems)),
                        milestone.GrantTokensPerPlayer.Value.ToString(),
                        milestone.GrantGroupCredits.Value.ToString(),
                        milestone.GrantShipFuel.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }));
                }
                // Shared takeover keys are not per-quota, so they follow the
                // milestone table as a second two-column block.
                lines.Add(string.Empty);
                lines.Add("shared_key\tvalue");
                lines.Add("DefaultMediaFile\t" + EscapeAudit(_defaultMediaFile?.Value));
                lines.Add("MumbleAudioFiles\t" + EscapeAudit(_mumbleAudioFiles?.Value));
                lines.Add("AlarmAudioFile\t" + EscapeAudit(_alarmAudioFile?.Value));
                lines.Add("SoundtrackSource\t" + EscapeAudit(_soundtrackSource?.Value));
                lines.Add("SoundtrackMode\t" + SoundtrackMode);
                lines.Add("SoundtrackVolume\t" + SoundtrackVolume.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add("RandomPerMonitorMedia\t" + RandomPerMonitorMedia);
                lines.Add("PerMonitorMediaPool\t" + EscapeAudit(_perMonitorMediaPool?.Value));
                lines.Add("MaxConcurrentMediaPlayers\t" + MaxConcurrentMediaPlayers);
                File.WriteAllLines(path, lines);
                _log?.LogInfo($"Quota progression effective audit written to '{path}' ({Milestones.Count} milestones).");
            }
            catch (Exception ex) { _log?.LogWarning($"Quota progression effective audit failed: {ex.Message}"); }
        }

        private static string EscapeAudit(string value) => (value ?? string.Empty).Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

        private static string TruncateFixed(string value, int maxChars)
        {
            value = value ?? string.Empty;
            return value.Length <= maxChars ? value : value.Substring(0, maxChars);
        }

        private static int GetRandomizedIndex(int count, int quota, string salt)
        {
            if (count <= 1) return 0;
            if (_randomizeWithoutReplacement?.Value != true)
                return Math.Abs(StableHash(CurrentSaveName() + ":" + salt + ":" + quota)) % count;

            var order = Enumerable.Range(0, count).ToArray();
            var random = new System.Random(Math.Abs(StableHash(CurrentSaveName() + ":" + salt)));
            for (int i = order.Length - 1; i > 0; i--)
            {
                int swap = random.Next(0, i + 1);
                int value = order[i];
                order[i] = order[swap];
                order[swap] = value;
            }
            return order[Math.Max(0, quota - 1) % count];
        }

        private static string ComputeSha256(string path)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
            catch { return string.Empty; }
        }
    }
}
