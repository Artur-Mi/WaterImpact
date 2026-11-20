# WaterImpact

Space Engineers 1 mod: what happens when missile-type rounds (rockets, guided missiles, assault/artillery shells,
railgun slugs, flares) enter the water from above. Standalone (no brain dependency); needs the Water Mod.
Part of the MisterWeapon modpack; workspace overview: [../CLAUDE.md](../CLAUDE.md).

## Docs
- [README.md](README.md): quick start
- [docs/behaviour.md](docs/behaviour.md): rules, code layout, multiplayer, what is built and what is not
- [../docs/water-impact-plan.md](../docs/water-impact-plan.md): the full plan, physics, decisions and phases
- [../missil-misterplace/docs/deployment.md](../missil-misterplace/docs/deployment.md): dev/final deploy rules (shared by all mods)

## Rules
- Edit only inside `Mod/`. Never edit the copies in `<SE data>/Mods` (Windows: `%APPDATA%\SpaceEngineers\Mods`) directly.
- Scripts must compile under the game's Mod API: C# 7.3 and whitelisted APIs only. Use the `se-dev-mod` and
  `se-dev-game-code` skills to check APIs.
- Deploy with `.\deploy.ps1 dev`. Run `.\deploy.ps1 final` only when the user asks for a release.
- Dev Workshop ID 3842706265; final has none yet. `Mod/modinfo.sbmi` is generated and git-ignored; after the first
  final publish, record the new ID in `deploy.json`. The Workshop page must list the Water Mod as a required item.
- Multiplayer is required: the server decides, clients only predict motion and effects. Never change damage
  (`HealthPool`, `ExplosionDamage`) on a client. Network channel `47391` must stay unique (brain: 23010).
- `Network/NetworkLib/` is THDigi's library with only its namespace changed (`WaterImpact.NetworkLib`); do not edit it.
- Water Mod calls throw or return null where there is no water: guard them, and test at sea.
- Keep the docs current whenever behaviour, layout or the workflow changes.
