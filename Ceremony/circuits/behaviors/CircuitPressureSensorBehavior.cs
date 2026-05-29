using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

#nullable disable

namespace circuits
{
    public class CircuitPressureSensorBehavior : CircuitBehavior
    {
        public const string PortIdOut = "pressure.sensor";

        private long tickListenerId;
        private long holdCallbackId;
        private bool lastPressed;

        private int holdDelayMs = 1000;
        // "players" | "entities" | "all"
        private string filter = "players";

        private static readonly string[] FilterKeys   = { "players", "entities", "all" };
        private static readonly string[] FilterLabels = { "Players", "All Entities", "Entities + Items" };

        public CircuitPressureSensorBehavior(BlockEntity be) : base(be) { }

        protected override IEnumerable<PortSpec> BuildPorts()
        {
            yield return new PortSpec(new PortDef
            {
                PortID = PortIdOut,
                Dir = PortDir.Out,
                Type = SignalType.Bool,
                DisplayName = "Depressed"
            });
        }

        protected override void ConfigureSettings(NodeSettingsDescriptor descriptor)
        {
            descriptor.Title = "Pressure Sensor";
            descriptor.AddNumber("holdDelayMs", "Hold Time (ms)", 1000, min: 0, max: 60000);
            descriptor.AddSelect("filter", "Trigger For", FilterKeys, FilterLabels, "players");
        }

        protected override Dictionary<string, object> GetCurrentSettings() => new()
        {
            ["holdDelayMs"] = holdDelayMs,
            ["filter"] = filter
        };

        protected override void ApplySettings(Dictionary<string, string> values)
        {
            if (values.TryGetValue("holdDelayMs", out var s) && int.TryParse(s, out int v))
                holdDelayMs = v < 0 ? 0 : v;
            if (values.TryGetValue("filter", out var f) && f != null)
                filter = f;
            Blockentity.MarkDirty(true);
        }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);

            if (api.Side == EnumAppSide.Server)
            {
                SetStateVariant(false);
                lastPressed = false;
                tickListenerId = Blockentity.RegisterGameTickListener(OnProximityTick, 20);
            }
        }

        public override void OnBlockRemoved()
        {
            if (Api?.Side == EnumAppSide.Server)
            {
                CancelHold();
                if (tickListenerId != 0)
                    Api.World.UnregisterGameTickListener(tickListenerId);
            }
            base.OnBlockRemoved();
        }

        // ── Proximity tick ───────────────────────────────────────────

        private void OnProximityTick(float dt)
        {
            bool pressed = CheckPressed();

            if (pressed)
            {
                // Cancel any pending hold-release when something steps back on
                CancelHold();

                if (!lastPressed)
                {
                    lastPressed = true;
                    SetStateVariant(true);
                    PlayToggle(true);
                    TryEmitBool(PortIdOut, true, out _);
                    Blockentity.MarkDirty(true);
                }
            }
            else if (lastPressed && holdCallbackId == 0)
            {
                // Plate just cleared — start (or immediately fire) the hold timer
                if (holdDelayMs > 0)
                {
                    holdCallbackId = Api.World.RegisterCallback(dt =>
                    {
                        holdCallbackId = 0;
                        lastPressed = false;
                        SetStateVariant(false);
                        PlayToggle(false);
                        TryEmitBool(PortIdOut, false, out _);
                        Blockentity.MarkDirty(true);
                    }, holdDelayMs);
                }
                else
                {
                    lastPressed = false;
                    SetStateVariant(false);
                    PlayToggle(false);
                    TryEmitBool(PortIdOut, false, out _);
                    Blockentity.MarkDirty(true);
                }
            }
        }

        private void CancelHold()
        {
            if (holdCallbackId != 0)
            {
                Api.World.UnregisterCallback(holdCallbackId);
                holdCallbackId = 0;
            }
        }

        // ── Entity detection ─────────────────────────────────────────

        private bool CheckPressed()
        {
            double plateMinX = Pos.X;
            double plateMaxX = Pos.X + 1.0;
            double plateMinZ = Pos.Z;
            double plateMaxZ = Pos.Z + 1.0;
            double minY = Pos.Y - 0.02;
            double maxY = Pos.Y + 0.50;

            Vec3d center = Pos.ToVec3d().Add(0.5, 0.05, 0.5);
            Entity[] ents = Api.World.GetEntitiesAround(center, 1.2f, 0.6f);

            foreach (var ent in ents)
            {
                if (!IsEntityMatch(ent)) continue;

                var p = ent.Pos;
                var cb = ent.CollisionBox;

                if (p.Y < minY || p.Y > maxY) continue;

                bool overlapX = (p.X + cb.X2) > plateMinX && (p.X + cb.X1) < plateMaxX;
                bool overlapZ = (p.Z + cb.Z2) > plateMinZ && (p.Z + cb.Z1) < plateMaxZ;

                if (overlapX && overlapZ) return true;
            }

            return false;
        }

        private bool IsEntityMatch(Entity ent)
        {
            switch (filter)
            {
                case "players":
                    return ent is EntityPlayer ep && ep.Alive;
                case "entities":
                    return ent is EntityAgent ea && ea.Alive;
                case "all":
                    return ent.Alive;
                default:
                    return ent is EntityPlayer ep2 && ep2.Alive;
            }
        }

        private void PlayToggle(bool isHigh)
        {
            float pitch = isHigh
                ? (1.1f + (float)Api.World.Rand.NextDouble() * 0.1f)
                : (0.8f + (float)Api.World.Rand.NextDouble() * 0.1f);
            Api.World.PlaySoundAt(
                new AssetLocation("game:sounds/effect/woodswitch"),
                Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5,
                dualCallByPlayer: null, pitch, volume: 16f);
        }

        // ── Block info ───────────────────────────────────────────────

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            string filterLabel = filter switch
            {
                "entities" => "All Entities",
                "all"      => "Entities + Items",
                _          => "Players"
            };
            dsc.AppendLine($"Trigger: {filterLabel}");
            if (holdDelayMs > 0)
                dsc.AppendLine($"Hold: {FormatDuration(holdDelayMs)}");
            dsc.AppendLine(lastPressed ? "State: ON" : "State: OFF");
        }

        // ── Carry-data settings ──────────────────────────────────────

        public override void WriteSettingsToTree(ITreeAttribute tree)
        {
            base.WriteSettingsToTree(tree);
            tree.SetInt("ps.holdDelayMs", holdDelayMs);
            tree.SetString("ps.filter", filter);
        }

        public override void ReadSettingsFromTree(ITreeAttribute tree)
        {
            base.ReadSettingsFromTree(tree);
            holdDelayMs = tree.GetInt("ps.holdDelayMs", 1000);
            filter = tree.GetString("ps.filter") ?? "players";
        }

        // ── Persistence ──────────────────────────────────────────────

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            holdDelayMs = tree.GetInt("ps.holdDelayMs", 1000);
            filter = tree.GetString("ps.filter") ?? "players";
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetInt("ps.holdDelayMs", holdDelayMs);
            tree.SetString("ps.filter", filter);
        }
    }
}
