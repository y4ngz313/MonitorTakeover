using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Core;
using Y4NGZCore.Diagnostics;
using Y4NGZCore.Lifecycle;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    /// <summary>
    /// The Monitor Takeover plugin: the quota milestone registry and its rewards, milestone unlock
    /// gating, the quota announcement ceremony, and the monitor takeover presentation.
    ///
    /// <para><b>#614 task 3.1.</b> This type was created in #612 task 1.4 as a plain static entry
    /// point called from <c>Y4NGZCompany.Plugin.Awake</c>, and extended in #613 task 2.1 to
    /// publish the finale route policy. It is now the real <see cref="BepInPlugin"/> of its own
    /// assembly, and <see cref="Awake"/> performs, in the same order, everything Contracted used
    /// to perform on this feature's behalf: the quota registry's unconditional initialisation, the
    /// unlock PatchAll under this feature's own Harmony owner, the route-policy publish, the
    /// bootstrap call, and this feature's own quota tick — plus the lifecycle it never had: its own
    /// host object, its own config file and its own log channel.</para>
    ///
    /// <para><b>Namespace, not assembly.</b> Everything in this assembly keeps the
    /// <c>Y4NGZCompany.*</c> namespaces it has always had. Only the assembly changed.</para>
    ///
    /// <para><b>Progression is separate from presentation, and always was — this file is where
    /// that becomes structural.</b> <see cref="Bootstrap.TakeoverBootstrap.Initialize"/> returns
    /// early on two conditions: the <c>Enabled</c> config toggle is false, or the asset bundle
    /// fails to load. Everything the crew is <em>owed</em> — milestone evaluation, credits and
    /// reward grants, the store/suit unlock gate, the finale's route lock — is applied BEFORE that
    /// call and outside the isolation, so it survives both. Only the presentation (the video, the
    /// mumbles, the ceremony's monitor lease) lives behind the
    /// bootstrap. Disabling presentation therefore disables presentation and nothing else.</para>
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    // Hard: this assembly compiles against Y4NGZCore (the module contract layer, the monitor
    // arbiter and its bindings, the quota reward and unlock contracts, the HUD suppression
    // registry, the UI theme substrate) and must load after it.
    [BepInDependency("com.y4ngz.core", "1.0.10")]
    //
    // ── NO dependency on com.y4ngz.company or com.y4ngz.company.shipsystems, and none may be
    //    added. ──────────────────────────────────────────────────────────────────────────────
    //
    // The declared edge set among the six Y4NGZ GUIDs after this task is:
    //
    //     Contracted  -> Core (hard), ShipSystems (soft), Atmosphere (soft), BloodFX (soft)
    //     ShipSystems -> Core (hard)
    //     Takeover    -> Core (hard)          <- this file, and this is all of it
    //     CCTV        -> Core (hard), Contracted (soft)
    //     Atmosphere  -> (none of ours)
    //     BloodFX     -> (none of ours)
    //
    // which is a DAG rooted at Core. Adding `Takeover -soft-> Contracted` would still be acyclic
    // today, but it would put this plugin one edit away from the failure #613 shipped: Contracted
    // declaring the mirror edge back is a two-node cycle, BepInEx 5 counts SOFT dependencies in
    // Chainloader.Start's topological sort, and a cycle there is Fatal — it does not skip the
    // offending plugin, it loads NOTHING in the profile. So the edge has to be earned, and it is
    // not: nothing this plugin does at Awake reads anything either of the other two publishes.
    //
    //   * Everything this Awake touches is Y4NGZCore state (ModuleLog, ModuleTickRegistry,
    //     ModuleHandshakeService, the reward/unlock/route provider registries and the monitor
    //     arbiter) plus this assembly's own types, and Core is a hard dependency of all three.
    //   * The route policy this plugin publishes is CONSUMED by Ship Systems' terminal catalogue,
    //     which is built in-lobby, seconds after every plugin's Awake — so the direction of that
    //     seam wants Takeover early if anything, not late, and it is mediated by Core either way.
    //   * The monitor-wall registration (TakeoverMonitorOwnership.EnsureRegistered) publishes into
    //     Y4NGZCore's arbiter, which arbitrates by priority at lease time rather than by
    //     registration order. Ship Systems' banner claim and this plugin's ceremony claim resolve
    //     against each other whichever loaded first.
    //   * OpenBodyCams is NOT declared as a soft dependency even though OpenBodyCamsCompat exists,
    //     because that compat is resolved lazily at takeover start (OpenBodyCamsCompat.Resolve,
    //     via Chainloader.PluginInfos) — which is in-lobby, long after the chainloader has
    //     finished. A load-order flag would buy it nothing.
    //
    // ModuleLifecycleRegression.NoDependencyCycleAmongTheY4NGZPlugins parses all seven shipped
    // plugin sources, including this one, and fails if any cycle among our own GUIDs is ever
    // declared.
    public sealed class MonitorTakeoverPlugin : BaseUnityPlugin
    {
        /// <summary>
        /// This plugin's BepInEx GUID, which is also the Harmony owner id every Monitor Takeover
        /// patch is applied under. Aliased to the Core table so the shipped GUIDs are declared in
        /// one place; the literal is frozen.
        /// </summary>
        public const string Guid = ModuleHarmonyIds.MonitorTakeover;

        /// <summary>
        /// Plugin display name — what BepInEx prints in the chainloader banner. NOT the config
        /// file's name: BepInEx 5 names <c>BaseUnityPlugin.Config</c> after the plugin GUID, so
        /// this plugin's file is <c>com.y4ngz.company.monitortakeover.cfg</c>. See the config note
        /// in <see cref="Awake"/>.
        /// </summary>
        public const string Name = "Monitor Takeover";

        /// <summary>Plugin version.</summary>
        public const string Version = "1.1.0";

        /// <summary>
        /// The wire-protocol revision this build of Monitor Takeover speaks, advertised through
        /// <see cref="Y4NGZCore.Modules.Net.ModuleHandshakeService"/>. A peer running Monitor
        /// Takeover at a different revision is refused with a message naming both.
        ///
        /// <para><b>What it covers:</b> <c>QuotaUnlockAnnouncement</c>'s one message and its
        /// payload layout, <c>QuotaProgressionRegistry</c>'s quota snapshot, and
        /// <c>QuotaTakeoverDebugController</c>'s forced-quota message. There are no woven RPCs to
        /// cover — this assembly declares no NetworkBehaviour at all.</para>
        ///
        /// <para><b>When to bump it:</b> only when one of those payloads, or the host-authoritative
        /// meaning of one, changes such that a peer on the previous revision would misread it.
        /// <b>Not</b> for a <see cref="Version"/> change — bumping per release would make every
        /// patch refuse to play with itself.</para>
        ///
        /// <para>Revision <c>1</c> is the split build, and stays <c>1</c> until a release
        /// deliberately drops monolith compatibility. The monolith predates the handshake and
        /// announces nothing, so a monolith peer is reported as "Monitor Takeover not installed"
        /// rather than as a mismatch.</para>
        /// </summary>
        public const int MonitorTakeoverProtocolRevision = 1;

        /// <summary>The Harmony owner id, by its historical name. Kept for call sites that read it.</summary>
        internal const string HarmonyId = Guid;

        /// <summary>
        /// Name of the GameObject that owns <see cref="MonitorTakeoverHost"/>. Every per-frame tick
        /// and every teardown this plugin performs hangs off that object's lifetime.
        /// </summary>
        internal const string HostObjectName = "Y4NGZMonitorTakeover_Host";

        internal static MonitorTakeoverPlugin Instance { get; private set; }

        /// <summary>
        /// The MonoBehaviour anything in this assembly should run a coroutine on. Never the plugin
        /// component itself: that lives on BepInEx's shared, externally destroyable manager
        /// object, while this is our own. Same reasoning as Contracted's #393 note.
        /// </summary>
        internal static MonoBehaviour CoroutineHost { get; private set; }

        private Harmony _harmony;
        private MonitorTakeoverHost _host;

        /// <summary>
        /// Set by this component's own <c>OnApplicationQuit</c>, which Unity raises on every active
        /// MonoBehaviour before it starts destroying them. Read by <see cref="OnDestroy"/> only to
        /// choose the log line — never to change what is torn down.
        /// <see cref="MonitorTakeoverHost"/> keeps its own copy of the same flag because the two
        /// components can be destroyed in either order.
        /// </summary>
        private bool _applicationQuitting;

        private void Awake()
        {
            Instance = this;

            // Bind only this plugin's own channel. Contracted binds Contracts and Bundy, Ship
            // Systems binds ShipSystems/Store/Terminal. In the monolith all six were one source;
            // now this one carries this plugin's BepInEx prefix with no edit at any call site.
            //
            // On a profile with neither of the other two installed, their channels are never bound
            // at all. That is why ModuleLog's accessors return a null-safe fallback rather than
            // null: nothing here writes to those channels, but a shared Core service reached from
            // here must not throw because a channel its other caller owns has no source.
            ModuleLog.Takeover = Logger;
            ModuleTickRegistry.WarningSink = message => Logger.LogWarning(message);

            SplitAssemblyDiagnostics.Report(Logger);

            // Import before the first Bind so BepInEx resolves migrated values, not defaults.
            MonitorTakeoverConfigMigration.MigrateConfigSafely(Config, Logger);

            // #393: not on BepInEx's shared "BepInEx_Manager" object. With the BepInEx default
            // (Chainloader/HideManagerGameObject=false) that object is findable by name and any mod
            // doing Find-based cleanup can destroy it plus every plugin component on it. A
            // self-owned, hide-flagged host is unreachable by name, so our lifetime is ours alone.
            var hostObject = new GameObject(HostObjectName)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            UnityEngine.Object.DontDestroyOnLoad(hostObject);
            _host = hostObject.AddComponent<MonitorTakeoverHost>();
            CoroutineHost = _host;

            // This assembly declares no NetworkBehaviour, ServerRpc, or ClientRpc types. Its
            // entire wire surface uses CustomMessagingManager named messages, so it is not
            // netcode-patched and must not call NetcodeRuntimeBootstrap.Initialize: that helper
            // deliberately rejects an assembly with zero generated runtime initializers.

            // Publish before anything can join so the first client is compared against a
            // complete manifest. This is the wire protocol revision, not the package version;
            // patch releases remain compatible until a payload or its authority semantics
            // actually change.
            Y4NGZCore.Modules.Net.ModuleHandshakeService.Publish(
                Y4NGZCore.Lifecycle.ModuleIds.MonitorTakeover,
                MonitorTakeoverProtocolRevision,
                Version);

            // The importer ran before host creation and before any Bind. Contracted's migration
            // continues to leave these sections alone because this plugin is their sole owner.
            _harmony = new Harmony(Guid);

            Initialize(Config, Logger, _harmony, InitializeModule);

            // Only now that every module is initialized does the host start ticking and become
            // responsible for teardown.
            _host.Activate(_harmony, Logger);

            Logger.LogInfo($"{Name} v{Version} loaded.");
        }

        /// <summary>
        /// Safety net only, and it must stay one. The real teardown lives on
        /// <see cref="MonitorTakeoverHost"/>; this runs the same sequence solely for the two cases
        /// that host cannot cover.
        ///
        /// <para><b>It must NOT tear down while the host is alive.</b> Same defect, same shape, as
        /// the one diagnosed on <c>ShipSystemsPlugin.OnDestroy</c> — see the long note there for
        /// the observed log evidence. This component rides on BepInEx's shared
        /// <c>BepInEx_Manager</c> object, which with the BepInEx default
        /// <c>Chainloader/HideManagerGameObject=false</c> is reachable by
        /// <c>GameObject.Find("BepInEx_Manager")</c> and is destroyed outright by some mods'
        /// cleanup — observed on a real profile one frame after "Chainloader startup complete".
        /// Running the teardown then would call <c>ModuleTickRegistry.RunTeardown(MonitorTakeover)</c>,
        /// which removes this owner's tick registrations and latches the owner against ever
        /// re-registering, plus <c>Harmony.UnpatchSelf</c> — while
        /// <c>Y4NGZMonitorTakeover_Host</c> is alive and still pumping. The quota registry would
        /// stop ticking, milestone unlocks would stop being enforced, and nothing would be written
        /// anywhere, because the loud message lives on
        /// <see cref="MonitorTakeoverHost.OnDestroy"/>, which never ran.</para>
        ///
        /// <para><b>The three reachable branches, exactly.</b></para>
        /// <list type="number">
        /// <item><description><see cref="MonitorTakeoverHost.HasTornDown"/> — the host already ran
        /// the shutdown. Nothing to do.</description></item>
        /// <item><description>A host that exists <em>and</em> is activated — the plugin is running
        /// normally and something destroyed BepInEx's shared object out from under it. Warn once
        /// (or log at Debug on an ordinary quit, where Unity may reach this component first) and
        /// leave every patch and registration applied.</description></item>
        /// <item><description>Everything else — no host, or a host that was created but never
        /// activated. The second half is not hypothetical: <see cref="Awake"/> assigns
        /// <c>_host</c> before <c>ModuleHandshakeService.Publish</c> and before
        /// <see cref="Initialize"/>, so a throw in between leaves a live, unactivated host.
        /// <see cref="MonitorTakeoverHost.OnDestroy"/> returns at its <c>!_activated</c> early-out
        /// in that state, so this branch is the only path that can unwind whatever the partial
        /// <c>Awake</c> managed to apply. <c>TearDown</c> tolerates a null Harmony, which is the
        /// shape a pre-<c>Activate</c> failure leaves behind.</description></item>
        /// </list>
        /// </summary>
        private void OnApplicationQuit()
        {
            _applicationQuitting = true;
        }

        private void OnDestroy()
        {
            if (MonitorTakeoverHost.HasTornDown)
                return;

            // Unity's overloaded null: a destroyed host compares equal to null and falls through
            // to the real teardown below. So does a host that exists but never reached Activate -
            // nothing else will ever tear that state down. Only a LIVE, ACTIVATED host means "the
            // plugin is still running and this is not a shutdown".
            if (_host != null && _host.IsActivated)
            {
                // Same branch, two very different situations. On an ordinary application quit
                // Unity may destroy BepInEx's manager object - and this component with it - before
                // it reaches the host, so this branch is on the normal exit path too. Warning about
                // "mid-session" destruction there would put a false alarm in every log the mod ever
                // produces, which is how a real one stops being read. The teardown decision is
                // unchanged either way: the host is alive and will run the shutdown from its own
                // OnDestroy.
                if (_applicationQuitting)
                {
                    Logger.LogDebug(
                        "[Y4NGZMonitorTakeover] Plugin component destroyed during application quit; "
                        + $"'{HostObjectName}' is still alive and owns the shutdown.");
                    return;
                }

                Logger.LogWarning(
                    "[Y4NGZMonitorTakeover] The plugin component's GameObject was destroyed "
                    + $"mid-session (frame={Time.frameCount}; BepInEx [Chainloader] "
                    + "HideManagerGameObject is false, so BepInEx_Manager is destroyable by any "
                    + $"mod's Find-based cleanup). '{HostObjectName}' is alive, so nothing was torn "
                    + "down: the tick registrations and every Harmony patch this plugin applied "
                    + "remain ACTIVE and gameplay continues normally.");
                return;
            }

            MonitorTakeoverHost.TearDown(_harmony, Logger);
        }

        /// <summary>
        /// Initialise Monitor Takeover.
        ///
        /// <para>Still a separate method taking its collaborators as parameters, rather than
        /// inlined into <see cref="Awake"/>: the call sequence and its isolation wording are
        /// unchanged from the pre-split <c>Plugin.Awake</c>, so a bootstrap that throws is isolated
        /// exactly as before, and keeping the signature keeps the method callable from an
        /// off-engine harness.</para>
        ///
        /// <para><b>The order of the four blocks below is the acceptance criterion "disabling
        /// presentation does not disable progression or rewards", written as code.</b> Blocks one
        /// to three are unconditional and outside the isolation; only block four can return
        /// early.</para>
        /// </summary>
        internal static void Initialize(
            BepInEx.Configuration.ConfigFile config,
            ManualLogSource logger,
            Harmony harmony,
            Action<string, Action> isolate)
        {
            // (1) PROGRESSION. The one host-authoritative publisher, milestone registry, reward
            // evaluation and reward ledger. All are unconditional and travel in this assembly:
            // rewards are owed whether or not presentation runs, and a Core + Monitor Takeover
            // profile must receive the same rollover input as the full stack. Contracted may
            // stage an objective delta through Core, but it neither owns nor patches a publisher.
            harmony.PatchAll(typeof(QuotaRolloverPublisher));
            QuotaProgressionRegistry.Initialize(config, logger);

            // (2) UNLOCK GATING. StartOfRound.PositionSuitsOnRack plus the two Terminal purchase
            // methods. Applied under THIS plugin's Harmony owner and outside the isolation, because
            // a crew that has not unlocked an item must still be unable to buy it when the takeover
            // is config-disabled or its bundle is missing. Moved here verbatim from Plugin.Awake,
            // where it was already applied under ModuleHarmonyIds.MonitorTakeover.
            harmony.PatchAll(typeof(QuotaProgressionUnlockPatches));

            // (3) PRESENTATION. Everything that can be turned off: the video, the mumbles, the
            // alarm, the ceremony's monitor lease. TakeoverBootstrap
            // returns early when Enabled is false or the bundle fails to load, and nothing above
            // this line is behind that gate.
            isolate("Monitor Takeover", () => Bootstrap.TakeoverBootstrap.Initialize(config, logger, harmony));

            // This plugin's per-frame subscriber, in the slot the pre-split ordered block gave it
            // (ModuleTickOrder.QuotaProgression). Registered here rather than from the bootstrap so
            // it is registered whether or not the bootstrap returned early on a disabled config
            // toggle - which is what Plugin.Awake did, since it registered this unconditionally.
            //
            // Owner is MonitorTakeover, not Contracted. It was Contracted's only because the code
            // was in Contracted's assembly; leaving it there would mean Contracted's host dying
            // strips this plugin's quota tick out of a pump this plugin's own live host is still
            // driving, permanently and silently.
            ModuleTickRegistry.RegisterTick(
                ModuleHarmonyIds.MonitorTakeover,
                ModuleTickOrder.QuotaProgression,
                "QuotaProgressionRegistry",
                QuotaProgressionRegistry.Tick);
        }

        /// <summary>
        /// Per-module isolation, identical in shape and wording to Contracted's and Ship Systems'.
        /// </summary>
        internal static void InitializeModule(string label, Action initialize)
        {
            try
            {
                initialize();
            }
            catch (Exception ex)
            {
                ModuleLog.Takeover?.LogError(
                    $"Y4NGZMonitorTakeover module '{label}' failed to initialize: {ex}");
            }
        }
    }

    /// <summary>
    /// Runtime host for the Monitor Takeover plugin: owns this plugin's per-frame tick pump and its
    /// teardown.
    ///
    /// <para>#393: rides on the plugin's own hide-flagged, <c>DontDestroyOnLoad</c> GameObject
    /// rather than BepInEx's shared manager object, which is discoverable by
    /// <c>GameObject.Find("BepInEx_Manager")</c> and therefore destroyable by another mod's
    /// cleanup. Losing this component stops the quota progression tick, and
    /// <c>Harmony.UnpatchSelf</c> strips every patch this plugin applied — so an unexpected destroy
    /// is logged loudly.</para>
    /// </summary>
    internal sealed class MonitorTakeoverHost : MonoBehaviour
    {
        private static bool _tornDown;

        private Harmony _harmony;
        private ManualLogSource _log;
        private bool _activated;
        private bool _applicationQuitting;

        /// <summary>True once this plugin's shutdown sequence has run, from either owner.</summary>
        internal static bool HasTornDown => _tornDown;

        /// <summary>
        /// True once <see cref="Activate"/> has run — i.e. once <c>Awake</c> finished and this host
        /// became responsible for the tick and the teardown.
        ///
        /// <para>Read by <see cref="MonitorTakeoverPlugin.OnDestroy"/>, which must distinguish "the
        /// shared BepInEx manager object died under a running plugin" (this is true: leave
        /// everything applied) from "Awake threw after creating the host but before activating it"
        /// (this is false: nobody else will ever tear the partial state down, so the safety net
        /// must). This host's own <c>OnDestroy</c> returns early in the second case, so without
        /// this the teardown would be unreachable from either side.</para>
        /// </summary>
        internal bool IsActivated => _activated;

        internal void Activate(Harmony harmony, ManualLogSource log)
        {
            _harmony = harmony;
            _log = log;
            _activated = true;
        }

        /// <summary>
        /// Pumps <see cref="ModuleTickRegistry"/>. Three plugins now have a host and all three call
        /// this; the registry runs every registered subscriber in <see cref="ModuleTickOrder"/>
        /// regardless of which host's frame it was reached from, which is the whole reason the
        /// ordering moved into Core in #612 task 1.4.
        ///
        /// <para><b>More than one host pumps it, so the pump is frame-scoped.</b>
        /// <see cref="ModuleTickRegistry.Tick(int)"/> runs the list at most once per
        /// <c>Time.frameCount</c>; whichever host's <c>LateUpdate</c> Unity reaches first that
        /// frame does the work and the others return 0. Every host must still pump, because any one
        /// of the three plugins can be the one that is not installed — and on a Core + Monitor
        /// Takeover profile this is the ONLY host there is.</para>
        /// </summary>
        private void LateUpdate()
        {
            if (!_activated)
                return;

            ModuleTickRegistry.Tick(Time.frameCount);
        }

        private void OnApplicationQuit()
        {
            _applicationQuitting = true;
        }

        private void OnDestroy()
        {
            if (!_activated)
                return;

            if (!_applicationQuitting)
            {
                _log?.LogError(
                    "[Y4NGZMonitorTakeover] Host destroyed outside application shutdown " +
                    $"(frame={Time.frameCount}, host='{(gameObject != null ? gameObject.name : "<null>")}', " +
                    $"scene='{(gameObject != null ? gameObject.scene.name : "<null>")}'). " +
                    "Quota progression and every Harmony patch this plugin applied are going down " +
                    "with it.");
            }

            TearDown(_harmony, _log);
        }

        /// <summary>
        /// Idempotent: the first caller (host or the plugin's safety-net OnDestroy) wins.
        ///
        /// <para><b>This host tears down exactly one owner.</b> Contracted's host names
        /// <see cref="ModuleHarmonyIds.Contracted"/>, Ship Systems' names
        /// <see cref="ModuleHarmonyIds.ShipSystems"/>, and this one names
        /// <see cref="ModuleHarmonyIds.MonitorTakeover"/> and nothing else. That is precisely why
        /// <see cref="ModuleTickRegistry"/> latches teardown per owner rather than once per
        /// process.</para>
        ///
        /// <para>One Harmony owner, one <c>UnpatchSelf</c>. Unpatching another plugin's id from
        /// here is the exact failure the split's separate ids exist to make impossible.</para>
        /// </summary>
        internal static void TearDown(Harmony harmony, ManualLogSource log)
        {
            if (_tornDown)
                return;
            _tornDown = true;

            ModuleTickRegistry.RunTeardown(ModuleHarmonyIds.MonitorTakeover);

#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            // #861: the takeover's audio decodes run on a TakeoverAudioLoader on this host's
            // object, which releases its own requests as the object goes. Shutdown also forgets
            // the selection and destroys the decoded clips, whichever object ran the loads.
            try
            {
                TakeoverAudioOverrides.Reset("Monitor Takeover host shut down");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Y4NGZMonitorTakeover shutdown warning: takeover audio reset failed: {ex.Message}");
            }
#endif

            if (harmony == null)
                return;

            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception ex)
            {
                log?.LogWarning(
                    "Y4NGZMonitorTakeover shutdown warning: UnpatchSelf failed for Harmony id "
                    + $"'{harmony.Id}': {ex.Message}");
            }
        }
    }
}
