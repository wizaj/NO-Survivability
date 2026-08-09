# NO-Survivability

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin for **Nuclear Option**
that makes the local player's aircraft survivable in singleplayer.

## What it does

- **RCS reduction** — clamps the player aircraft's radar cross section to a
  negligible value, so radar-guided seekers and ground radar cannot generate a
  usable return.
- **Damage immunity** — blocks all damage applied to parts of the player
  aircraft: pierce, blast, fire, impact and collision, plus blast shockwaves,
  physics-driven structural tearing, and engine debris ingestion.

Both are independently toggleable in config, and a rebindable key (`F10` by
default) toggles everything at runtime, with a brief on-screen indicator.
The toggle is routed through the game's pilot input handler, so it only
registers while you're flying — never while typing in chat or in menus.

## Scope

This is a singleplayer tool. Damage in Nuclear Option is server-authoritative,
so the damage block is a no-op on a dedicated server by design — the
`RequireServerAuthority` setting enforces this and defaults to on. Note also
that the NOMM mod manager ships a mod-reporting component, so installed mods
are visible to servers.

## Configuration

Written to `BepInEx/config/local.nosolosurvivability.cfg` on first run.

| Section | Key | Default | Notes |
|---|---|---|---|
| RCS | `Enabled` | `true` | |
| RCS | `Floor` | `0.0001` | Not exactly zero — avoids NaN in ratio comparisons |
| Damage | `Enabled` | `true` | |
| Safety | `RequireServerAuthority` | `true` | Leave on |
| Keybinds | `Toggle` | `F10` | Any [`KeyCode`](https://docs.unity3d.com/ScriptReference/KeyCode.html) name; `None` disables. Rebind in the cfg or via ConfigurationManager |
| UI | `ToastSeconds` | `2.5` | On-screen ENABLED/DISABLED indicator duration; `0` hides it |

## Building

Requires the game's managed assemblies from your own install; they are not
redistributed here.

```
dotnet build src/NOSurvivability/NOSurvivability.csproj -c Release
```

Pass `-p:GameDir="<path>"` if your install is not at the default Windows Steam
location. Copy the resulting DLL into `BepInEx/plugins/`.

## Status

**v0.4.0 — tested and working in singleplayer** (game build `211b5aad0ca1`).
Field testing of earlier versions showed damage leaking through paths beyond
`TakeDamage`; v0.4.0 blocks the full damage surface — direct/RPC damage,
blast shockwaves, physics structural tearing, and engine debris self-damage —
and survives sustained missile fire. RCS clamp, rebindable toggle, and
on-screen indicator all confirmed in-flight. See [CLAUDE.md](CLAUDE.md) for
the verified game API reference.

## Credits

Game API details were recovered by reading these open-source mods:

- [`pauel3312/NOKillWeapons`](https://github.com/pauel3312/NOKillWeapons) — damage model
- [`clumzy/NO_Tactitools`](https://github.com/clumzy/NO_Tactitools) — player aircraft access patterns
- [`Modzer0/NuclearOption-ActiveDecoy`](https://github.com/Modzer0/NuclearOption-ActiveDecoy) — radar/RCS behaviour
