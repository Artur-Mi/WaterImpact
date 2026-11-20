using System;
using System.Collections.Generic;
using VRageMath;

namespace WaterImpact
{
    // What happens to a round that enters the water from above.
    public enum ImpactMode : byte
    {
        Ignore = 0,     // left alone, keeps flying as the game moves it
        Detonate = 1,   // explodes at the entry point with its own damage
        Penetrate = 2,  // travels on under water, slowing down (phase 2; until then treated like Ignore)
        Dud = 3,        // removed at the entry point without an explosion
    }

    // Tunable values. Phase 3 moves these into a per-world config file.
    public static class ImpactSettings
    {
        // Kinetic rounds entering faster than this shatter at the surface (impact pressure ~ 0.5 * rho * v^2 is above
        // what steel survives). With the default, both vanilla railguns shatter; artillery and assault cannon do not.
        public const float BreakupSpeed = 1100f;

        // Splash radius limits, metres.
        public const float MinSplashRadius = 2f;
        public const float MaxSplashRadius = 25f;

        // Per-subtype mode overrides (beat the automatic rules). Empty for now; phase 3 fills it from the config.
        public static readonly Dictionary<string, ImpactMode> Overrides = new Dictionary<string, ImpactMode>();
    }

    // Pure maths and rules: no game calls, so it behaves the same on server and clients.
    public static class ImpactModel
    {
        // The automatic rules (mode Auto in the plan, docs/water-impact-plan.md in the workspace).
        public static ImpactMode Resolve(string subtype, float definitionHealthPool, float explosionDamage, float speed)
        {
            ImpactMode overridden;
            if (subtype != null && ImpactSettings.Overrides.TryGetValue(subtype, out overridden))
                return overridden;

            if (subtype == "Flare" || (subtype != null && subtype.StartsWith("Firework", StringComparison.Ordinal)))
                return ImpactMode.Dud;

            if (definitionHealthPool > 0f)
                return speed > ImpactSettings.BreakupSpeed ? ImpactMode.Detonate : ImpactMode.Penetrate;

            if (explosionDamage > 0f)
                return ImpactMode.Detonate;

            return ImpactMode.Dud;
        }

        // Where the straight segment prev -> now crosses the surface, from the two signed depths
        // (Water Mod convention: positive above the surface, negative below).
        public static Vector3D EntryPoint(Vector3D prevPos, double prevDepth, Vector3D nowPos, double nowDepth)
        {
            double span = prevDepth - nowDepth;
            double t = span > 1e-6 ? prevDepth / span : 1d;
            t = MathHelper.Clamp(t, 0d, 1d);
            return prevPos + (nowPos - prevPos) * t;
        }

        // Splash size grows with the cube root of kinetic energy: rocket ~2 m, artillery ~9 m, large railgun 25 m (cap).
        public static float SplashRadius(float massKg, float speed)
        {
            double energyMJ = 0.5d * Math.Max(massKg, 1f) * speed * speed / 1e6d;
            float radius = (float)(2d * Math.Pow(Math.Max(energyMJ, 0d), 1d / 3d));
            return MathHelper.Clamp(radius, ImpactSettings.MinSplashRadius, ImpactSettings.MaxSplashRadius);
        }

        // Ticks until the next water check for a round `heightAbove` metres over the surface: half the time it needs
        // to reach the surface at its current speed, between 1 and 30 ticks.
        public static int TicksUntilNextCheck(double heightAbove, float speed)
        {
            if (heightAbove <= 0d)
                return 1;
            double ticksToSurface = heightAbove / Math.Max(speed, 1f) * 60d;
            int ticks = (int)(ticksToSurface / 2d);
            return MathHelper.Clamp(ticks, 1, 30);
        }
    }
}
