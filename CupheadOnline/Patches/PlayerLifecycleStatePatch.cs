using HarmonyLib;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    [HarmonyPatch(typeof(AbstractPlayerController), "OnDeath")]
    public static class PlayerDeathStatePatch
    {
        static void Prefix(AbstractPlayerController __instance)
        {
            TrySendImmediateState(__instance);
        }

        internal static void TrySendImmediateState(AbstractPlayerController player)
        {
            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected || player == null)
                return;
            if (!MultiplayerSession.IsAuthoritativePlayer(player.id))
                return;

            var levelPlayer = player as LevelPlayerController;
            if (levelPlayer == null || levelPlayer.motor == null)
                return;

            var pkt = PlayerMotorPatch.BuildStatePacket(levelPlayer, levelPlayer.motor);
            Plugin.Net.SendPlayerState(ref pkt);
            ParticipantStatusTracker.PushLocalStatus(player);
        }
    }

    [HarmonyPatch(typeof(AbstractPlayerController), "OnRevive")]
    public static class PlayerReviveStatePatch
    {
        static void Postfix(AbstractPlayerController __instance, UnityEngine.Vector3 pos)
        {
            PlayerDeathStatePatch.TrySendImmediateState(__instance);

            // The host decides revives. Without this the guest only revived if
            // its own (network-delayed) copy of the partner happened to hit the
            // ghost too; otherwise the body stayed deactivated on the guest's
            // screen while the host already saw it alive.
            ParticipantReviveController.MirrorHostRevive(__instance, pos);
        }
    }

    [HarmonyPatch(typeof(PlayerDeathEffect), "OnParrySwitch")]
    public static class PlayerDeathEffectGuestParryGatePatch
    {
        // Runs before the extra-participant visual patch: on a guest, parrying
        // the ghost of a built-in player must not start a local revive. The
        // host's mirrored revive brings that body back on both screens at once.
        [HarmonyPriority(Priority.High)]
        static bool Prefix(PlayerDeathEffect __instance)
        {
            return !ParticipantReviveController.ShouldBlockGuestGhostParry(__instance);
        }
    }
}
