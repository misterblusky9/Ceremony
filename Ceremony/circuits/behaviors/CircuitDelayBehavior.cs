using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

#nullable disable

namespace circuits
{
    /// <summary>
    /// True time-shift delay line.  Every input state change is enqueued and
    /// replayed on the output exactly <c>delayMs</c> later.
    /// HIGH at t=0 → output HIGH at t+delay.
    /// LOW  at t=500 → output LOW  at t+500+delay.
    /// </summary>
    public class CircuitDelayBehavior : CircuitBehavior
    {
        public const string PortIdIn = "buffer.in";
        public const string PortIdOut = "buffer.out";

        private int delayMs;
        private bool currentOutput;
        private bool lastInputState;
        private long tickListenerId;

        // Pending state changes queued in chronological order
        private readonly Queue<(long fireAtMs, bool value)> pending = new();
        private readonly Dictionary<PortKey, bool> inputSignals = new();

        public CircuitDelayBehavior(BlockEntity be) : base(be) { }

        protected override IEnumerable<PortSpec> BuildPorts()
        {
            yield return new PortSpec(new PortDef
            {
                PortID = PortIdOut,
                Dir = PortDir.Out,
                Type = SignalType.Bool,
                DisplayName = "Delay Out"
            });

            yield return new PortSpec(
                new PortDef
                {
                    PortID = PortIdIn,
                    Dir = PortDir.In,
                    Type = SignalType.Bool,
                    MaxInputs = int.MaxValue,
                    DisplayName = "Delay In"
                },
                new global::System.Func<object, PortKey, bool>(OnInput)
            );
        }

        protected override void ConfigureSettings(NodeSettingsDescriptor descriptor)
        {
            descriptor.Title = "Delay Settings";
            descriptor.AddNumber("delayMs", "Delay (ms)", 3000, min: 50, max: 3600000);
        }

        protected override Dictionary<string, object> GetCurrentSettings() => new()
        {
            ["delayMs"] = delayMs
        };

        protected override void ApplySettings(Dictionary<string, string> values)
        {
            if (values.TryGetValue("delayMs", out var s) && int.TryParse(s, out int v))
            {
                delayMs = Math.Max(50, v);
                Blockentity.MarkDirty(true);
            }
        }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);

            if (delayMs <= 0)
                delayMs = properties?["delayMs"].AsInt(3000) ?? 3000;
            if (delayMs < 50) delayMs = 50;

            if (api.Side != EnumAppSide.Server) return;

            // Restore last known output; in-flight changes are lost on reload
            SetStateVariant(currentOutput);
            TryEmitBool(PortIdOut, currentOutput, out _);

            tickListenerId = Blockentity.RegisterGameTickListener(OnTick, 50);
        }

        public override void OnBlockRemoved()
        {
            CleanupTick();
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            CleanupTick();
            base.OnBlockUnloaded();
        }

        private void CleanupTick()
        {
            if (Api?.Side == EnumAppSide.Server && tickListenerId != 0)
            {
                Api.World.UnregisterGameTickListener(tickListenerId);
                tickListenerId = 0;
            }
        }

        // ── Input handler ────────────────────────────────────────────

        private bool OnInput(object value, PortKey from)
        {
            if (value is not bool v) return false;

            inputSignals[from] = v;
            bool desired = AnyTrue(inputSignals);

            if (desired != lastInputState)
            {
                lastInputState = desired;
                pending.Enqueue((Api.World.ElapsedMilliseconds + delayMs, desired));
                Blockentity.MarkDirty(true);
            }

            return true;
        }

        public override void OnSourceDisconnected(string inPortId, PortKey from)
        {
            if (inPortId != PortIdIn) return;
            inputSignals.Remove(from);

            bool desired = AnyTrue(inputSignals);
            if (desired != lastInputState)
            {
                lastInputState = desired;
                pending.Enqueue((Api.World.ElapsedMilliseconds + delayMs, desired));
                Blockentity.MarkDirty(true);
            }
        }

        // ── Tick: drain the queue ────────────────────────────────────

        private void OnTick(float dt)
        {
            if (pending.Count == 0) return;

            long now = Api.World.ElapsedMilliseconds;
            bool changed = false;

            while (pending.Count > 0 && pending.Peek().fireAtMs <= now)
            {
                var (_, value) = pending.Dequeue();
                if (value != currentOutput)
                {
                    currentOutput = value;
                    changed = true;
                }
            }

            if (changed)
            {
                SetStateVariant(currentOutput);
                TryEmitBool(PortIdOut, currentOutput, out _);
                Blockentity.MarkDirty(true);
            }
        }

        // ── Block info ───────────────────────────────────────────────

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            dsc.AppendLine($"Delay: {FormatDelay(delayMs)}");
            if (pending.Count > 0)
                dsc.AppendLine($"State: {(currentOutput ? "ON" : "OFF")} ({pending.Count} queued)");
            else
                dsc.AppendLine(currentOutput ? "State: ON" : "State: OFF");
        }

        private static string FormatDelay(int ms)
        {
            if (ms < 1000) return $"{ms}ms";
            if (ms % 1000 == 0) return $"{ms / 1000}s";
            return $"{ms / 1000.0:0.#}s";
        }

        // ── Carry-data settings ──────────────────────────────────────

        public override void WriteSettingsToTree(ITreeAttribute tree)
        {
            base.WriteSettingsToTree(tree);
            tree.SetInt("delay.delayMs", delayMs);
        }

        public override void ReadSettingsFromTree(ITreeAttribute tree)
        {
            base.ReadSettingsFromTree(tree);
            int saved = tree.GetInt("delay.delayMs", 0);
            if (saved > 0) delayMs = saved;
        }

        // ── Persistence ──────────────────────────────────────────────

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            currentOutput = tree.GetBool("delay.output");
            int saved = tree.GetInt("delay.delayMs", 0);
            if (saved > 0) delayMs = saved;
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetBool("delay.output", currentOutput);
            tree.SetInt("delay.delayMs", delayMs);
        }
    }
}
