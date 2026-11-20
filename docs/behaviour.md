# Behaviour and code

State: **phase 1 built (2026-11-19), not tested in game.** The full plan, physics and decisions are in
[../../docs/water-impact-plan.md](../../docs/water-impact-plan.md).

## What phase 1 does

Every missile-type round is tracked. When one goes from above the surface to below it, its ammo decides:

| Ammo | Mode | What happens |
|---|---|---|
| `Flare`, `Firework*` | Dud | Removed at the entry point, small splash, no explosion |
| Explosive, no HealthPool (`Missile`, brain-guided missiles, `IDMissile`, `IronDomeXMissile`, ...) | Detonate | Explodes at the entry point with its own damage |
| Kinetic (HealthPool > 0) entering faster than 1 100 m/s (`SmallRailgunSlug`, `LargeRailgunSlug`) | Detonate | Shatters at the surface (its explosion damage is 0 in vanilla, so only a splash and a small blast) |
| Other kinetic (`MediumCalibreShell`, `LargeCalibreShell`) | Penetrate | **Phase 2, not built:** for now the shell keeps flying as before, with a splash |
| No HealthPool and no explosion damage | Dud | Removed |

- **Only on entry from above.** Rounds fired under water (SLBM `AMissile`, `LauncherXMissile`) are ignored until they
  surface; if one falls back into the sea, it detonates.
- The entry point is interpolated between the last two checks from the Water Mod's signed depth, since a large
  railgun slug moves 33 m per tick.
- Splash radius grows with the cube root of the kinetic energy: about 2 m for a rocket, 9 m for artillery, 25 m (cap)
  for a large railgun. The Water Mod already restyles the explosion itself as a water blast.
- The first time each ammo subtype hits water, the log gets `[WaterImpact] <subtype> hit water at <speed> m/s: <mode>`.
  Search the newest `SpaceEngineers_*.log` for `[WaterImpact]` when testing.
- Values are constants in `ImpactSettings` for now (break-up speed 1 100 m/s); phase 3 moves them to a world config.

## How detonation works

The game explodes a missile when its distance from `Origin` reaches `MaxTrajectory`. On entry the mod moves the round to
the entry point, sets its velocity to zero and `MaxTrajectory` to the distance travelled. The game then explodes it on
the next ticks. Until it does, the mod keeps zeroing its velocity (before simulation) and puts it back on the entry
point (after simulation), because the brain's guidance or physics may move it. A round that has not exploded after
10 ticks is removed instead.

## Code (`Mod/Data/Scripts/WaterImpact/`)

| File | What |
|---|---|
| `WaterImpactSession.cs` | Session component. Tracks missiles (`OnMissileAdded`/`Removed`), checks water after simulation, applies the outcome, sends and receives the packet |
| `ImpactModel.cs` | Pure rules and maths: `ImpactMode`, `ImpactSettings`, mode resolution, entry point, splash size, check scheduling |
| `Network/WaterEntryPacket.cs`, `Network/RegisterPackets.cs` | The one packet and its registration |
| `Network/NetworkLib/` | THDigi's NetworkLib, namespace changed to `WaterImpact.NetworkLib`, otherwise unedited |
| `APIs/WaterModAPI.cs` | Jakaria's Water Mod API (same copy as the brain's) |

Checks are cheap far from water: a round high above the sea is checked again after half the time it needs to reach
the surface (1 to 30 ticks); with no water near, every 10 ticks.

## Multiplayer

- Missile entity IDs are the same on the server and every client, so the packet names the round by ID.
- **Server:** detects the entry, decides, applies (only the server's explosion does damage), and sends a
  `WaterEntryPacket` (ID, mode, entry point) to players within sync distance (at least 3 km).
- **Clients:** run the same detection as a prediction (stop, splash, remove their copy), then obey the packet. If the
  packet says something the client did not predict, the client applies the server's outcome.
- Network channel **47391** (brain: 23010, Water Mod: 50271).
- Not tested yet with a second player or on a dedicated server.

## Not built yet

Phase 2 penetration (slowing, HealthPool/ExplosionDamage scaling), phase 3 config file, phase 4 ricochet and stopping
gatling/autocannon bullets. See the plan.
