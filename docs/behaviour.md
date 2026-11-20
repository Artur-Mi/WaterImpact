# Behaviour and code

State: **phase 1 built and loaded in game 2026-11-19** (compiles; a rocket detonated on the water per the log; visuals not
confirmed yet). **Phase 2 (penetration) built 2026-11-19, not tested in game.** The full plan, physics and decisions are in
[../../docs/water-impact-plan.md](../../docs/water-impact-plan.md).

## What it does

Every missile-type round is tracked. When one goes from above the surface to below it, its ammo decides:

| Ammo | Mode | What happens |
|---|---|---|
| `Flare`, `Firework*` | Dud | Removed at the entry point, small splash, no explosion |
| Explosive, no HealthPool (`Missile`, brain-guided missiles, `IDMissile`, `IronDomeXMissile`, ...) | Detonate | Explodes at the entry point with its own damage |
| Kinetic (HealthPool > 0) entering faster than 1 100 m/s (`SmallRailgunSlug`, `LargeRailgunSlug`) | Detonate | Shatters at the surface (its explosion damage is 0 in vanilla, so only a splash and a small blast) |
| Other kinetic (`MediumCalibreShell`, `LargeCalibreShell`) | Penetrate | Travels on under water in a straight line, slowing down and losing damage, then sinks (removed). See below |
| No HealthPool and no explosion damage | Dud | Removed |

- **Only on entry from above.** Rounds fired under water (SLBM `AMissile`, `LauncherXMissile`) are ignored until they
  surface; if one falls back into the sea, it detonates.
- The entry point is interpolated between the last two checks from the Water Mod's signed depth, since a large
  railgun slug moves 33 m per tick.
- Splash radius grows with the cube root of the kinetic energy: about 2 m for a rocket, 9 m for artillery, 25 m (cap)
  for a large railgun. The Water Mod already restyles the explosion itself as a water blast.
- The first time each ammo subtype hits water, the log gets `[WaterImpact] <subtype> hit water at <speed> m/s: <mode>`.
  Search the newest `SpaceEngineers_*.log` for `[WaterImpact]` when testing.
- Values are constants in `ImpactSettings` for now (break-up speed 1 100 m/s, `DragK` 4, stop at 5 %, minimum travel
  3 m); phase 3 moves them to a world config.

## How penetration works

- Speed falls with distance under water: `v = v0 · e^(-s / L)`, with the drag length `L = 4 · mass^(1/3)` metres
  (27 m for the assault cannon shell, 37 m for artillery). The mod sets the speed every tick from the distance
  travelled, so the game's gravity never builds up and the round keeps its entry direction.
- Damage follows kinetic energy: `HealthPool` and `ExplosionDamage` are multiplied each tick by the drop in
  `(v / v0)²`, **on the server only**. Scaling the current values (not resetting them) keeps what the round already
  spent on hulls it hit under water.
- When `(v / v0)²` falls below 5 % (after `1.5 L`: 40 m for the assault cannon, 56 m for artillery) the round is
  removed with a bubble. A round that would stop within 3 m detonates at the surface instead (only very light ones).
- The detection runs after the move, so the round may be metres past the surface already: it is put back on the entry
  point to start its underwater run.
- Clients draw a bubble trail (one every 3 ticks).
- The log line for a penetrating subtype also gives `L` and the stop distance.

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
  `WaterEntryPacket` (ID, mode, entry point, entry speed and direction) to players within sync distance (at least 3 km).
- **Clients:** run the same detection as a prediction (stop, splash, slow down, remove their copy), then obey the
  packet. If the packet says something the client did not predict, the client applies the server's outcome; for a
  penetrating round it keeps its own progress but moves the round onto the server's entry line. Clients never change
  damage.
- Network channel **47391** (brain: 23010, Water Mod: 50271).
- Not tested yet with a second player or on a dedicated server.

## Not built yet

Phase 3 config file (all values are constants in `ImpactSettings` for now), phase 4 ricochet and stopping
gatling/autocannon bullets. See the plan.
