# WaterImpact

Missiles and shells fired from above water stop treating the sea as air (Space Engineers 1, part of the MisterWeapon
modpack). Rockets and guided missiles explode on the surface; railgun slugs shatter there; flares and fireworks vanish.
Artillery and assault-cannon shells will travel a short way under water, losing damage (phase 2, not built yet).
**Standalone**: it does not need the brain. It needs the **Water Mod** and a water planet; without them it does nothing.
See [docs/behaviour.md](docs/behaviour.md).

| Target | Local folder in `<SE data>/Mods` (Windows: `%APPDATA%\SpaceEngineers\Mods`) | Workshop ID |
|---|---|---|
| dev   | `WaterImpact-dev` | none yet (created by the first dev Publish) |
| final | `WaterImpact`     | none yet (not published) |

## Quick start
```powershell
.\deploy.ps1 dev            # link Mods/WaterImpact-dev -> Mod/ (live edits)
.\deploy.ps1 final -DryRun  # preview a release
.\deploy.ps1 final          # needs a clean git tree; copies a snapshot, tags release/<date>
```
Then publish from the game's Mods screen. Never load dev and final in the same world.
Deploy details: [../missil-misterplace/docs/deployment.md](../missil-misterplace/docs/deployment.md).

## Layout
- `Mod/`: everything that ships (scripts only)
- `deploy.json` / `deploy.ps1`: deploy targets and script
- `docs/`: documentation

## Docs
- [Behaviour and code](docs/behaviour.md)
- Full plan with the physics and phases: [../docs/water-impact-plan.md](../docs/water-impact-plan.md)
