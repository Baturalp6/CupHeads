using HarmonyLib;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Tutorial parry-sphere chain: the host advances it and replicates each
    /// advance; a guest only advances when replaying the host's event.
    /// </summary>
    [HarmonyPatch(typeof(TutorialLevelParryNext), "SetNextParry")]
    public static class TutorialParryNextPatch
    {
        static bool Prefix()
        {
            return WorldEventSync.MayRunLocally;
        }

        static void Postfix(TutorialLevelParryNext __instance)
        {
            if (__instance != null)
                WorldEventSync.Broadcast(WorldEventKind.TutorialParryAdvance, __instance.gameObject);
        }
    }

    [HarmonyPatch(typeof(TutorialShmupLevelParryNext), "SetNextParry")]
    public static class TutorialShmupParryNextPatch
    {
        static bool Prefix()
        {
            return WorldEventSync.MayRunLocally;
        }

        static void Postfix(TutorialShmupLevelParryNext __instance)
        {
            if (__instance != null)
                WorldEventSync.Broadcast(WorldEventKind.TutorialShmupParryAdvance, __instance.gameObject);
        }
    }
}
