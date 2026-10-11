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

        public static void Apply(WorldEventPacket pkt)
        {
            switch ((WorldEventKind)pkt.Kind)
            {
                case WorldEventKind.TutorialParryAdvance:
                    Replay<TutorialLevelParryNext>(pkt.ObjectId, TutorialSetNextParry);
                    break;

                case WorldEventKind.TutorialShmupParryAdvance:
                    Replay<TutorialShmupLevelParryNext>(pkt.ObjectId, TutorialShmupSetNextParry);
                    break;
            }
        }

        static void Replay<T>(int objectId, MethodInfo method) where T : Component
        {
            if (method == null)
                return;

            var candidates = UnityEngine.Object.FindObjectsOfType<T>();
            for (int i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (candidate == null || EnemyRegistry.GetStableId(candidate.gameObject) != objectId)
                    continue;

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
