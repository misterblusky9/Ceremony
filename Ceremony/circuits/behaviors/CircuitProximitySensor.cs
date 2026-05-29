using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

#nullable disable

namespace circuits
{
    public class CircuitProximitySensor : CircuitBehavior
    {
        private const string PortIdOut = "player.state";

        private float radius = 12f;
        private float checkIntervalSeconds = 0.25f;

        private string playerNames = "";
        private bool filterMode = false;

        private long tickListenerId;
        private bool currentState;

        public CircuitProximitySensor(BlockEntity be) : base(be) { }

        protected override IEnumerable<PortSpec> BuildPorts()
        {
            yield return new PortSpec(new PortDef
            {
                PortID = PortIdOut,
                Dir = PortDir.Out,
                Type = SignalType.Bool,
                DisplayName = "Player Detected"
            });
        }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);

            radius = properties?["radius"].AsFloat(radius) ?? radius;
            checkIntervalSeconds = properties?["checkIntervalSeconds"].AsFloat(checkIntervalSeconds) ?? checkIntervalSeconds;
            playerNames = properties?["playerNames"].AsString(playerNames) ?? playerNames;
            filterMode = properties?["filterMode"].AsBool(filterMode) ?? filterMode;

            if (api.Side != EnumAppSide.Server) return;

            currentState = ReadStateVariant();
            TryEmitBool(PortIdOut, currentState, out _);

            RegisterTick();
        }

        protected override void ConfigureSettings(NodeSettingsDescriptor d)
        {
            d.Title = "Proximity Sensor";
            d.AddNumber("radius", "Radius", radius, 1, 2048);
            d.AddNumber("checkIntervalSeconds", "Refresh (s)", checkIntervalSeconds, 0.05f, 60);
            d.AddText("playerNames", "Names", playerNames);
            d.AddToggle("filterMode", "Include?", filterMode);
        }

        protected override Dictionary<string, object> GetCurrentSettings() => new()
        {
            ["radius"] = radius,
            ["checkIntervalSeconds"] = checkIntervalSeconds,
            ["playerNames"] = playerNames,
            ["filterMode"] = filterMode
        };

        protected override void ApplySettings(Dictionary<string, string> values)
        {
            if (values.TryGetValue("radius", out var r) && float.TryParse(r, out var rf))
                radius = Math.Clamp(rf, 1f, 2048f);

            if (values.TryGetValue("checkIntervalSeconds", out var i) && float.TryParse(i, out var iff))
                checkIntervalSeconds = Math.Clamp(iff, 0.05f, 60f);

            if (values.TryGetValue("playerNames", out var names))
                playerNames = names ?? "";

            if (values.TryGetValue("filterMode", out var wm) && bool.TryParse(wm, out var wb))
                filterMode = wb;

            RegisterTick();
            Blockentity.MarkDirty(true);
        }

        private void RegisterTick()
        {
            if (Api?.Side != EnumAppSide.Server) return;

            if (tickListenerId != 0)
            {
                Blockentity.UnregisterGameTickListener(tickListenerId);
                tickListenerId = 0;
            }

            tickListenerId = Blockentity.RegisterGameTickListener(
                OnTick,
                Math.Max(50, (int)(checkIntervalSeconds * 1000))
            );
        }

        private void OnTick(float dt)
        {
            if (Api is not ICoreServerAPI sapi) return;

            Vec3d center = new(
                Pos.X + 0.5,
                Pos.Y + 0.5,
                Pos.Z + 0.5
            );

            var players = sapi.World.GetPlayersAround(center, radius, radius, p =>
            {
                if (p?.WorldData?.CurrentGameMode == EnumGameMode.Spectator)
                    return false;

                if (p is not IServerPlayer sp || !PassesPlayerFilter(sp))
                    return false;

                var ent = p.Entity;
                return ent != null && ent.Alive;
            });

            bool detected = players != null && players.Length > 0;

            if (detected == currentState) return;

            currentState = detected;
            SetStateVariant(detected);
            TryEmitBool(PortIdOut, detected, out _);

            Blockentity.MarkDirty(true);
        }

        private bool PassesPlayerFilter(IServerPlayer player)
        {
            if (string.IsNullOrWhiteSpace(playerNames))
                return true;

            string name = player.PlayerName ?? "";

            foreach (var raw in playerNames.Split(','))
            {
                string listed = raw.Trim();

                if (listed.Length == 0)
                    continue;

                if (string.Equals(name, listed, StringComparison.OrdinalIgnoreCase))
                    return filterMode;
            }

            return !filterMode;
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);

            dsc.AppendLine(currentState ? "State: ON" : "State: OFF");
            dsc.AppendLine($"Range: {radius}");
            dsc.AppendLine($"Check Interval: {checkIntervalSeconds}s");
            dsc.AppendLine(filterMode ? "Filter: Whitelist" : "Filter: Blacklist");

            if (!string.IsNullOrWhiteSpace(playerNames))
                dsc.AppendLine($"Names: {playerNames}");
        }

        public override void WriteSettingsToTree(ITreeAttribute tree)
        {
            base.WriteSettingsToTree(tree);

            tree.SetFloat("playerSensor.radius", radius);
            tree.SetFloat("playerSensor.checkIntervalSeconds", checkIntervalSeconds);
            tree.SetString("playerSensor.playerNames", playerNames);
            tree.SetBool("playerSensor.filterMode", filterMode);
        }

        public override void ReadSettingsFromTree(ITreeAttribute tree)
        {
            base.ReadSettingsFromTree(tree);

            float r = tree.GetFloat("playerSensor.radius", 0);
            if (r > 0) radius = r;

            float i = tree.GetFloat("playerSensor.checkIntervalSeconds", 0);
            if (i > 0) checkIntervalSeconds = i;

            playerNames = tree.GetString("playerSensor.playerNames", playerNames);
            filterMode = tree.GetBool("playerSensor.filterMode", filterMode);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);

            currentState = tree.GetBool("playerSensor.state");

            radius = tree.GetFloat("playerSensor.radius", radius);
            checkIntervalSeconds = tree.GetFloat("playerSensor.checkIntervalSeconds", checkIntervalSeconds);
            playerNames = tree.GetString("playerSensor.playerNames", playerNames);
            filterMode = tree.GetBool("playerSensor.filterMode", filterMode);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);

            tree.SetBool("playerSensor.state", currentState);
            tree.SetFloat("playerSensor.radius", radius);
            tree.SetFloat("playerSensor.checkIntervalSeconds", checkIntervalSeconds);
            tree.SetString("playerSensor.playerNames", playerNames);
            tree.SetBool("playerSensor.filterMode", filterMode);
        }

        public override void OnBlockRemoved()
        {
            Cleanup();
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            Cleanup();
            base.OnBlockUnloaded();
        }

        private void Cleanup()
        {
            if (Api?.Side != EnumAppSide.Server) return;

            if (tickListenerId != 0)
            {
                Blockentity.UnregisterGameTickListener(tickListenerId);
                tickListenerId = 0;
            }
        }
    }
}