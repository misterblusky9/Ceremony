using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

#nullable disable

namespace circuits
{
    public class CircuitEffectorLightning : CircuitBehavior
    {
        private const string PortIdIn = "lightning.set";
        private const string PortIdOut = "lightning.state";

        private readonly Dictionary<PortKey, bool> inputSignals = new();

        private float offsetRange = 0f;

        private bool currentState;
        private WeatherSystemServer weather;

        public CircuitEffectorLightning(BlockEntity be) : base(be) { }

        protected override IEnumerable<PortSpec> BuildPorts()
        {
            yield return new PortSpec(new PortDef
            {
                PortID = PortIdOut,
                Dir = PortDir.Out,
                Type = SignalType.Bool,
                DisplayName = "Lightning State"
            });

            yield return new PortSpec(
                new PortDef
                {
                    PortID = PortIdIn,
                    Dir = PortDir.In,
                    Type = SignalType.Bool,
                    MaxInputs = int.MaxValue,
                    DisplayName = "Call Lightning"
                },
                new global::System.Func<object, PortKey, bool>(OnSetLightning));
        }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);

            offsetRange = properties?["offsetRange"].AsFloat(offsetRange) ?? offsetRange;

            if (api.Side != EnumAppSide.Server) return;

            weather = api.ModLoader.GetModSystem<WeatherSystemServer>();

            currentState = ReadStateVariant();
            TryEmitBool(PortIdOut, currentState, out _);
        }

        protected override void ConfigureSettings(NodeSettingsDescriptor d)
        {
            d.Title = "Lightning Effector";
            d.AddNumber("offsetRange", "Radius", offsetRange, 0, 128);
        }

        protected override Dictionary<string, object> GetCurrentSettings() => new()
        {
            ["offsetRange"] = offsetRange
        };

        protected override void ApplySettings(Dictionary<string, string> values)
        {
            if (values.TryGetValue("offsetRange", out var o) && float.TryParse(o, out var of))
                offsetRange = Math.Clamp(of, 0f, 128f);

            Blockentity.MarkDirty(true);
        }

        private bool OnSetLightning(object value, PortKey from)
        {
            if (value is not bool v) return false;

            inputSignals[from] = v;

            bool on = AnyTrue(inputSignals);

            SetStateVariant(on);
            currentState = on;
            TryEmitBool(PortIdOut, on, out _);

            if (on)
                TryStrike();

            Blockentity.MarkDirty(true);
            return true;
        }

        public override void OnSourceDisconnected(string inPortId, PortKey from)
        {
            if (inPortId == PortIdIn && inputSignals.Remove(from))
            {
                bool on = AnyTrue(inputSignals);

                SetStateVariant(on);
                currentState = on;
                TryEmitBool(PortIdOut, on, out _);

                Blockentity.MarkDirty(true);
                return;
            }

            base.OnSourceDisconnected(inPortId, from);
        }

        private void TryStrike()
        {
            if (Api is not ICoreServerAPI sapi) return;
            if (weather == null) return;

            Vec3d center = new(
                Pos.X + 0.5,
                Pos.Y + 0.5,
                Pos.Z + 0.5
            );

            double offX = 0;
            double offZ = 0;

            if (offsetRange > 0)
            {
                double angle = sapi.World.Rand.NextDouble() * Math.PI * 2;
                double dist = sapi.World.Rand.NextDouble() * offsetRange;

                offX = Math.Cos(angle) * dist;
                offZ = Math.Sin(angle) * dist;
            }

            Vec3d strikePos = new(
                Pos.X + 0.5 + offX,
                Pos.Y + 0.5,
                Pos.Z + 0.5 + offZ
            );

            weather.SpawnLightningFlash(strikePos);
            Blockentity.MarkDirty();
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);

            dsc.AppendLine(currentState ? "State: ON" : "State: OFF");
            dsc.AppendLine($"Offset Radius: {offsetRange}");
        }

        public override void WriteSettingsToTree(ITreeAttribute tree)
        {
            base.WriteSettingsToTree(tree);

            tree.SetFloat("lightning.offsetRange", offsetRange);
        }

        public override void ReadSettingsFromTree(ITreeAttribute tree)
        {
            base.ReadSettingsFromTree(tree);
            if (tree.HasAttribute("lightning.offsetRange"))
                offsetRange = tree.GetFloat("lightning.offsetRange");
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            currentState = tree.GetBool("lightning.state");
            offsetRange = tree.GetFloat("lightning.offsetRange", offsetRange);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetBool("lightning.state", currentState);
            tree.SetFloat("lightning.offsetRange", offsetRange);
        }
    }
}