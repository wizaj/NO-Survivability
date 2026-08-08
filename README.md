# NO-Survivability

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin for **Nuclear Option**
that makes the local player's aircraft survivable in singleplayer.

## What it does

- **RCS reduction** — clamps the player aircraft's radar cross section to a
  negligible value, so radar-guided seekers and ground radar cannot generate a
  usable return.
- **Damage immunity** — blocks all damage applied to parts of the player
  aircraft: pierce, blast, fire, impact and collision.

Both are independently toggleable in config, and `F10` toggles everything at
runtime.

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
| Keybinds | `Toggle` | `F10` | |
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

Early. See [CLAUDE.md](CLAUDE.md) for the recovered game API reference and the
list of things still unverified.

## Credits

Game API details were recovered by reading these open-source mods:

- [`pauel3312/NOKillWeapons`](https://github.com/pauel3312/NOKillWeapons) — damage model
- [`clumzy/NO_Tactitools`](https://github.com/clumzy/NO_Tactitools) — player aircraft access patterns
- [`Modzer0/NuclearOption-ActiveDecoy`](https://github.com/Modzer0/NuclearOption-ActiveDecoy) — radar/RCS behaviour
