using HarmonyLib;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Hybrid co-op damage.
    ///
    /// The host still owns scenes, saves, boss state, RNG, and progression. Damage
    /// to a player body is owned by that player's machine when latency-friendly
    /// damage is enabled, so a guest does not lose HP because the host saw an old
    /// lagged position.
    /// </summary>
    [HarmonyPatch(typeof(PlayerDamageReceiver), nameof(PlayerDamageReceiver.TakeDamage))]
    public static class PlayerDamagePatch
    {
        public struct HitState
        {
            public bool Valid;
            public int Health;
            public float StoneTime;
            public bool ChaliceShield;
        }

        static bool Prefix(PlayerDamageReceiver __instance, DamageDealer.DamageInfo info, ref HitState __state)
        {
            __state = CaptureHitState(__instance);

            if (!MultiplayerSession.IsActive)
                return true;
            if (LocalDevSession.IsActive)
                return true;

            var player = __instance.GetComponent<AbstractPlayerController>();
            if (player == null)
                return true;

            if (!Plugin.LatencyFriendlyDamage)
            {
                return MultiplayerSession.IsHost || DamageAuthority.IsAuthorised(__instance, info);
            }

            if (MultiplayerSession.IsClient)
            {
                if (MultiplayerSession.IsLocalPlayer(player.id))
                    return true;

                return DamageAuthority.IsAuthorised(__instance, info);
            }

            if (MultiplayerSession.IsHost && MultiplayerSession.IsNetworkControlledPlayer(player.id))
                return DamageAuthority.IsAuthorised(__instance, info);

            return true;
        }

        static void Postfix(PlayerDamageReceiver __instance, DamageDealer.DamageInfo info, HitState __state)
        {
            if (!MultiplayerSession.IsActive)
                return;
            if (LocalDevSession.IsActive)
                return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (info.damage <= 0f && info.stoneTime <= 0f)
                return;
            if (DamageAuthority.IsApplyingAuthorizedDamage)
                return;

            // TakeDamage silently ignores hits during invulnerability frames,
            // super invincibility, etc. Only replicate hits that actually
            // changed the player; otherwise the peer applies phantom damage.
            if (!HitLanded(__instance, __state))
                return;

            var player = __instance.GetComponent<AbstractPlayerController>();
            if (player == null)
                return;

            if (!Plugin.LatencyFriendlyDamage)
            {
                if (!MultiplayerSession.IsHost)
                    return;

                Plugin.Net.SendDamageEventForParticipant(
                    (byte)player.id,
                    info.damage,
                    info.stoneTime,
                    (byte)info.damageSource,
                    MultiplayerSession.Tick);
                return;
            }

            if (MultiplayerSession.IsHost)
            {
                if (MultiplayerSession.IsNetworkControlledPlayer(player.id))
                    return;

                Plugin.Net.SendDamageEventForParticipant(
                    (byte)player.id,
                    info.damage,
                    info.stoneTime,
                    (byte)info.damageSource,
                    MultiplayerSession.Tick);
                return;
            }

            if (MultiplayerSession.IsClient && MultiplayerSession.IsLocalPlayer(player.id))
            {
                var pkt = new DamageEventPacket
                {
                    TargetPlayerId = (byte)player.id,
                    Damage = info.damage,
                    StoneTime = info.stoneTime,
                    Source = (byte)info.damageSource,
                    Tick = MultiplayerSession.Tick,
                };
                Plugin.Net.SendDamageEvent(ref pkt);
            }
        }

        static HitState CaptureHitState(PlayerDamageReceiver receiver)
        {
            var stats = GetStats(receiver);
            if (stats == null)
                return default(HitState);

            return new HitState
            {
                Valid = true,
                Health = stats.Health,
                StoneTime = stats.StoneTime,
                ChaliceShield = stats.ChaliceShieldOn,
            };
        }

        static bool HitLanded(PlayerDamageReceiver receiver, HitState before)
        {
            if (!before.Valid)
                return false;

            var stats = GetStats(receiver);
            if (stats == null)
                return false;

            return stats.Health < before.Health
                || stats.StoneTime > before.StoneTime + 0.0001f
                || (before.ChaliceShield && !stats.ChaliceShieldOn);
        }

        static PlayerStatsManager GetStats(PlayerDamageReceiver receiver)
        {
            if (receiver == null)
                return null;

            var player = receiver.GetComponent<AbstractPlayerController>();
            return player != null ? player.stats : null;
        }
    }
}
