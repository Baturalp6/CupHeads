using UnityEngine;
using CupheadOnline.Net;
using System.Collections.Generic;
using System.Reflection;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Tracks which incoming damage events the CLIENT is authorised to apply.
    ///
    /// The host sends a DamageEventPacket whenever a player takes damage.
    /// The client queues an "authorised token" keyed by (targetPlayerId, tick).
    /// When PlayerDamageReceiver.TakeDamage fires locally on the client, the prefix
    /// patch checks this store and only allows execution if a matching token exists.
    ///
    /// This prevents the client from double-counting damage from locally-simulated
    /// hits AND from host-sourced projectiles.
    /// </summary>
    public static class DamageAuthority
    {
        // Tag written onto DamageDealer.DamageInfo to mark authorised damage
        // We compare damage amount + source to avoid spoofing.
        private static float  _pendingDamage;
        private static float  _pendingStoneTime;
        private static byte   _pendingSource;
        private static PlayerId _pendingTarget = PlayerId.None;
        private static bool   _pendingReady;
        private static bool   _applyingAuthorizedDamage;
        private static readonly Dictionary<byte, uint> _lastAppliedTicks =
            new Dictionary<byte, uint>(2);
        private static readonly FieldInfo _damageInfoStoneTimeField =
            typeof(DamageDealer.DamageInfo).GetField(
                "<stoneTime>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

        static DamageAuthority()
        {
            MultiplayerSession.OnSessionEnded += ResetAll;
        }

        public static bool IsApplyingAuthorizedDamage => _applyingAuthorizedDamage;

        // ──────────────────────────────────────────────────────────────────────
        //  Called by PacketDispatcher on DamageEventPacket received
        // ──────────────────────────────────────────────────────────────────────

        public static void ApplyAuthorized(DamageEventPacket pkt)
        {
            if (Level.Current == null)
                return;

            uint previousTick;
            if (_lastAppliedTicks.TryGetValue(pkt.TargetPlayerId, out previousTick)
             && NetTick.IsOlder(pkt.Tick, previousTick))
            {
                return;
            }

            // Find the target player controller and force-apply the damage
            AbstractPlayerController player;
            try
            {
                player = PlayerManager.GetPlayer((PlayerId)pkt.TargetPlayerId);
            }
            catch
            {
                return;
            }

            if (player == null) return;
            if (!(player is LevelPlayerController)) return;
            var dr = player.damageReceiver as PlayerDamageReceiver;
            if (dr == null) return;

            if (!_lastAppliedTicks.ContainsKey(pkt.TargetPlayerId)
             || NetTick.IsNewer(pkt.Tick, _lastAppliedTicks[pkt.TargetPlayerId]))
            {
                _lastAppliedTicks[pkt.TargetPlayerId] = pkt.Tick;
            }

            // Mark as authorised so the prefix patch lets it through
            _pendingDamage = pkt.Damage;
            _pendingStoneTime = pkt.StoneTime;
            _pendingSource = pkt.Source;
            _pendingTarget = (PlayerId)pkt.TargetPlayerId;
            _pendingReady  = true;

            // Build a synthetic DamageInfo and call TakeDamage
            var info = new DamageDealer.DamageInfo(
                pkt.Damage,
                DamageDealer.Direction.Neutral,
                Vector2.zero,
                (DamageDealer.DamageSource)pkt.Source);
            SetStoneTime(info, pkt.StoneTime);

            // The owning peer already resolved invulnerability when the hit
            // landed there. The local copy's i-frame window started at a
            // different (network-delayed) moment, so clear it to make sure
            // an authoritative hit is not dropped.
            ClearInvulnerabilityTimer(dr);

            try
            {
                _applyingAuthorizedDamage = true;
                dr.TakeDamage(info);
            }
            finally
            {
                _applyingAuthorizedDamage = false;
                Reset();
            }
        }

        /// <summary>
        /// Prefix guard called by DamageReceiverPatch.
        /// Returns true (allow) only for damage we triggered ourselves via ApplyAuthorized.
        /// </summary>
        public static bool IsAuthorised(PlayerDamageReceiver receiver, DamageDealer.DamageInfo info)
        {
            if (!_pendingReady) return false;

            var player = receiver != null ? receiver.GetComponent<AbstractPlayerController>() : null;
            if (player == null || player.id != _pendingTarget)
                return false;

            return System.Math.Abs(info.damage - _pendingDamage) < 0.001f
                && System.Math.Abs(info.stoneTime - _pendingStoneTime) < 0.001f
                && info.damageSource == (DamageDealer.DamageSource)_pendingSource;
        }

        public static void Reset()
        {
            _pendingDamage = 0f;
            _pendingStoneTime = 0f;
            _pendingSource = 0;
            _pendingTarget = PlayerId.None;
            _pendingReady = false;
            _applyingAuthorizedDamage = false;
        }

        public static void ResetAll()
        {
            Reset();
            _lastAppliedTicks.Clear();
        }

        private static readonly FieldInfo _receiverTimerField =
            typeof(PlayerDamageReceiver).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void ClearInvulnerabilityTimer(PlayerDamageReceiver receiver)
        {
            if (_receiverTimerField == null || _receiverTimerField.FieldType != typeof(float))
                return;

            try { _receiverTimerField.SetValue(receiver, 0f); }
            catch { }
        }

        private static void SetStoneTime(DamageDealer.DamageInfo info, float stoneTime)
        {
            if (_damageInfoStoneTimeField == null)
                return;

            try
            {
                _damageInfoStoneTimeField.SetValue(info, stoneTime);
            }
            catch
            {
            }
        }
    }
}
