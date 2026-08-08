using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NOSoloSurvivability
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.nosolosurvivability";
        public const string PluginName = "Solo Survivability";
        public const string PluginVersion = "0.2.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> RcsEnabled;
        internal static ConfigEntry<float> RcsFloor;
        internal static ConfigEntry<bool> DamageImmunity;
        internal static ConfigEntry<bool> RequireServerAuthority;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<float> ToastSeconds;

        internal static bool RuntimeEnabled = true;

        private void Awake()
        {
            Log = Logger;

            RcsEnabled = Config.Bind("RCS", "Enabled", true,
                "Collapse the player aircraft's radar cross section.");

            RcsFloor = Config.Bind("RCS", "Floor", 0.0001f,
                new ConfigDescription(
                    "Value the player RCS is clamped to. Not exactly zero: seeker code uses " +
                    "Pow(RCS, 0.25) and other systems compare ratios of it, so a tiny positive " +
                    "value avoids NaN propagation while producing a negligible return.",
                    new AcceptableValueRange<float>(0.0000001f, 1f)));

            DamageImmunity = Config.Bind("Damage", "Enabled", true,
                "Block all damage applied to parts of the player aircraft.");

            RequireServerAuthority = Config.Bind("Safety", "RequireServerAuthority", true,
                "Only block damage when this client owns the unit's server authority. " +
                "On a dedicated server this makes the mod inert, because damage is " +
                "resolved server-side and the block would not apply anyway.");

            ToggleKey = Config.Bind("Keybinds", "Toggle", new KeyboardShortcut(KeyCode.F10),
                "Toggles all effects on/off at runtime.");

            ToastSeconds = Config.Bind("UI", "ToastSeconds", 2.5f,
                new ConfigDescription(
                    "How long the on-screen ENABLED/DISABLED indicator stays visible " +
                    "after toggling or entering an aircraft. 0 disables the indicator.",
                    new AcceptableValueRange<float>(0f, 10f)));

            var harmony = new Harmony(PluginGuid);
            DamagePatcher.ApplyAll(harmony);

            var host = new GameObject("NOSoloSurvivability_Host");
            host.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(host);
            host.AddComponent<RcsDriver>();

            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Toggle: {ToggleKey.Value}");
        }
    }

    /// <summary>
    /// Resolves the aircraft the local player is flying. CombatHUD owns the
    /// reference to the player's own aircraft, so keying off it affects only
    /// your jet rather than every aircraft in the mission.
    /// </summary>
    internal static class PlayerRef
    {
        public static Aircraft Get()
        {
            try { return SceneSingleton<CombatHUD>.i?.aircraft; }
            catch (NullReferenceException) { return null; }
        }

        public static bool IsPlayerUnit(Unit unit)
        {
            if (unit == null) return false;
            Aircraft player = Get();
            return player != null && ReferenceEquals(unit, player);
        }
    }

    /// <summary>
    /// Patches every concrete implementation of IDamageable.TakeDamage.
    ///
    /// Harmony does not intercept overrides when you patch a base method, and
    /// Turbofan is matched before UnitPart in the game's own type switch, which
    /// implies it overrides rather than inherits. Rather than hardcoding a list
    /// that breaks on the next game update, this discovers implementors at load
    /// time and patches each one it finds.
    /// </summary>
    internal static class DamagePatcher
    {
        private static readonly Type[] TakeDamageSignature =
        {
            typeof(float),          // pierceDamage
            typeof(float),          // blastDamage
            typeof(float),          // amountAffected
            typeof(float),          // fireDamage
            typeof(float),          // impactDamage
            typeof(PersistentID)    // dealerID
        };

        public static void ApplyAll(Harmony harmony)
        {
            var prefix = new HarmonyMethod(
                AccessTools.Method(typeof(DamagePatcher), nameof(BlockDamagePrefix)));

            int patched = 0;
            foreach (Type t in FindDamageableTypes())
            {
                MethodInfo m = AccessTools.Method(t, "TakeDamage", TakeDamageSignature);
                if (m == null || m.IsAbstract) continue;

                // Skip inherited methods: patching the declaring type once is enough,
                // and patching the same MethodInfo twice throws.
                if (m.DeclaringType != t) continue;

                try
                {
                    harmony.Patch(m, prefix: prefix);
                    patched++;
                    Plugin.Log.LogInfo($"Patched {t.FullName}.TakeDamage");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Could not patch {t.FullName}.TakeDamage: {e.Message}");
                }
            }

            if (patched == 0)
                Plugin.Log.LogError(
                    "No TakeDamage implementations patched. The signature has likely " +
                    "changed. Damage immunity will not work.");
        }

        private static IEnumerable<Type> FindDamageableTypes()
        {
            Type iface = AccessTools.TypeByName("IDamageable");
            if (iface == null)
            {
                Plugin.Log.LogError("IDamageable not found.");
                return Enumerable.Empty<Type>();
            }

            return AccessTools.GetTypesFromAssembly(iface.Assembly)
                .Where(t => t != null
                            && !t.IsInterface
                            && !t.IsAbstract
                            && iface.IsAssignableFrom(t));
        }

        /// <summary>
        /// Returning false skips the original method entirely, so no damage is
        /// recorded, no part detaches, Networkdisabled is never set, and
        /// ReportKilled is never called.
        /// </summary>
        private static bool BlockDamagePrefix(object __instance)
        {
            if (!Plugin.RuntimeEnabled) return true;
            if (!Plugin.DamageImmunity.Value) return true;

            Unit owner = ResolveOwner(__instance);
            if (owner == null) return true;
            if (!PlayerRef.IsPlayerUnit(owner)) return true;

            if (Plugin.RequireServerAuthority.Value && !owner.IsServer)
                return true;

            return false;
        }

        private static readonly Dictionary<Type, FieldInfo> ParentCache = new Dictionary<Type, FieldInfo>();

        /// <summary>
        /// UnitPart exposes parentUnit. Other implementors may name it
        /// differently, so fall back to a cached reflective search for any
        /// Unit-typed member, then to a component lookup.
        /// </summary>
        private static Unit ResolveOwner(object instance)
        {
            if (instance == null) return null;
            if (instance is Unit direct) return direct;

            Type t = instance.GetType();
            if (!ParentCache.TryGetValue(t, out FieldInfo field))
            {
                field = AccessTools.Field(t, "parentUnit")
                        ?? AccessTools.GetDeclaredFields(t)
                            .FirstOrDefault(f => typeof(Unit).IsAssignableFrom(f.FieldType));
                ParentCache[t] = field;
            }

            if (field != null)
                return field.GetValue(instance) as Unit;

            if (instance is Component c)
                return c.GetComponentInParent<Unit>();

            return null;
        }
    }

    /// <summary>
    /// Reapplies the RCS clamp every frame. A one-shot write at spawn is not
    /// enough: RCS may be recalculated when stores separate or gear deploys.
    /// One reflected float write per frame is negligible and survives whichever
    /// code path resets it.
    /// </summary>
    internal class RcsDriver : MonoBehaviour
    {
        private Aircraft cached;
        private Traverse accessor;
        private float original = -1f;
        private bool warned;

        private string toastText;
        private float toastUntil;
        private GUIStyle toastStyle;

        private void Update()
        {
            if (!Plugin.ToggleKey.Value.IsDown()) return;

            Plugin.RuntimeEnabled = !Plugin.RuntimeEnabled;
            Plugin.Log.LogInfo($"Solo Survivability {(Plugin.RuntimeEnabled ? "ENABLED" : "DISABLED")}");
            ShowToast();
            if (!Plugin.RuntimeEnabled) Restore();
        }

        private void ShowToast()
        {
            toastText = $"Solo Survivability: {(Plugin.RuntimeEnabled ? "ENABLED" : "DISABLED")}";
            toastUntil = Time.unscaledTime + Plugin.ToastSeconds.Value;
        }

        private void OnGUI()
        {
            float remaining = toastUntil - Time.unscaledTime;
            if (remaining <= 0f || toastText == null) return;

            if (toastStyle == null)
            {
                toastStyle = new GUIStyle
                {
                    fontSize = 20,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
            }

            float alpha = Mathf.Clamp01(remaining / 0.5f); // fade over the last half second
            var rect = new Rect(0, Screen.height * 0.12f, Screen.width, 30f);
            Color body = Plugin.RuntimeEnabled
                ? new Color(0.4f, 1f, 0.4f, alpha)
                : new Color(1f, 0.55f, 0.2f, alpha);

            toastStyle.normal.textColor = new Color(0f, 0f, 0f, alpha);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), toastText, toastStyle);
            toastStyle.normal.textColor = body;
            GUI.Label(rect, toastText, toastStyle);
        }

        private void LateUpdate()
        {
            if (!Plugin.RuntimeEnabled || !Plugin.RcsEnabled.Value) return;

            Aircraft player = PlayerRef.Get();
            if (player == null)
            {
                cached = null; accessor = null; original = -1f;
                return;
            }

            if (!ReferenceEquals(player, cached))
            {
                cached = player;
                accessor = Traverse.Create(player).Field("RCS");
                if (!accessor.FieldExists())
                    accessor = Traverse.Create(player).Property("RCS");

                if (!accessor.FieldExists() && !accessor.PropertyExists())
                {
                    if (!warned)
                    {
                        Plugin.Log.LogError(
                            "Could not resolve RCS as a field or property on Aircraft. " +
                            "Dump members with RuntimeUnityEditor and update the name.");
                        warned = true;
                    }
                    accessor = null;
                    return;
                }

                original = accessor.GetValue<float>();
                Plugin.Log.LogInfo($"Player aircraft acquired. Stock RCS = {original:F4}, " +
                                   $"clamping to {Plugin.RcsFloor.Value}");
                ShowToast();
            }

            if (accessor == null) return;

            float target = Plugin.RcsFloor.Value;
            if (Mathf.Abs(accessor.GetValue<float>() - target) > float.Epsilon)
                accessor.SetValue(target);
        }

        private void Restore()
        {
            if (accessor != null && original >= 0f) accessor.SetValue(original);
        }

        private void OnDestroy() => Restore();
    }
}
