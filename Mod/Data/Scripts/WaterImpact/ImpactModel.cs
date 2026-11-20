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
        Penetrate = 2,  // travels on under water, slowing down and losing damage
        Dud = 3,        // removed at the entry point without an explosion
    }

    // Tunable values. Phase 3 moves these into a per-world config file.
    public static class ImpactSettings
    {
        // Kinetic rounds entering faster than this shatter at the surface (impact pressure ~ 0.5 * rho * v^2 is above
        // what steel survives). With the default, both vanilla railguns shatter; artillery and assault cannon do not.
        public const float BreakupSpeed = 1100f;

        // Water drag: speed falls as v = v0 * e^(-s / L) with distance s under water, and the drag length
        // L = DragK * mass^(1/3) (a heavier round has more mass per frontal area). DragK 4 gives 27 m for the assault
        // cannon shell (300 kg) and 37 m for artillery (800 kg).
        public const float DragK = 4f;

        // A penetrating round is removed once its damage (which follows kinetic energy, (v / v0)^2) falls below this
        // fraction of its entry value. 0.05 means after 1.5 L of travel.
        public const float StopFraction = 0.05f;

        // A kinetic round that would travel less than this under water detonates at the surface instead.
        public const float MinTravel = 3f;

        // Splash radius limits, metres.
        public const float MinSplashRadius = 2f;
        public const float MaxSplashRadius = 25f;

        // Per-subtype mode overrides (beat the automatic rules). Empty for now; phase 3 fills it from the config.
        public static readonly Dictionary<string, ImpactMode> Overrides = new Dictionary<string, ImpactMode>();

        // Per-subtype drag length overrides, metres. Empty for now; phase 3 fills it from the config.
        public static readonly Dictionary<string, float> DragLengths = new Dictionary<string, float>();
    }

    // Pure maths and rules: no game calls, so it behaves the same on server and clients.
    public static class ImpactModel
    {
        // The automatic rules (mode Auto in the plan, docs/water-impact-plan.md in the workspace).
        public static ImpactMode Resolve(string subtype, float definitionHealthPool, float explosionDamage, float speed, float dragLength)
        {
            ImpactMode overridden;
            if (subtype != null && ImpactSettings.Overrides.TryGetValue(subtype, out overridden))
                return overridden;

            if (subtype == "Flare" || (subtype != null && subtype.StartsWith("Firework", StringComparison.Ordinal)))
                return ImpactMode.Dud;

            if (definitionHealthPool > 0f)
            {
                if (speed > ImpactSettings.BreakupSpeed)
                    return ImpactMode.Detonate;
                if (StopTravel(dragLength) < ImpactSettings.MinTravel)
                    return ImpactMode.Detonate;
                return ImpactMode.Penetrate;
            }

            if (explosionDamage > 0f)
                return ImpactMode.Detonate;

            return ImpactMode.Dud;
        }

        // Drag length L in metres for a round: the per-subtype override, else DragK * mass^(1/3).
        public static float DragLength(string subtype, float massKg)
        {
            float overridden;
            if (subtype != null && ImpactSettings.DragLengths.TryGetValue(subtype, out overridden) && overridden > 0f)
                return overridden;
            return ImpactSettings.DragK * (float)Math.Pow(Math.Max(massKg, 1f), 1d / 3d);
        }

        // Distance under water at which damage falls to StopFraction: (v/v0)^2 = f  =>  s = 0.5 * L * ln(1/f).
        public static float StopTravel(float dragLength)
        {
            return 0.5f * dragLength * (float)Math.Log(1d / ImpactSettings.StopFraction);
        }

        // Speed after travelling `distance` metres under water.
        public static float SpeedAt(float entrySpeed, float distance, float dragLength)
        {
            return entrySpeed * (float)Math.Exp(-distance / Math.Max(dragLength, 0.1f));
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
