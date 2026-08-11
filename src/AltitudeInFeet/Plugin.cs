using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace AltitudeInFeet
{
    /// <summary>
    /// Mixed units of measure: leaves the game's Metric setting alone (km for
    /// distance, m/s for airspeed, kg, and so on) but renders altitude — and
    /// optionally climb rate — the way the Imperial setting would.
    ///
    /// Every altitude string in the game funnels through the static
    /// UnitConverter.AltitudeReading(float); climb rate likewise through
    /// ClimbRateReading(float). The prefixes below reproduce the Imperial
    /// branches of those methods verbatim (formats and the 3.28084 factor
    /// taken from the shipping assembly's IL), so output is pixel-identical
    /// to what Imperial mode shows for those two readouts.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.altitudeinfeet";
        public const string PluginName = "Altitude In Feet";
        public const string PluginVersion = "0.1.0";

        private const float MetresToFeet = 3.28084f;

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ConvertAltitude;
        internal static ConfigEntry<bool> ConvertClimbRate;

        private void Awake()
        {
            Log = Logger;

            ConvertAltitude = Config.Bind("Conversions", "Altitude", true,
                "Show altitude in feet even when the game is set to Metric.");

            ConvertClimbRate = Config.Bind("Conversions", "ClimbRate", true,
                "Show climb rate in feet per minute even when the game is set to Metric.");

            var harmony = new Harmony(PluginGuid);
            PatchReading(harmony, "AltitudeReading", nameof(AltitudePrefix));
            PatchReading(harmony, "ClimbRateReading", nameof(ClimbRatePrefix));

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void PatchReading(Harmony harmony, string methodName, string prefixName)
        {
            MethodInfo target = AccessTools.Method(typeof(UnitConverter), methodName, new[] { typeof(float) });
            if (target == null)
            {
                Log.LogError($"UnitConverter.{methodName}(float) not found — that readout will stay metric.");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), prefixName)));
            Log.LogInfo($"Patched UnitConverter.{methodName}");
        }

        private static bool AltitudePrefix(float altitude, ref string __result)
        {
            if (!ConvertAltitude.Value) return true;
            if (PlayerSettings.unitSystem != PlayerSettings.UnitSystem.Metric) return true;

            __result = string.Format("{0:F0}ft", altitude * MetresToFeet);
            return false;
        }

        private static bool ClimbRatePrefix(float speed, ref string __result)
        {
            if (!ConvertClimbRate.Value) return true;
            if (PlayerSettings.unitSystem != PlayerSettings.UnitSystem.Metric) return true;

            string sign = speed > 0.5f ? "+" : "";
            __result = string.Format("{0}{1:F0}fpm", sign, speed * 60f * MetresToFeet);
            return false;
        }
    }
}
