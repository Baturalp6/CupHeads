using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    static class MapPlayerNetVisuals
    {
        struct VisualState
        {
            public sbyte X;
            public sbyte Y;
            public bool Moving;
        }

        static readonly Dictionary<byte, VisualState> States = new Dictionary<byte, VisualState>(2);

        static MapPlayerNetVisuals()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Set(byte participantId, sbyte x, sbyte y)
        {
            States[participantId] = new VisualState
            {
                X = ClampAxis(x),
                Y = ClampAxis(y),
                Moving = x != 0 || y != 0,
            };
        }

        public static bool TryGet(byte participantId, out sbyte x, out sbyte y, out bool moving)
        {
            VisualState state;
            if (States.TryGetValue(participantId, out state))
            {
                x = state.X;
                y = state.Y;
                moving = state.Moving;
                return true;
            }

            x = 0;
            y = 0;
            moving = false;
            return false;
        }

        static sbyte ClampAxis(sbyte value)
        {
            if (value > 0) return 1;
            if (value < 0) return -1;
            return 0;
        }

        static void Reset()
        {
            States.Clear();
        }
    }

    [HarmonyPatch(typeof(MapPlayerMotor), "Update")]
    public static class MapPlayerMotorPatch
    {
        static bool Prefix(MapPlayerMotor __instance)
        {
            if (!MultiplayerSession.IsActive)
                return true;

            MultiplayerSession.EnsureCupheadMultiplayerState();

            var player = __instance != null ? __instance.player : null;
            if (player == null)
                return true;

            if (!MultiplayerSession.IsNetworkControlledPlayer(player.id))
                return true;

            RemoteInputDriver.Tick(player.id);
            if (MultiplayerSession.IsHost && player.id <= PlayerId.PlayerTwo)
                return true;

            ApplyRemoteMapState(__instance, (byte)player.id);
            return false;
        }

        static void Postfix(MapPlayerMotor __instance)
        {
            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var player = __instance != null ? __instance.player : null;
            if (player == null)
                return;

            bool authoritativeBuiltIn = MultiplayerSession.IsHost && player.id <= PlayerId.PlayerTwo;
            if (!authoritativeBuiltIn && !MultiplayerSession.IsLocalPlayer(player.id))
                return;

            var packet = BuildMapStatePacket(player, __instance);
            Plugin.Net.SendPlayerState(ref packet);

            if (MultiplayerSession.IsClient && MultiplayerSession.IsLocalPlayer(player.id))
                ApplyHostMapCorrection(__instance, player.id);
        }

        // Map movement is 2.5 world units per second (MapPlayerMotor.SPEED).
        const float MapSpeed = 2.5f;
        const float MapBaseTolerance = 0.25f;
        const float MapSnapDistance = 3f;
        const float MapCorrectionBlend = 0.2f;
        const float MapMaxSnapshotAge = 0.75f;

        static bool _initialMapSnapDone;

        /// <summary>Called when a map scene starts; the next host snapshot places the guest.</summary>
        internal static void ResetForNewMap()
        {
            _initialMapSnapDone = false;
        }

        /// <summary>
        /// The guest's own map body spawns from the guest's save file
        /// (CurrentMapData.playerTwoPosition), which is unrelated to where the
        /// host's world has that player. The host simulates the guest's body,
        /// so its position wins: snap once on arrival, then only correct drift
        /// beyond what latency explains.
        /// </summary>
        static void ApplyHostMapCorrection(MapPlayerMotor motor, PlayerId playerId)
        {
            PlayerStatePacket snapshot;
            if (!RemotePlayer.TryConsumeLocalAuthoritySnapshot(playerId, MapMaxSnapshotAge, out snapshot))
                return;

            var current = motor.transform.position;
            var target = new Vector3(snapshot.PosX, snapshot.PosY, current.z);
            float distance = Vector2.Distance(current, target);

            Vector3 next;
            if (!_initialMapSnapDone || distance > MapSnapDistance)
            {
                _initialMapSnapDone = true;
                next = target;
            }
            else
            {
                float rttSeconds = Plugin.Net != null ? Mathf.Max(0, Plugin.Net.Latency) / 1000f : 0f;
                float tolerance = MapBaseTolerance + MapSpeed * (rttSeconds + 0.05f);
                if (distance <= tolerance)
                    return;

                next = Vector3.Lerp(current, target, MapCorrectionBlend);
            }

            motor.transform.position = next;
            var body = motor.GetComponent<Rigidbody2D>();
            if (body != null)
                body.position = next;
        }

        static PlayerStatePacket BuildMapStatePacket(MapPlayerController player, MapPlayerMotor motor)
        {
            int x = 0;
            int y = 0;
            try
            {
                if (player.input != null)
                {
                    x = player.input.GetAxisInt(PlayerInput.Axis.X, false, false);
                    y = player.input.GetAxisInt(PlayerInput.Axis.Y, false, false);
                }
            }
            catch
            {
                x = AxisFromVelocity(motor.velocity.x);
                y = AxisFromVelocity(motor.velocity.y);
            }

            if (x == 0 && y == 0 && motor.velocity.sqrMagnitude > 0.01f)
            {
                x = AxisFromVelocity(motor.velocity.x);
                y = AxisFromVelocity(motor.velocity.y);
            }

            return new PlayerStatePacket
            {
                PlayerId = (byte)player.id,
                PosX = motor.transform.position.x,
                PosY = motor.transform.position.y,
                LookX = (sbyte)x,
                LookY = (sbyte)y,
                Flags = 0,
                AnimState = (byte)player.state,
                Tick = MultiplayerSession.Tick,
            };
        }

        static void ApplyRemoteMapState(MapPlayerMotor motor, byte participantId)
        {
            var snapshot = RemotePlayer.GetNextSnapshot(participantId);
            if (!snapshot.HasValue)
            {
                StopPhysics(motor);
                return;
            }

            var state = snapshot.Value;
            var target = new Vector3(state.PosX, state.PosY, motor.transform.position.z);
            float t = Mathf.Min(1f, 20f * Time.deltaTime);
            motor.transform.position = Vector3.Lerp(motor.transform.position, target, t);
            Traverse.Create(motor).Property("velocity").SetValue(new Vector2(state.LookX, state.LookY) * 2.5f);
            StopPhysics(motor);
            MapPlayerNetVisuals.Set(participantId, state.LookX, state.LookY);
        }

        static void StopPhysics(MapPlayerMotor motor)
        {
            var body = motor.GetComponent<Rigidbody2D>();
            if (body != null)
                body.velocity = Vector2.zero;
        }

        static int AxisFromVelocity(float value)
        {
            if (value > 0.05f) return 1;
            if (value < -0.05f) return -1;
            return 0;
        }
    }

    [HarmonyPatch(typeof(MapPlayerAnimationController), "Update")]
    public static class MapPlayerAnimationPatch
    {
        static bool Prefix(MapPlayerAnimationController __instance)
        {
            if (!MultiplayerSession.IsActive || __instance == null)
                return true;

            var player = __instance.player;
            if (player == null || !MultiplayerSession.IsNetworkControlledPlayer(player.id))
                return true;

            if (MultiplayerSession.IsHost && player.id <= PlayerId.PlayerTwo)
                return true;

            sbyte x;
            sbyte y;
            bool moving;
            if (!MapPlayerNetVisuals.TryGet((byte)player.id, out x, out y, out moving))
            {
                x = 0;
                y = 0;
                moving = false;
            }

            ApplyRemoteAnimation(__instance, x, y, moving);
            return false;
        }

        static void ApplyRemoteAnimation(MapPlayerAnimationController controller, sbyte x, sbyte y, bool moving)
        {
            Traverse.Create(controller).Property("state").SetValue(moving
                ? MapPlayerAnimationController.State.Walk
                : MapPlayerAnimationController.State.Idle);
            controller.facingUpwards = y > 0;

            if (controller.spriteRenderer != null && x != 0)
                controller.spriteRenderer.transform.SetScale(new float?(x < 0 ? -1f : 1f), null, null);

            Traverse.Create(controller).Field("axis").SetValue(new Trilean2((int)x, (int)y));
            Traverse.Create(controller).Field("directionRotation").SetValue(DirectionRotation(x, y));

            var animator = controller.animator;
            if (animator == null)
                return;

            animator.SetInteger("X", x);
            animator.SetInteger("Y", y);
            animator.SetInteger("Speed", moving ? 1 : 0);
        }

        static float DirectionRotation(sbyte x, sbyte y)
        {
            if (x == 1 && y == 1) return -45f;
            if (x == 1 && y == 0) return -90f;
            if (x == 1 && y == -1) return -135f;
            if (x == 0 && y == 1) return 0f;
            if (x == 0 && y == -1) return -180f;
            if (x == -1 && y == 1) return 45f;
            if (x == -1 && y == 0) return 90f;
            if (x == -1 && y == -1) return 135f;
            return 0f;
        }
    }
}
