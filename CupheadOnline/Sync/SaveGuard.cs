using System.Collections.Generic;
using HarmonyLib;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Keeps the session's temporary save-data overrides out of the real save
    /// files.
    ///
    /// To make the game spawn the right weapons and map, the mod writes the
    /// remote player's loadout into PlayerData and (on the guest) mirrors the
    /// host's current map, character flag and map intro state into the guest's
    /// slot. Those values belong to the other player and must never be written
    /// to disk. Before each override the original value is remembered; while
    /// PlayerData serialises a slot the originals are swapped in, and when the
    /// session ends they are restored in memory as well. Real progress made
    /// during the session (levels, coins, unlocks) is still saved normally.
    /// </summary>
    public static class SaveGuard
    {
        sealed class LoadoutCopy
        {
            public Weapon PrimaryWeapon;
            public Weapon SecondaryWeapon;
            public Super Super;
            public Charm Charm;

            public static LoadoutCopy From(PlayerData.PlayerLoadouts.PlayerLoadout loadout)
            {
                return new LoadoutCopy
                {
                    PrimaryWeapon = loadout.primaryWeapon,
                    SecondaryWeapon = loadout.secondaryWeapon,
                    Super = loadout.super,
                    Charm = loadout.charm,
                };
            }

            public void ApplyTo(PlayerData.PlayerLoadouts.PlayerLoadout loadout)
            {
                loadout.primaryWeapon = PrimaryWeapon;
                loadout.secondaryWeapon = SecondaryWeapon;
                loadout.super = Super;
                loadout.charm = Charm;
            }
        }

        sealed class SlotOriginals
        {
            public readonly Dictionary<PlayerId, LoadoutCopy> Loadouts = new Dictionary<PlayerId, LoadoutCopy>(2);
            public bool HasMeta;
            public Scenes CurrentMap;
            public bool IsPlayer1Mugman;
            public readonly Dictionary<Scenes, bool> SessionStarted = new Dictionary<Scenes, bool>(2);
        }

        sealed class SwapState
        {
            public int Slot;
            public SlotOriginals Live;
        }

        static readonly Dictionary<int, SlotOriginals> _originals = new Dictionary<int, SlotOriginals>(3);

        static SaveGuard()
        {
            MultiplayerSession.OnSessionEnded += RestoreAndClear;
        }

        /// <summary>Call before overwriting a player's loadout in the current slot.</summary>
        public static void RememberLoadout(PlayerId id)
        {
            if (!MultiplayerSession.IsActive)
                return;

            try
            {
                var data = PlayerData.Data;
                if (data == null || data.Loadouts == null)
                    return;

                var originals = GetOrCreate(PlayerData.CurrentSaveFileIndex);
                if (!originals.Loadouts.ContainsKey(id))
                    originals.Loadouts[id] = LoadoutCopy.From(data.Loadouts.GetPlayerLoadout(id));
            }
            catch
            {
            }
        }

        /// <summary>Call before mirroring host slot metadata into a local slot.</summary>
        public static void RememberSlotMeta(int slot, Scenes mapScene)
        {
            if (!MultiplayerSession.IsActive)
                return;

            try
            {
                var data = PlayerData.GetDataForSlot(slot);
                if (data == null)
                    return;

                var originals = GetOrCreate(slot);
                if (!originals.HasMeta)
                {
                    originals.HasMeta = true;
                    originals.CurrentMap = data.CurrentMap;
                    originals.IsPlayer1Mugman = data.isPlayer1Mugman;
                }

                if (!originals.SessionStarted.ContainsKey(mapScene))
                {
                    var mapData = data.GetMapData(mapScene);
                    if (mapData != null)
                        originals.SessionStarted[mapScene] = mapData.sessionStarted;
                }
            }
            catch
            {
            }
        }

        static SlotOriginals GetOrCreate(int slot)
        {
            SlotOriginals originals;
            if (!_originals.TryGetValue(slot, out originals))
            {
                originals = new SlotOriginals();
                _originals[slot] = originals;
            }
            return originals;
        }

        /// <summary>
        /// Writes <paramref name="source"/> into the slot and returns the values
        /// that were live before, so they can be put back afterwards.
        /// </summary>
        static SlotOriginals Swap(int slot, SlotOriginals source)
        {
            var data = PlayerData.GetDataForSlot(slot);
            if (data == null)
                return null;

            var previous = new SlotOriginals();

            if (data.Loadouts != null)
            {
                foreach (var entry in source.Loadouts)
                {
                    var loadout = data.Loadouts.GetPlayerLoadout(entry.Key);
                    previous.Loadouts[entry.Key] = LoadoutCopy.From(loadout);
                    entry.Value.ApplyTo(loadout);
                }
            }

            if (source.HasMeta)
            {
                previous.HasMeta = true;
                previous.CurrentMap = data.CurrentMap;
                previous.IsPlayer1Mugman = data.isPlayer1Mugman;
                data.CurrentMap = source.CurrentMap;
                data.isPlayer1Mugman = source.IsPlayer1Mugman;
            }

            foreach (var entry in source.SessionStarted)
            {
                var mapData = data.GetMapData(entry.Key);
                if (mapData == null)
                    continue;

                previous.SessionStarted[entry.Key] = mapData.sessionStarted;
                mapData.sessionStarted = entry.Value;
            }

            return previous;
        }

        static void RestoreAndClear()
        {
            foreach (var entry in _originals)
            {
                try { Swap(entry.Key, entry.Value); }
                catch { }
            }

            if (_originals.Count > 0)
                Plugin.Log.LogInfo("[SaveGuard] Restored the local save data that the session had mirrored.");

            _originals.Clear();
        }

        [HarmonyPatch(typeof(PlayerData), "Save", typeof(int))]
        public static class PlayerDataSavePatch
        {
            static void Prefix(int fileIndex, ref SwapState __state)
            {
                __state = null;

                SlotOriginals originals;
                if (!_originals.TryGetValue(fileIndex, out originals))
                    return;

                try
                {
                    var live = Swap(fileIndex, originals);
                    if (live != null)
                        __state = new SwapState { Slot = fileIndex, Live = live };
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning("[SaveGuard] Could not swap originals before save: " + ex.Message);
                }
            }

            // PlayerData.Save serialises synchronously (JsonUtility.ToJson)
            // before handing the string to the cloud layer, so the live session
            // values can be put back right after the call.
            static void Postfix(SwapState __state)
            {
                if (__state == null)
                    return;

                try { Swap(__state.Slot, __state.Live); }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning("[SaveGuard] Could not restore session values after save: " + ex.Message);
                }
            }
        }
    }
}
