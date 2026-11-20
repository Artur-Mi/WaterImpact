using System;
using System.Collections.Generic;
using Jakaria.API;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using WaterImpact.NetworkLib;

namespace WaterImpact
{
    // Tracks every missile-type round (rockets, guided missiles, assault/artillery shells, railgun slugs, flares) and
    // decides what happens when one enters the water from above. See docs/behaviour.md.
    //
    // Multiplayer: the server decides and sends a WaterEntryPacket; clients run the same detection as a prediction
    // (motion and effects only, so there is no lag at the surface) and obey the packet when it arrives.
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation | MyUpdateOrder.AfterSimulation)]
    public class WaterImpactSession : MySessionComponentBase
    {
        // Must differ from every other mod's channel (brain: 23010, Water Mod: 50271).
        public const ushort NetworkChannel = 47391;

        // A detonating round that has not exploded after this many ticks is removed instead.
        const int DetonateTimeoutTicks = 10;

        // Ticks between water checks when no water is near. Kept short: a round whose first check finds it already
        // under water is treated as fired under water and ignored.
        const int NoWaterRecheckTicks = 10;

        // Bubble trail of a penetrating round: one bubble every this many ticks (clients only).
        const int BubbleEveryTicks = 3;

        enum Phase
        {
            Air,                // above water (or not yet checked)
            StartedUnderwater,  // fired under water (SLBM, LauncherX): ignored until it surfaces
            Detonating,         // held at the entry point until the game explodes it
            Penetrating,        // travelling under water, slowing down
            Removing,           // dud or spent: removed on the next BeforeSimulation
            Done,               // handled or ignored; no more checks
        }

        class Tracked
        {
            public IMyMissile Missile;
            public Phase Phase;
            public bool HasPrev;
            public Vector3D PrevPos;
            public double PrevDepth;
            public int NextCheckTick;
            public Vector3D EntryPos;
            public int DetonateTick;
            public bool Splashed;

            // Penetration state.
            public Vector3 Dir;          // unit direction under water (kept straight)
            public float EntrySpeed;     // v0, m/s
            public float DragLength;     // L, m
            public float Travelled;      // s, m under water
            public float DamageFactor;   // (v / v0)^2 last applied to HealthPool / ExplosionDamage (server)
        }

        public static WaterImpactSession Instance;

        Network net;
        bool isServer;
        bool isDedicated;
        int tick;

        readonly Dictionary<long, Tracked> tracked = new Dictionary<long, Tracked>();
        readonly List<Tracked> work = new List<Tracked>();
        readonly List<IMyMissile> toDestroy = new List<IMyMissile>();
        readonly List<IMyPlayer> players = new List<IMyPlayer>();
        readonly HashSet<string> loggedSubtypes = new HashSet<string>();

        public override void LoadData()
        {
            Instance = this;
            net = new Network(NetworkChannel, ModContext.ModName);
            WaterEntryPacket.OnReceive += OnWaterEntryPacket;
        }

        public override void BeforeStart()
        {
            isServer = MyAPIGateway.Multiplayer.IsServer;
            isDedicated = MyAPIGateway.Utilities.IsDedicated;
            MyAPIGateway.Missiles.OnMissileAdded += OnMissileAdded;
            MyAPIGateway.Missiles.OnMissileRemoved += OnMissileRemoved;
        }

        protected override void UnloadData()
        {
            if (MyAPIGateway.Missiles != null)
            {
                MyAPIGateway.Missiles.OnMissileAdded -= OnMissileAdded;
                MyAPIGateway.Missiles.OnMissileRemoved -= OnMissileRemoved;
            }
            WaterEntryPacket.OnReceive -= OnWaterEntryPacket;
            net?.Dispose();
            net = null;
            tracked.Clear();
            Instance = null;
        }

        void OnMissileAdded(IMyMissile missile)
        {
            if (missile == null)
                return;
            tracked[missile.EntityId] = new Tracked { Missile = missile, Phase = Phase.Air, NextCheckTick = tick };
        }

        void OnMissileRemoved(IMyMissile missile)
        {
            if (missile != null)
                tracked.Remove(missile.EntityId);
        }

        // Before the missiles move: keep detonating rounds still, set the speed of penetrating rounds for this tick,
        // remove duds and spent rounds (Destroy() is not allowed inside the missile's own events, so it happens here).
        public override void UpdateBeforeSimulation()
        {
            tick++;
            if (tracked.Count == 0)
                return;

            work.Clear();
            work.AddRange(tracked.Values);
            toDestroy.Clear();

            foreach (Tracked t in work)
            {
                IMyMissile missile = t.Missile;
                if (missile == null || missile.MarkedForClose || missile.Closed)
                    continue;

                switch (t.Phase)
                {
                    case Phase.Detonating:
                        missile.LinearVelocity = Vector3.Zero;
                        if (tick - t.DetonateTick > DetonateTimeoutTicks)
                            toDestroy.Add(missile);
                        break;

                    case Phase.Penetrating:
                        StepPenetration(t);
                        if (t.Phase == Phase.Removing)
                            toDestroy.Add(missile);
                        break;

                    case Phase.Removing:
                        toDestroy.Add(missile);
                        break;
                }
            }

            foreach (IMyMissile missile in toDestroy)
            {
                Tracked t;
                if (tracked.TryGetValue(missile.EntityId, out t))
                    t.Phase = Phase.Done;
                try
                {
                    missile.Destroy();
                }
                catch (Exception e)
                {
                    MyLog.Default.WriteLineAndConsole("[WaterImpact] Destroy failed: " + e.Message);
                }
            }
            toDestroy.Clear();
            work.Clear();
        }

        // One tick of a penetrating round. The speed for this tick comes from the distance already travelled, so the
        // game's gravity (re-added every tick to shells) never builds up. Damage follows kinetic energy.
        void StepPenetration(Tracked t)
        {
            IMyMissile missile = t.Missile;
            float speed = ImpactModel.SpeedAt(t.EntrySpeed, t.Travelled, t.DragLength);
            float ratio = t.EntrySpeed > 0f ? speed / t.EntrySpeed : 0f;
            float factor = ratio * ratio;

            if (factor < ImpactSettings.StopFraction)
            {
                // Spent: it sinks harmlessly. Clients remove their copy too (same maths, and the packet started it).
                t.Phase = Phase.Removing;
                missile.LinearVelocity = Vector3.Zero;
                Bubble(missile.GetPosition(), 1f);
                return;
            }

            if (isServer)
                ScaleDamage(t, factor);

            missile.LinearVelocity = t.Dir * speed;
            t.Travelled += speed / 60f;
        }

        // Server only: scale HealthPool and ExplosionDamage by the change in (v/v0)^2 since the last tick. Scaling the
        // current values (not resetting from the entry values) keeps whatever the round already spent on hulls.
        void ScaleDamage(Tracked t, float factor)
        {
            if (t.DamageFactor <= 0f || factor >= t.DamageFactor)
                return;

            IMyMissile missile = t.Missile;
            float step = factor / t.DamageFactor;
            t.DamageFactor = factor;

            float pool = missile.HealthPool;
            if (pool > 0f)
                missile.HealthPool = pool * step;

            float damage = missile.ExplosionDamage;
            if (damage > 0f)
                missile.ExplosionDamage = damage * step;
        }

        // After physics, positions are final for this tick: detect water entries, snap detonating rounds back to
        // their entry point (the brain's guidance or physics may have moved them), draw bubble trails.
        public override void UpdateAfterSimulation()
        {
            if (tracked.Count == 0 || !WaterModAPI.Registered)
                return;

            work.Clear();
            work.AddRange(tracked.Values);

            foreach (Tracked t in work)
            {
                IMyMissile missile = t.Missile;
                if (missile == null || missile.MarkedForClose || missile.Closed)
                {
                    if (missile != null)
                        tracked.Remove(missile.EntityId);
                    continue;
                }

                switch (t.Phase)
                {
                    case Phase.Detonating:
                        missile.SetPosition(t.EntryPos);
                        break;
                    case Phase.Penetrating:
                        if (!isDedicated && tick % BubbleEveryTicks == 0)
                            Bubble(missile.GetPosition(), 0.5f);
                        break;
                    case Phase.Air:
                    case Phase.StartedUnderwater:
                        if (tick >= t.NextCheckTick)
                            CheckWater(t);
                        break;
                }
            }
            work.Clear();
        }

        void CheckWater(Tracked t)
        {
            IMyMissile missile = t.Missile;
            Vector3D pos = missile.GetPosition();

            float? depthOrNull;
            try
            {
                depthOrNull = WaterModAPI.GetDepth(pos);
            }
            catch (Exception)
            {
                depthOrNull = null;
            }

            if (!depthOrNull.HasValue)
            {
                // No water near this round: look again later.
                t.HasPrev = false;
                t.NextCheckTick = tick + NoWaterRecheckTicks;
                return;
            }

            double depth = depthOrNull.Value;
            Vector3 velocity = missile.LinearVelocity;
            float speed = velocity.Length();

            if (!t.HasPrev)
            {
                t.HasPrev = true;
                if (depth <= 0d)
                    t.Phase = Phase.StartedUnderwater;
            }
            else if (t.Phase == Phase.StartedUnderwater)
            {
                if (depth > 0d)
                    t.Phase = Phase.Air;
            }
            else if (depth <= 0d)
            {
                Vector3D entry = ImpactModel.EntryPoint(t.PrevPos, t.PrevDepth, pos, depth);
                OnEntry(t, entry, velocity);
                return;
            }

            t.PrevPos = pos;
            t.PrevDepth = depth;
            t.NextCheckTick = tick + (t.Phase == Phase.Air ? ImpactModel.TicksUntilNextCheck(depth, speed) : 1);
        }

        void OnEntry(Tracked t, Vector3D entry, Vector3 velocity)
        {
            IMyMissile missile = t.Missile;
            MyMissileAmmoDefinition ammo = missile.AmmoDefinition as MyMissileAmmoDefinition;
            string subtype = missile.AmmoDefinition?.Id.SubtypeName;
            float definitionPool = ammo != null ? ammo.MissileHealthPool : 0f;
            float mass = ammo != null ? ammo.MissileMass : 1f;
            float speed = velocity.Length();
            float dragLength = ImpactModel.DragLength(subtype, mass);
            ImpactMode mode = ImpactModel.Resolve(subtype, definitionPool, missile.ExplosionDamage, speed, dragLength);

            if (subtype != null && loggedSubtypes.Add(subtype))
            {
                string extra = mode == ImpactMode.Penetrate
                    ? " (L " + dragLength.ToString("0.0") + " m, stops after " + ImpactModel.StopTravel(dragLength).ToString("0") + " m)"
                    : "";
                MyLog.Default.WriteLineAndConsole("[WaterImpact] " + subtype + " hit water at " + speed.ToString("0") + " m/s: " + mode + extra);
            }

            Vector3 dir = speed > 0.01f ? velocity / speed : Vector3.Zero;
            Apply(t, mode, entry, dir, speed, ImpactModel.SplashRadius(mass, speed));

            if (isServer)
                SendEntry(missile.EntityId, mode, entry, dir, speed);
        }

        void Apply(Tracked t, ImpactMode mode, Vector3D entry, Vector3 dir, float speed, float splashRadius)
        {
            IMyMissile missile = t.Missile;

            switch (mode)
            {
                case ImpactMode.Detonate:
                    t.Phase = Phase.Detonating;
                    t.EntryPos = entry;
                    t.DetonateTick = tick;
                    missile.SetPosition(entry);
                    missile.LinearVelocity = Vector3.Zero;
                    // MyMissile explodes when its distance from Origin reaches MaxTrajectory. On clients this only
                    // removes the local copy; the server's explosion is the real one.
                    float travelled = (float)Vector3D.Distance(missile.Origin, entry);
                    missile.MaxTrajectory = Math.Max(0f, travelled - 0.01f);
                    break;

                case ImpactMode.Penetrate:
                    if (dir == Vector3.Zero || speed <= 0f)
                    {
                        t.Phase = Phase.Removing;
                        break;
                    }
                    MyMissileAmmoDefinition ammo = missile.AmmoDefinition as MyMissileAmmoDefinition;
                    float mass = ammo != null ? ammo.MissileMass : 1f;
                    t.Phase = Phase.Penetrating;
                    t.EntryPos = entry;
                    t.Dir = dir;
                    t.EntrySpeed = speed;
                    t.DragLength = ImpactModel.DragLength(missile.AmmoDefinition?.Id.SubtypeName, mass);
                    t.Travelled = 0f;
                    t.DamageFactor = 1f;
                    // The detection runs after the move, so the round may already be metres past the surface: start
                    // the underwater run at the entry point.
                    missile.SetPosition(entry);
                    missile.LinearVelocity = dir * speed;
                    break;

                case ImpactMode.Dud:
                    t.Phase = Phase.Removing;
                    missile.SetPosition(entry);
                    missile.LinearVelocity = Vector3.Zero;
                    break;

                default:
                    t.Phase = Phase.Done;
                    break;
            }

            if (!t.Splashed)
            {
                t.Splashed = true;
                Splash(entry, splashRadius);
            }
        }

        void Splash(Vector3D position, float radius)
        {
            if (isDedicated)
                return;
            try
            {
                Vector3D up = WaterModAPI.GetUpDirection(position);
                WaterModAPI.CreateSplash(position, radius, true);
                WaterModAPI.CreatePhysicsSplash(position, up * radius * 2d, Math.Max(radius / 4f, 1f), 3);
            }
            catch (Exception e)
            {
                // The Water Mod throws where there is no water; never let an effect break the session.
                MyLog.Default.WriteLineAndConsole("[WaterImpact] Splash failed: " + e.Message);
            }
        }

        void Bubble(Vector3D position, float radius)
        {
            if (isDedicated)
                return;
            try
            {
                WaterModAPI.CreateBubble(position, radius);
            }
            catch (Exception)
            {
                // effects only
            }
        }

        // Server: tell the players near the entry point. The server's own player already applied it.
        void SendEntry(long missileId, ImpactMode mode, Vector3D entry, Vector3 dir, float speed)
        {
            if (net == null || !MyAPIGateway.Multiplayer.MultiplayerActive)
                return;

            WaterEntryPacket packet = new WaterEntryPacket
            {
                MissileId = missileId,
                Mode = (byte)mode,
                X = entry.X,
                Y = entry.Y,
                Z = entry.Z,
                Speed = speed,
                DirX = dir.X,
                DirY = dir.Y,
                DirZ = dir.Z,
            };

            double range = Math.Max(MyAPIGateway.Session.SessionSettings.SyncDistance, 3000);
            double rangeSq = range * range;
            byte[] serialized = null;

            players.Clear();
            MyAPIGateway.Players.GetPlayers(players);
            foreach (IMyPlayer player in players)
            {
                if (player.SteamUserId == MyAPIGateway.Multiplayer.ServerId || player.IsBot)
                    continue;
                if (Vector3D.DistanceSquared(player.GetPosition(), entry) > rangeSq)
                    continue;
                if (serialized == null)
                    serialized = MyAPIGateway.Utilities.SerializeToBinary(packet);
                net.SendToPlayer(packet, player.SteamUserId, serialized);
            }
            players.Clear();
        }

        // Client: the server's decision overrides the local prediction.
        void OnWaterEntryPacket(WaterEntryPacket packet, ref PacketInfo packetInfo, ulong senderSteamId)
        {
            if (isServer)
                return;

            Tracked t;
            if (!tracked.TryGetValue(packet.MissileId, out t))
                return; // already gone here

            IMyMissile missile = t.Missile;
            if (missile == null || missile.MarkedForClose || missile.Closed)
                return;

            ImpactMode mode = (ImpactMode)packet.Mode;
            Vector3D entry = new Vector3D(packet.X, packet.Y, packet.Z);
            Vector3 dir = new Vector3(packet.DirX, packet.DirY, packet.DirZ);

            if (t.Phase == Phase.Detonating && mode == ImpactMode.Detonate)
            {
                t.EntryPos = entry; // predicted the same; adopt the server's point
                return;
            }
            if (t.Phase == Phase.Removing && mode == ImpactMode.Dud)
                return;
            if (t.Phase == Phase.Penetrating && mode == ImpactMode.Penetrate)
            {
                // Predicted the same: keep the local progress, but follow the server's entry state.
                t.EntryPos = entry;
                t.Dir = dir;
                t.EntrySpeed = packet.Speed;
                missile.SetPosition(entry + (Vector3D)dir * t.Travelled);
                return;
            }

            MyMissileAmmoDefinition ammo = missile.AmmoDefinition as MyMissileAmmoDefinition;
            float mass = ammo != null ? ammo.MissileMass : 1f;
            Apply(t, mode, entry, dir, packet.Speed, ImpactModel.SplashRadius(mass, packet.Speed));
        }
    }
}
