using HarmonyLib;
using UnityEngine;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Patches LevelPlayerMotor.FixedUpdate.
    ///
    /// Host: both built-in gameplay slots run the real Cuphead motor so the host
    /// remains authoritative for movement, collisions, jump state, death, and
    /// animation transitions.
    ///
    /// Client: the host-controlled slot stays a proxy that consumes snapshots.
    /// </summary>
    [HarmonyPatch(typeof(LevelPlayerMotor), "FixedUpdate")]
    public static class PlayerMotorPatch
    {
        static bool Prefix(LevelPlayerMotor __instance)
        {
            if (!MultiplayerSession.IsActive)
                return true;

            var player = __instance.player;
            if (player == null)
                return true;

            byte extraParticipantId;
            if (ExtraRemoteAvatarManager.TryGetAvatarParticipantId(__instance, out extraParticipantId))
            {
                RemoteInputDriver.Tick(extraParticipantId);
                ApplyRemoteState(__instance, extraParticipantId);
                return false;
            }

            if (MultiplayerSession.IsNetworkControlledPlayer(player.id))
            {
                RemoteInputDriver.Tick(player.id);

                // The host simulates the guest with the real gameplay motor.
                if (MultiplayerSession.IsHost && player.id <= PlayerId.PlayerTwo)
                    return true;

                ApplyRemoteState(__instance, (byte)player.id);
                return false;
            }

            return true;
        }

        static void Postfix(LevelPlayerMotor __instance)
        {
            if (!MultiplayerSession.IsActive)
                return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var player = __instance.player;
            if (player == null)
                return;

            bool authoritativeBuiltIn = MultiplayerSession.IsHost && player.id <= PlayerId.PlayerTwo;
            if (!authoritativeBuiltIn && !MultiplayerSession.IsLocalPlayer(player.id))
                return;

            var pkt = BuildStatePacket(player, __instance);

            if (authoritativeBuiltIn)
                Plugin.Net.SendPlayerState(ref pkt);
            else
            {
                SendInputFrameAndState(__instance, player, ref pkt);
                if (MultiplayerSession.IsClient && MultiplayerSession.IsLocalPlayer(player.id))
                    ApplyAuthoritativeCorrection(__instance, player.id);
            }
        }

        internal static byte BuildFlags(AbstractPlayerController player, LevelPlayerMotor m)
        {
            byte f = 0;
            if (m.Grounded) f |= 1;
            if (m.Dashing) f |= 2;
            if (m.Ducking) f |= 4;
            if (m.GravityReversed) f |= 8;
            if (m.IsHit) f |= 16;
            if (m.IsUsingSuperOrEx) f |= 32;
            if (player != null && player.IsDead) f |= 64;
            return f;
        }

        internal static PlayerStatePacket BuildStatePacket(LevelPlayerController player, LevelPlayerMotor motor)
        {
            return new PlayerStatePacket
            {
                PlayerId = (byte)player.id,
                PosX = motor.transform.position.x,
                PosY = motor.transform.position.y,
                LookX = (sbyte)motor.LookDirection.x.Value,
                LookY = (sbyte)motor.LookDirection.y.Value,
                Flags = BuildFlags(player, motor),
                AnimState = GetAnimHash(player),
                Tick = MultiplayerSession.Tick,
            };
        }

        internal static byte GetAnimHash(LevelPlayerController player)
        {
            var anim = player.animationController?.animator;
            if (anim == null)
                return 0;

            return (byte)(anim.GetCurrentAnimatorStateInfo(0).fullPathHash & 0xFF);
        }

        static void SendInputFrameAndState(LevelPlayerMotor motor, LevelPlayerController player, ref PlayerStatePacket statePkt)
        {
            Plugin.Net.SendPlayerState(ref statePkt);
        }

        static void ApplyRemoteState(LevelPlayerMotor motor, byte participantId)
        {
            var snapshot = RemotePlayer.GetNextSnapshot(participantId);
            if (!snapshot.HasValue)
                return;

            var s = snapshot.Value;
            var target = new Vector3(s.PosX, s.PosY, motor.transform.position.z);
            motor.transform.position = Vector3.Lerp(
                motor.transform.position,
                target,
                Mathf.Min(1f, 20f * Time.fixedDeltaTime));

            // The setters are private; MotorSetters binds them once instead of
            // doing a Traverse/reflection lookup on every physics tick.
            var look = new Trilean2(s.LookX, s.LookY);
            MotorSetters.Set(MotorSetters.LookDirection, motor, look);
            MotorSetters.Set(MotorSetters.TrueLookDirection, motor, look);

            InputFramePacket input;
            var move = new Trilean2(0, 0);
            if (RemoteInputDriver.TryGetCurrent(participantId, out input))
            {
                move = new Trilean2(
                    input.AxisX > 0.38f ? 1 : input.AxisX < -0.38f ? -1 : 0,
                    input.AxisY > 0.38f ? 1 : input.AxisY < -0.38f ? -1 : 0);
            }
            MotorSetters.Set(MotorSetters.MoveDirection, motor, move);

            // Dashing, Ducking, IsHit and IsUsingSuperOrEx are computed getters
            // backed by the motor's internal managers; their visual effect on the
            // proxy is driven through the flag-transition events below instead.
            MotorSetters.Set(MotorSetters.Grounded, motor, s.Grounded);
            MotorSetters.Set(MotorSetters.Locked, motor, false);
            MotorSetters.Set(MotorSetters.GravityReversed, motor, s.GravReversed);

            RemotePlayer.UpdateStateTransitions(participantId, motor, s);
        }

        // Fastest regular player movement (dash speed in LevelPlayerMotor.Properties).
        const float MaxPlayerSpeed = 1100f;
        const float CorrectionBaseTolerance = 32f;
        const float CorrectionSnapDistance = 400f;
        const float CorrectionBlend = 0.15f;
        const float MaxSnapshotAge = 0.75f;

        /// <summary>
        /// The host's view of the guest's own body is at least one round trip
        /// old, so differences up to "distance travelable in that time" are
        /// expected and must not be corrected. Each host snapshot is applied at
        /// most once, and stale snapshots are ignored entirely.
        /// </summary>
        static void ApplyAuthoritativeCorrection(LevelPlayerMotor motor, PlayerId playerId)
        {
            PlayerStatePacket snapshot;
            if (!RemotePlayer.TryConsumeLocalAuthoritySnapshot(playerId, MaxSnapshotAge, out snapshot))
                return;

            var target = new Vector3(snapshot.PosX, snapshot.PosY, motor.transform.position.z);
            float distance = Vector2.Distance(motor.transform.position, target);

            float rttSeconds = Plugin.Net != null ? Mathf.Max(0, Plugin.Net.Latency) / 1000f : 0f;
            float tolerance = CorrectionBaseTolerance + MaxPlayerSpeed * (rttSeconds + 0.05f);

            if (distance <= tolerance)
                return;

            motor.transform.position = distance > CorrectionSnapDistance
                ? target
                : Vector3.Lerp(motor.transform.position, target, CorrectionBlend);
        }
    }

    [HarmonyPatch(typeof(PlayerInput), nameof(PlayerInput.GetAxis))]
    public static class PlayerInputAxisPatch
    {
        static bool Prefix(PlayerInput __instance, PlayerInput.Axis axis, ref float __result)
        {
            if (!MultiplayerSession.IsActive)
                return true;
            if (!MultiplayerSession.IsNetworkControlledPlayer(__instance.playerId))
                return true;

            InputFramePacket input;
            if (!RemoteInputDriver.TryGetCurrent(__instance.playerId, out input))
            {
                __result = 0f;
                return false;
            }

            __result = axis == PlayerInput.Axis.X ? input.AxisX : input.AxisY;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInput), nameof(PlayerInput.GetAxisInt))]
    public static class PlayerInputAxisIntPatch
    {
        static bool Prefix(PlayerInput __instance, PlayerInput.Axis axis, ref int __result)
        {
            if (!MultiplayerSession.IsActive)
                return true;
            if (!MultiplayerSession.IsNetworkControlledPlayer(__instance.playerId))
                return true;

            InputFramePacket input;
            if (!RemoteInputDriver.TryGetCurrent(__instance.playerId, out input))
            {
                __result = 0;
                return false;
            }

            float v = axis == PlayerInput.Axis.X ? input.AxisX : input.AxisY;
            __result = v > 0.38f ? 1 : v < -0.38f ? -1 : 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInput), nameof(PlayerInput.GetButton))]
    public static class PlayerInputButtonPatch
    {
        static bool Prefix(PlayerInput __instance, CupheadButton button, ref bool __result)
        {
            if (!MultiplayerSession.IsActive)
                return true;
            if (!MultiplayerSession.IsNetworkControlledPlayer(__instance.playerId))
                return true;

            InputFramePacket input;
            if (!RemoteInputDriver.TryGetCurrent(__instance.playerId, out input))
            {
                __result = false;
                return false;
            }

            __result = input.IsPressed(button);
            return false;
        }
    }

    // NOTE: Cuphead's PlayerInput has no GetButtonDown / GetButtonUp methods —
    // edge queries go through Rewired.Player and are covered by the
    // RewiredPlayerGetButtonDown/Up patches in InputRouterPatches.
}
