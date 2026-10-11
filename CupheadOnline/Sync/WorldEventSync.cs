using System;
using System.Reflection;
using HarmonyLib;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Host-authoritative state for scripted level objects that each peer would
    /// otherwise advance on its own.
    ///
    /// Example: the tutorial's chain of three parry spheres. Each peer advanced
    /// the chain whenever *any* player parried locally, and the guest's view of
    /// both bodies is network-delayed, so the two machines ended up highlighting
    /// different spheres. Now only the host advances the chain and the guest
    /// replays the host's advances.
    /// </summary>
    public static class WorldEventSync
    {
        static readonly MethodInfo TutorialSetNextParry =
            AccessTools.Method(typeof(TutorialLevelParryNext), "SetNextParry");
        static readonly MethodInfo TutorialShmupSetNextParry =
            AccessTools.Method(typeof(TutorialShmupLevelParryNext), "SetNextParry");

        static bool _applyingRemote;

        /// <summary>
        /// True when the local peer may run a host-authoritative world change:
        /// offline, on the host, or while replaying the host's event on a guest.
        /// </summary>
        public static bool MayRunLocally
        {
            get
            {
                if (!MultiplayerSession.IsActive || MultiplayerSession.IsHost)
                    return true;
                if (LocalDevSession.IsActive)
                    return true;
                return _applyingRemote;
            }
        }

        public static void Broadcast(WorldEventKind kind, GameObject target)
        {
            if (!MultiplayerSession.IsActive || !MultiplayerSession.IsHost)
                return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected || target == null)
                return;

            var pkt = new WorldEventPacket
            {
                Kind = (byte)kind,
                ObjectId = EnemyRegistry.GetStableId(target),
                Tick = MultiplayerSession.Tick,
            };
            Plugin.Net.SendWorldEvent(ref pkt);
        }

        /// <summary>
        /// Guest-side: the guest's own body triggered the change locally. The
        /// host's copy of the guest may just miss it, so report it; the host
        /// applies it only if that object is still in the expected state.
        /// </summary>
        public static void RequestFromGuest(WorldEventKind kind, GameObject target)
        {
            if (!MultiplayerSession.IsActive || !MultiplayerSession.IsClient)
                return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected || target == null)
                return;

            var pkt = new WorldEventPacket
            {
                Kind = (byte)kind,
                ObjectId = EnemyRegistry.GetStableId(target),
                Tick = MultiplayerSession.Tick,
            };
            Plugin.Net.SendWorldEvent(ref pkt);
        }

        /// <summary>Guest: replay the host's change. Host: handle a guest request.</summary>
        public static void Apply(WorldEventPacket pkt)
        {
            bool isRequest = MultiplayerSession.IsHost;
            switch ((WorldEventKind)pkt.Kind)
            {
                case WorldEventKind.TutorialParryAdvance:
                    Replay<TutorialLevelParryNext>(pkt.ObjectId, TutorialSetNextParry, isRequest ? TutorialParrySwitchField : null);
                    break;

                case WorldEventKind.TutorialShmupParryAdvance:
                    Replay<TutorialShmupLevelParryNext>(pkt.ObjectId, TutorialShmupSetNextParry, isRequest ? TutorialShmupParrySwitchField : null);
                    break;
            }
        }

        static readonly FieldInfo TutorialParrySwitchField =
            AccessTools.Field(typeof(TutorialLevelParryNext), "parrySwitch");
        static readonly FieldInfo TutorialShmupParrySwitchField =
            AccessTools.Field(typeof(TutorialShmupLevelParryNext), "parrySwitch");

        /// <summary>
        /// A guest request only advances the sphere that is pink right now on
        /// the host, so a request racing the host's own advance is dropped.
        /// </summary>
        static bool IsActiveParrySphere(Component sphere, FieldInfo parrySwitchField)
        {
            if (parrySwitchField == null)
                return false;
            var parrySwitch = parrySwitchField.GetValue(sphere) as Behaviour;
            return parrySwitch != null && parrySwitch.enabled;
        }

        static void Replay<T>(int objectId, MethodInfo method, FieldInfo requireActiveSwitch = null) where T : Component
        {
            if (method == null)
                return;

            var candidates = UnityEngine.Object.FindObjectsOfType<T>();
            for (int i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (candidate == null || EnemyRegistry.GetStableId(candidate.gameObject) != objectId)
                    continue;
                if (requireActiveSwitch != null && !IsActiveParrySphere(candidate, requireActiveSwitch))
                    return;

                _applyingRemote = true;
                try
                {
                    method.Invoke(candidate, null);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[WorldEvent] Replaying " + typeof(T).Name + " failed: " + ex.Message);
                }
                finally
                {
                    _applyingRemote = false;
                }
                return;
            }

            Plugin.LogVerbose("[WorldEvent] No local " + typeof(T).Name + " with id " + objectId + ".");
        }
    }
}
