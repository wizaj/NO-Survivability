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
        public const string PluginVersion = "0.4.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> RcsEnabled;
        internal static ConfigEntry<float> RcsFloor;
        internal static ConfigEntry<bool> DamageImmunity;
        internal static ConfigEntry<bool> RequireServerAuthority;
        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<float> ToastSeconds;

        internal static bool RuntimeEnabled = true;
        internal static bool KeybindPatched;

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

            ToggleKey = Config.Bind("Keybinds", "Toggle", KeyCode.F10,
                "Key that toggles all effects at runtime. Accepts any UnityEngine.KeyCode " +
                "name; rebind here or via ConfigurationManager. None disables the keybind. " +
                "Only registers while flying (hooked into the game's pilot input handler).");

            ToastSeconds = Config.Bind("UI", "ToastSeconds", 2.5f,
                new ConfigDescription(
                    "How long the on-screen ENABLED/DISABLED indicator stays visible " +
                    "after toggling or entering an aircraft. 0 disables the indicator.",
                    new AcceptableValueRange<float>(0f, 10f)));

            var harmony = new Harmony(PluginGuid);
            DamagePatcher.ApplyAll(harmony);
            KeybindPatcher.Apply(harmony);

            var host = new GameObject("NOSoloSurvivability_Host");
            host.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(host);
            host.AddComponent<RcsDriver>();

            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Toggle: {ToggleKey.Value}");
        }

        internal static void ToggleRuntime()
        {
            RuntimeEnabled = !RuntimeEnabled;
            Log.LogInfo($"{PluginName} {(RuntimeEnabled ? "ENABLED" : "DISABLED")}");
            RcsDriver.Instance?.OnToggled();
        }
    }

    /// <summary>
    /// Routes the toggle key through the game's own pilot input handler
    /// (pattern from Modzer0/DefensiveAutoTarget): the key only registers
    /// while the player is actually flying, so it cannot fire while typing
    /// in chat or navigating menus. If the hook can't be resolved,
    /// RcsDriver.Update falls back to always-on polling.
    /// </summary>
    internal static class KeybindPatcher
    {
        public static void Apply(Harmony harmony)
        {
            Type owner = AccessTools.TypeByName("PilotPlayerState");
            MethodInfo target = owner == null ? null : AccessTools.Method(owner, "PlayerControls");
            if (target == null)
            {
                Plugin.Log.LogWarning(
                    "PilotPlayerState.PlayerControls not found; toggle key falls back " +
                    "to always-on polling (will also fire outside the cockpit).");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(KeybindPatcher), nameof(PlayerControlsPostfix))));
            Plugin.KeybindPatched = true;
            Plugin.Log.LogInfo("Toggle key hooked into PilotPlayerState.PlayerControls.");
        }

        private static void PlayerControlsPostfix()
        {
            KeyCode key = Plugin.ToggleKey.Value;
            if (key == KeyCode.None || !Input.GetKeyDown(key)) return;
            Plugin.ToggleRuntime();
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
    /// Patches every concrete implementation of the IDamageable damage surface:
    /// TakeDamage (server-side armour resolution), ApplyDamage (direct hitPoints
    /// write, reached via Unit.RpcDamage) and TakeShockwave (blast overpressure
    /// from near misses). Patching only TakeDamage is not enough — v0.3.0 field
    /// testing showed damage leaking through the other two, plus two paths that
    /// bypass IDamageable entirely (physics tearing and engine self-damage),
    /// which get targeted patches below.
    ///
    /// Harmony does not intercept overrides when you patch a base method, so
    /// this discovers implementors at load time and patches whatever each type
    /// declares itself.
    /// </summary>
    internal static class DamagePatcher
    {
        private static readonly string[] BlockedNames = { "TakeDamage", "ApplyDamage", "TakeShockwave" };

        private static readonly Type[][] BlockedSignatures =
        {
            new[] { typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(PersistentID) },
            new[] { typeof(float), typeof(float), typeof(float), typeof(float) },
            new[] { typeof(Vector3), typeof(float), typeof(float) },
        };

        public static void ApplyAll(Harmony harmony)
        {
            var prefix = new HarmonyMethod(
                AccessTools.Method(typeof(DamagePatcher), nameof(BlockDamagePrefix)));

            int patched = 0;
            foreach (Type t in FindDamageableTypes())
            {
                var done = new List<string>();
                for (int i = 0; i < BlockedNames.Length; i++)
                {
                    MethodInfo m = AccessTools.Method(t, BlockedNames[i], BlockedSignatures[i]);
                    if (m == null || m.IsAbstract) continue;

                    // Skip inherited methods: patching the declaring type once is
                    // enough, and patching the same MethodInfo twice throws.
                    if (m.DeclaringType != t) continue;

                    try
                    {
                        harmony.Patch(m, prefix: prefix);
                        done.Add(BlockedNames[i]);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"Could not patch {t.FullName}.{BlockedNames[i]}: {e.Message}");
                    }
                }

                if (done.Count > 0)
                {
                    patched++;
                    Plugin.Log.LogInfo($"Patched {t.FullName}: {string.Join(", ", done.ToArray())}");
                }
            }

            if (patched == 0)
                Plugin.Log.LogError(
                    "No damage implementations patched. The signatures have likely " +
                    "changed. Damage immunity will not work.");

            // Damage that never crosses the IDamageable surface:
            // parts physically torn off by excess force (the tailboom failure
            // mode observed in v0.3.0 testing), and turbofans grinding
            // themselves down in FixedUpdate after ingesting debris.
            PatchGuard(harmony, prefix, "AeroPart", "CheckAttachment", "physics structural tearing");
            PatchGuard(harmony, prefix, "Turbofan", "InvokeDamage", "engine self-damage");
        }

        private static void PatchGuard(Harmony harmony, HarmonyMethod prefix, string typeName, string methodName, string what)
        {
            Type t = AccessTools.TypeByName(typeName);
            MethodInfo m = t == null ? null : AccessTools.Method(t, methodName, Type.EmptyTypes);
            if (m == null)
            {
                Plugin.Log.LogWarning($"{typeName}.{methodName} not found — {what} will NOT be blocked.");
                return;
            }

            try
            {
                harmony.Patch(m, prefix: prefix);
                Plugin.Log.LogInfo($"Patched {typeName}.{methodName} ({what})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not patch {typeName}.{methodName}: {e.Message}");
            }
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

        /// <summary>
        /// IDamageable ships its own owner accessor, GetUnit() — discovered via
        /// Cecil after v0.3.0's reflective field search misattributed types
        /// whose owner reference lives on a base class (e.g. MountedCargo's
        /// attachedUnit is declared on Weapon). Fallbacks cover the two guard
        /// patches on types reached before full interface dispatch is safe.
        /// </summary>
        private static Unit ResolveOwner(object instance)
        {
            if (instance == null) return null;

            if (instance is IDamageable d)
            {
                try { return d.GetUnit(); }
                catch (NullReferenceException) { return null; }
            }

            if (instance is Unit direct) return direct;

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
        internal static RcsDriver Instance;

        private Aircraft cached;
        private Traverse accessor;
        private float original = -1f;
        private bool warned;

        private string toastText;
        private float toastUntil;
        private GUIStyle toastStyle;

        private void Awake() => Instance = this;

        /// <summary>Fallback path only: KeybindPatcher normally owns the key.</summary>
        private void Update()
        {
            if (Plugin.KeybindPatched) return;

            KeyCode key = Plugin.ToggleKey.Value;
            if (key == KeyCode.None || !Input.GetKeyDown(key)) return;
            Plugin.ToggleRuntime();
        }

        public void OnToggled()
        {
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

        private void OnDestroy()
        {
            Restore();
            if (Instance == this) Instance = null;
        }
    }
}
