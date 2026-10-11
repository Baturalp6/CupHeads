using System;
using System.Reflection;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Cached delegates for the motors' non-public property setters. Proxy
    /// players update these every physics tick, so a one-time delegate bind
    /// replaces per-tick Traverse/reflection lookups and boxing.
    /// </summary>
    internal static class MotorSetters
    {
        internal static readonly Action<LevelPlayerMotor, Trilean2> LookDirection =
            Bind<LevelPlayerMotor, Trilean2>("LookDirection");
        internal static readonly Action<LevelPlayerMotor, Trilean2> TrueLookDirection =
            Bind<LevelPlayerMotor, Trilean2>("TrueLookDirection");
        internal static readonly Action<LevelPlayerMotor, Trilean2> MoveDirection =
            Bind<LevelPlayerMotor, Trilean2>("MoveDirection");
        internal static readonly Action<LevelPlayerMotor, bool> Grounded =
            Bind<LevelPlayerMotor, bool>("Grounded");
        internal static readonly Action<LevelPlayerMotor, bool> Locked =
            Bind<LevelPlayerMotor, bool>("Locked");
        internal static readonly Action<LevelPlayerMotor, bool> GravityReversed =
            Bind<LevelPlayerMotor, bool>("GravityReversed");

        internal static readonly Action<PlanePlayerMotor, Trilean2> PlaneMoveDirection =
            Bind<PlanePlayerMotor, Trilean2>("MoveDirection");

        internal static void Set<TOwner, TValue>(Action<TOwner, TValue> setter, TOwner owner, TValue value)
        {
            if (setter != null && owner != null)
                setter(owner, value);
        }

        static Action<TOwner, TValue> Bind<TOwner, TValue>(string propertyName)
        {
            try
            {
                var property = typeof(TOwner).GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var setter = property != null ? property.GetSetMethod(true) : null;
                if (setter == null || property.PropertyType != typeof(TValue))
                {
                    LogMissing(typeof(TOwner).Name + "." + propertyName);
                    return null;
                }

                return (Action<TOwner, TValue>)Delegate.CreateDelegate(typeof(Action<TOwner, TValue>), setter);
            }
            catch (Exception ex)
            {
                LogMissing(typeof(TOwner).Name + "." + propertyName + " (" + ex.Message + ")");
                return null;
            }
        }

        static void LogMissing(string name)
        {
            if (Plugin.Log != null)
                Plugin.Log.LogWarning("[MotorSetters] No setter for " + name + "; proxy sync for it is disabled.");
        }
    }
}
