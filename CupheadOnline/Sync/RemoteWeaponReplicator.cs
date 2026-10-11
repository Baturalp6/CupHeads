using System.Collections.Generic;
using UnityEngine;
using CupheadOnline.Net;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Applies weapon events received from the network onto the remote player.
    ///
    /// For the two built-in slots the proxy's own LevelPlayerWeaponManager already
    /// fires real projectiles: it reads Shoot/Super/Switch through Rewired, and the
    /// input router feeds it the remote player's InputFrame. Adding tracers or a
    /// forced "Shooting" animator flag on top only doubled the visuals (and the flag
    /// was never cleared). Here we only align aim, trigger one-shot animations and
    /// apply explicit weapon switches. Extra participants have no weapon manager
    /// and keep their visual-only path.
    /// </summary>
    public static class RemoteWeaponReplicator
    {
        static readonly Dictionary<byte, uint> _lastEventTicks =
            new Dictionary<byte, uint>(4);

        static RemoteWeaponReplicator()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Apply(WeaponEventPacket pkt)
        {
            if (!ShouldApplyEvent(pkt.PlayerId, pkt.Tick))
                return;

            if (pkt.PlayerId > (byte)PlayerId.PlayerTwo)
            {
                ExtraRemoteAvatarManager.ApplyWeaponEvent(pkt);
                return;
            }

            if (MultiplayerSession.IsHost
             && MultiplayerSession.IsNetworkControlledPlayer((PlayerId)pkt.PlayerId))
            {
                return;
            }

            LevelPlayerController player;
            try
            {
                player = PlayerManager.GetPlayer((PlayerId)pkt.PlayerId) as LevelPlayerController;
            }
            catch
            {
                return;
            }

            if (player == null) return;

            // Set look direction so the shot leaves in the right direction
            if (player.motor != null)
            {
                var dir = new Trilean2(pkt.AimX, pkt.AimY);
                Patches.MotorSetters.Set(Patches.MotorSetters.LookDirection, player.motor, dir);
                Patches.MotorSetters.Set(Patches.MotorSetters.TrueLookDirection, player.motor, dir);
            }

            var wm = player.weaponManager;
            if (wm == null) return;

            switch ((WeaponEventType)pkt.EventType)
            {
                case WeaponEventType.Fire:
                    // The proxy's weapon manager fires the real shot from the
                    // replicated Shoot input; nothing extra to draw here.
                    break;

                case WeaponEventType.Ex:
                    TriggerAnimatorTrigger(player, "Ex");
                    break;

                case WeaponEventType.Super:
                    TriggerAnimatorTrigger(player, "Super");
                    break;

                case WeaponEventType.Parry:
                    TriggerAnimatorTrigger(player, "Parry");
                    break;

                case WeaponEventType.Switch:
                    ApplyWeaponSwitch(wm, pkt.WeaponId);
                    break;
            }
        }

        static void TriggerAnimatorTrigger(LevelPlayerController player, string trigger)
        {
            var anim = player.animationController?.animator;
            if (anim == null) return;
            anim.SetTrigger(trigger);
        }

        static void ApplyWeaponSwitch(LevelPlayerWeaponManager wm, byte weaponId)
        {
            // The weapon enum value was sent — try to switch to that weapon
            try
            {
                var weapon = LoadoutCodec.DecodeWeapon(weaponId, primarySlot: true);
                // Call SwitchWeapon via reflection since it's likely private
                var mi = typeof(LevelPlayerWeaponManager).GetMethod(
                    "SwitchWeapon",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                mi?.Invoke(wm, new object[] { weapon });
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[WeaponReplicator] Weapon switch failed: {ex.Message}");
            }
        }

        static bool ShouldApplyEvent(byte playerId, uint tick)
        {
            uint previous;
            if (_lastEventTicks.TryGetValue(playerId, out previous))
            {
                if (NetTick.IsOlder(tick, previous))
                    return false;
                if (NetTick.IsNewer(tick, previous))
                    _lastEventTicks[playerId] = tick;
                return true;
            }

            _lastEventTicks[playerId] = tick;
            return true;
        }

        static void Reset()
        {
            _lastEventTicks.Clear();
        }
    }
}
