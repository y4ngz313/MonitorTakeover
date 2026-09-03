#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
using Y4NGZCore.Modules.Quota;
using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    /// <summary>
    /// Host-authoritative debug path for replaying a completed-quota takeover.
    /// This deliberately does not call the vanilla quota RPC, which also changes
    /// credits, overtime, and the next quota target.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class QuotaTakeoverDebugController : MonoBehaviour
    {
        private const string ForceQuotaMessage = "Y4NGZCompany.ForceQuotaCompletion.v1";

        private static QuotaTakeoverDebugController _instance;
        private NetworkManager _registeredNetworkManager;
        private int _nextSequence = 1;
        private int _lastAppliedSequence = -1;
        private bool _takeoverQueuedUntilOrbit;
        private int _queuedCompletedQuota;
        private float _nextQueuedBroadcastAt;

        internal static void EnsureAttached(GameObject owner)
        {
            if (_instance != null || owner == null)
                return;

            _instance = owner.GetComponent<QuotaTakeoverDebugController>()
                ?? owner.AddComponent<QuotaTakeoverDebugController>();
        }

        internal static Y4NGZCompany.Core.ForcedQuotaCompletionResult ForceCompletedQuota(int completedQuota)
        {
            if (completedQuota < 1 || completedQuota > 99)
                return Y4NGZCompany.Core.ForcedQuotaCompletionResult.InvalidQuota;

            QuotaTakeoverDebugController controller = _instance;
            NetworkManager network = NetworkManager.Singleton;
            if (controller == null || network == null || !network.IsListening)
                return Y4NGZCompany.Core.ForcedQuotaCompletionResult.Unavailable;
            if (!network.IsServer)
                return Y4NGZCompany.Core.ForcedQuotaCompletionResult.HostOnly;
            if (StartOfRound.Instance == null || TimeOfDay.Instance == null)
                return Y4NGZCompany.Core.ForcedQuotaCompletionResult.NotInRound;
            if (TakeoverManager.IsActive
                || QuotaTakeoverHandoff.QuotaJustCompleted || QuotaTakeoverHandoff.MaskManPending)
            {
                return Y4NGZCompany.Core.ForcedQuotaCompletionResult.Busy;
            }

            controller.TryRegisterMessages();

            int sequence = controller._nextSequence++;
            bool startImmediately = IsStableOrbit();

            // Set only the completed-quota counter. The normal quota target, deadline,
            // overtime bonus, and terminal credits are intentionally left untouched.
            TimeOfDay.Instance.timesFulfilledQuota = completedQuota;
            Y4NGZCompany.Core.QuotaProgressionRegistry.OnQuotaCompleted();
            controller.ApplyForcedCompletion(sequence, completedQuota, startImmediately);
            controller.SendForcedCompletion(sequence, completedQuota, startImmediately);

            TakeoverBootstrap.Log.LogInfo(
                $"[QuotaDebug] Forced completed quota {completedQuota}; "
                + (startImmediately ? "starting takeover now." : "queued until the next stable orbit."));

            return startImmediately
                ? Y4NGZCompany.Core.ForcedQuotaCompletionResult.StartedImmediately
                : Y4NGZCompany.Core.ForcedQuotaCompletionResult.QueuedUntilOrbit;
        }

        internal static bool ConsumeQueuedSkipOrbitDelay()
        {
            if (_instance == null || !_instance._takeoverQueuedUntilOrbit)
                return false;

            _instance._takeoverQueuedUntilOrbit = false;
            _instance._queuedCompletedQuota = 0;
            return true;
        }

        internal static void ResetPending()
        {
            QuotaUnlockAnnouncement.ClearDebugReplay();

            if (_instance == null)
                return;

            bool hadQueuedDebugTakeover = _instance._takeoverQueuedUntilOrbit;
            _instance._takeoverQueuedUntilOrbit = false;
            _instance._queuedCompletedQuota = 0;
            if (hadQueuedDebugTakeover)
            {
                QuotaTakeoverHandoff.QuotaJustCompleted = false;
                QuotaTakeoverHandoff.MaskManPending = false;
            }
        }

        private static bool IsStableOrbit()
        {
            StartOfRound start = StartOfRound.Instance;
            return start != null
                && start.inShipPhase
                && !start.shipHasLanded
                && !start.travellingToNewLevel
                && !start.beganLoadingNewLevel
                && (RoundManager.Instance == null || !RoundManager.Instance.dungeonIsGenerating);
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }

            _instance = this;
            TryRegisterMessages();
        }

        private void Update()
        {
            TryRegisterMessages();

            NetworkManager network = NetworkManager.Singleton;
            if (!_takeoverQueuedUntilOrbit || _queuedCompletedQuota <= 0
                || network?.IsServer != true
                || Time.realtimeSinceStartup < _nextQueuedBroadcastAt)
            {
                return;
            }

            // Repeat only the queued state. Existing peers ignore the sequence;
            // a late joiner receives it before the ship next reaches orbit.
            _nextQueuedBroadcastAt = Time.realtimeSinceStartup + 2f;
            SendForcedCompletion(
                _lastAppliedSequence,
                _queuedCompletedQuota,
                startImmediately: false);
        }

        private void OnDestroy()
        {
            UnregisterMessages();
            if (_instance == this)
                _instance = null;
        }

        private void TryRegisterMessages()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsListening || network.CustomMessagingManager == null)
                return;
            if (ReferenceEquals(network, _registeredNetworkManager))
                return;

            UnregisterMessages();
            _nextSequence = 1;
            _lastAppliedSequence = -1;
            _takeoverQueuedUntilOrbit = false;
            _queuedCompletedQuota = 0;
            network.CustomMessagingManager.RegisterNamedMessageHandler(
                ForceQuotaMessage,
                OnForcedCompletionMessage);
            _registeredNetworkManager = network;
        }

        private void UnregisterMessages()
        {
            if (_registeredNetworkManager?.CustomMessagingManager != null)
            {
                try
                {
                    _registeredNetworkManager.CustomMessagingManager
                        .UnregisterNamedMessageHandler(ForceQuotaMessage);
                }
                catch { }
            }

            _registeredNetworkManager = null;
        }

        private void SendForcedCompletion(int sequence, int completedQuota, bool startImmediately)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.IsServer != true || network.CustomMessagingManager == null)
                return;

            using (var writer = new FastBufferWriter(sizeof(int) * 2 + sizeof(bool), Allocator.Temp))
            {
                writer.WriteValueSafe(sequence);
                writer.WriteValueSafe(completedQuota);
                writer.WriteValueSafe(startImmediately);
                network.CustomMessagingManager.SendNamedMessageToAll(ForceQuotaMessage, writer);
            }
        }

        private void OnForcedCompletionMessage(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || senderClientId != NetworkManager.ServerClientId)
                return;

            reader.ReadValueSafe(out int sequence);
            reader.ReadValueSafe(out int completedQuota);
            reader.ReadValueSafe(out bool startImmediately);
            ApplyForcedCompletion(sequence, completedQuota, startImmediately);
        }

        private void ApplyForcedCompletion(int sequence, int completedQuota, bool startImmediately)
        {
            if (sequence <= _lastAppliedSequence || completedQuota < 1 || completedQuota > 99)
                return;
            _lastAppliedSequence = sequence;

            if (TimeOfDay.Instance != null)
                TimeOfDay.Instance.timesFulfilledQuota = completedQuota;

            QuotaTakeoverHandoff.QuotaJustCompleted = false;
            QuotaTakeoverHandoff.MaskManPending = false;

            QuotaUnlockAnnouncement.PrepareDebugReplay(completedQuota);
            if (completedQuota == 3)
            {
                // Debug replay is intentionally repeatable and does not mutate the
                // natural one-shot Mask Man save key.
                QuotaTakeoverHandoff.MaskManPending = true;
            }
            else
            {
                QuotaTakeoverHandoff.QuotaJustCompleted = true;
            }

            _takeoverQueuedUntilOrbit = !startImmediately;
            _queuedCompletedQuota = startImmediately ? 0 : completedQuota;
            _nextQueuedBroadcastAt = Time.realtimeSinceStartup + 2f;
            if (startImmediately)
            {
                if (!Patches.TryStartPendingTakeover(
                        skipOrbitDelay: true,
                        debugForced: true))
                    QuotaUnlockAnnouncement.ClearDebugReplay();
            }
        }
    }
}
#endif
