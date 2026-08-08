# NO-Survivability — working context

A BepInEx plugin for **Nuclear Option** that makes the local player's aircraft
survivable in singleplayer: near-zero radar cross section plus full damage
immunity.

## Status

`v0.2.0`. **Compiles clean against the shipping assembly (2026-08-08); not
yet run in game.** The code was originally written against game API signatures
recovered by reading other open-source mods. First build resolved every
recovered signature — the only fix needed was referencing `Mirage.dll`
(the game's networking library): `Unit` derives from Mirage's
`NetworkBehaviour`, which is where `IsServer` is declared.

## Build

Requires the game's managed assemblies, which are not in this repo.

```
dotnet build src/NOSurvivability/NOSurvivability.csproj -c Release
```

Override the install path if needed:

```
dotnet build src/NOSurvivability/NOSurvivability.csproj -c Release -p:GameDir="D:\Games\Nuclear Option"
```

Output DLL goes to `BepInEx/plugins/` in the game directory.

## Game API reference

All of the below was recovered from `pauel3312/NOKillWeapons`, which
reimplements the game's damage functions, and `clumzy/NO_Tactitools` +
`Modzer0/NuclearOption-ActiveDecoy` for HUD and radar. Treat it as accurate
but unverified against the shipping assembly.

### Damage model

The chokepoint is:

```csharp
IDamageable.TakeDamage(
    float pierceDamage,
    float blastDamage,
    float amountAffected,
    float fireDamage,
    float impactDamage,
    PersistentID dealerID)
```

Implemented by at least `UnitPart`, `Turbofan`, `Missile`, `MountedCargo`.
The game's own type switch tests `Turbofan` **before** `UnitPart`, implying
`Turbofan` overrides rather than inherits — so patching the base method alone
is not sufficient. `DamagePatcher` discovers implementors reflectively for
this reason.

Damage resolution inside `UnitPart.TakeDamage`:

```
pierceAfterArmour = max(pierce - armorProperties.pierceArmor, 0) / max(armorProperties.pierceTolerance, 0.01)
blastAfterArmour  = blast * amountAffected / max(armorProperties.blastTolerance, 0.01)
fireAfterArmour   = max(fire - armorProperties.fireArmor, 0) / max(armorProperties.fireTolerance, 0.01)
damageAmount      = pierceAfterArmour + blastAfterArmour + fireAfterArmour + impactDamage
```

Kill condition: `part.criticalPart && part.hitPoints - damageAmount <= 0`
→ sets `part.parentUnit.Networkdisabled = true` and calls
`part.parentUnit.ReportKilled()`.

Part detachment: below `part.structuralThreshold`, unless `part is AeroPart`
or `part.attachInfo.detachedFromParentPart` is already set.

Guard at the top of the method: `if (!part.parentUnit.IsServer)` logs a
warning and does nothing. **Damage is server-authoritative.** This is why
`RequireServerAuthority` exists in config — on a dedicated server the prefix
is a genuine no-op, not merely discouraged.

Do **not** try to achieve immunity by inflating `armorProperties`. Those look
like shared per-airframe definition objects, so mutating them would buff enemy
aircraft of the same type. Also `blastAfterArmour` has no armour subtraction
term — only tolerance division — so blast can never be fully negated that way.

Other confirmed members: `Unit.RecordDamage(PersistentID, float, string)`,
`Unit.RegisterHit`, `Unit.HitOnPhysicsFrame` (async — patch the state
machine's `MoveNext`), `Unit.RpcDamage(int partId, DamageInfo)`,
`Unit.DetachPart`, `Unit.damageCredit`, `Unit.persistentID`,
`Unit.SavedUnit`, `UnitPart.parentUnit`, `UnitPart.id`,
`UnitPart.attachInfo`, `SavedScenery.indestructible`.

### Radar

`RCS` is a member on `Unit`, so `Aircraft` inherits it. Seeker signal:

```
signal = min(radarParams.maxRange / distance * Pow(RCS, 0.25f), radarParams.maxSignal)
```

The fourth root means halving RCS cuts signal by only ~16%; meaningful
reduction needs orders of magnitude. Clamped to a small positive value rather
than exactly zero because other code compares RCS ratios and could produce
NaN.

Not covered: **IR seekers use a separate path** (`IRSeeker.IRLockCheck`).
Heaters will still lock. Damage immunity covers the outcome, but if lock
denial matters, see `Modzer0/BetterIR`.

### Player aircraft reference

```csharp
SceneSingleton<CombatHUD>.i.aircraft
```

This is the local player's own aircraft — keying off it scopes effects to the
player rather than all aircraft. Pattern taken from `NO_Tactitools`.
Also useful: `aircraft.GetAircraftParameters().aircraftName`,
`aircraft.definition.unitName`, `aircraft.definition.jsonKey`,
`aircraft.radar.activated`, `aircraft.NetworkHQ`, `aircraft.rb`.

## Known unknowns

1. ~~`PersistentID` namespace.~~ **Resolved:** it lives in the global
   namespace; compiles bare with no extra `using`.
2. Whether `RCS` is a field or property. `RcsDriver` tries both via Traverse
   and logs which it resolved. Still unverified until first run.
3. ~~Where `IsServer` is declared.~~ **Resolved:** on Mirage's
   `NetworkBehaviour`, which `Unit` derives from — hence the `Mirage.dll`
   reference in the csproj.
4. Game version targeting. Reference mods span 0.32–0.33.4. Local install
   build hash at first successful compile: `211b5aad0ca1`.

## Verification once it builds

Watch `BepInEx/LogOutput.log` on load for:

- One `Patched <type>.TakeDamage` line per implementor. Zero lines means the
  signature is wrong and immunity is silently inert.
- `Player aircraft acquired. Stock RCS = ...` on mission entry. Record the
  stock value per airframe; useful for tuning the floor.

`F10` toggles at runtime, which makes A/B testing in one sortie easy.

## Scope

Singleplayer tool. Nuclear Option ships **NOSMR** (`RaylaValdez/NOSMR`), a
mod-reporting system that the NOMM mod manager installs and protects from
removal; NOMM also builds a SHA-256 hash index of installed mods for the
server browser. Installed mods are visible to servers. Keep
`RequireServerAuthority = true`.
