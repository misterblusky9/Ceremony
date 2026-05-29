using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

#nullable disable

namespace circuits
{
    /// <summary>
    /// Adds Ceremony circuit triggering to a vanilla command block entity.
    ///
    /// Expected block entity:
    ///   BlockEntityGuiConfigurableCommands
    ///
    /// Signal behavior:
    ///   Bool input rising edge -> run the command block's configured commands.
    /// </summary>
    public class CircuitCommandBehavior : CircuitBehavior
    {
        public const string PortIdTrigger = "command.trigger";

        private const string TreeKeyInputCount = "cnetcmd.input.count";
        private const string TreeKeyInputNodePrefix = "cnetcmd.input.node.";
        private const string TreeKeyInputPortPrefix = "cnetcmd.input.port.";
        private const string TreeKeyInputValuePrefix = "cnetcmd.input.value.";

        private readonly Dictionary<PortKey, bool> inputStates = new();

        public CircuitCommandBehavior(BlockEntity be) : base(be) { }

        protected override IEnumerable<PortSpec> BuildPorts()
        {
            yield return new PortSpec(
                new PortDef
                {
                    PortID = PortIdTrigger,
                    Dir = PortDir.In,
                    Type = SignalType.Bool,
                    MaxInputs = int.MaxValue,
                    DisplayName = "Run"
                },
                OnTriggerSignal,
                requireTrueForBool: false
            );
        }

        private bool OnTriggerSignal(object value, PortKey from)
        {
            if (value is not bool isOn) return false;

            inputStates.TryGetValue(from, out bool wasOn);
            inputStates[from] = isOn;

            // Persist last input states so a loaded chunk does not re-run
            // commands just because the circuit manager re-delivers a stored true.
            Blockentity.MarkDirty(true);

            if (isOn && !wasOn)
            {
                return ExecuteCommandBlock();
            }

            return true;
        }

        private bool ExecuteCommandBlock()
        {
            if (Api?.Side != EnumAppSide.Server) return true;

            if (Blockentity is not BlockEntityGuiConfigurableCommands cmdBe)
            {
                Api.Logger.Warning(
                    "CNetCommand at {0} is attached to {1}, not BlockEntityGuiConfigurableCommands",
                    Pos,
                    Blockentity?.GetType().Name ?? "null"
                );
                return false;
            }

            if (string.IsNullOrWhiteSpace(cmdBe.Commands))
            {
                return true;
            }

            var caller = new Caller
            {
                Type = EnumCallerType.Block,
                Pos = Pos.ToVec3d(),
                CallerPrivileges = cmdBe.CallingPrivileges
            };

            cmdBe.Execute(caller, cmdBe.Commands);
            return true;
        }

        public override void OnSourceDisconnected(string inPortId, PortKey from)
        {
            if (inPortId == PortIdTrigger && inputStates.Remove(from))
            {
                Blockentity.MarkDirty(true);
                return;
            }

            base.OnSourceDisconnected(inPortId, from);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);

            inputStates.Clear();

            int count = tree.GetInt(TreeKeyInputCount, 0);
            for (int i = 0; i < count; i++)
            {
                string nodeIdString = tree.GetString(TreeKeyInputNodePrefix + i);
                string portId = tree.GetString(TreeKeyInputPortPrefix + i);

                if (string.IsNullOrEmpty(nodeIdString)) continue;
                if (string.IsNullOrEmpty(portId)) continue;
                if (!Guid.TryParse(nodeIdString, out Guid nodeId)) continue;

                bool value = tree.GetBool(TreeKeyInputValuePrefix + i, false);
                inputStates[new PortKey(nodeId, portId)] = value;
            }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);

            tree.SetInt(TreeKeyInputCount, inputStates.Count);

            int i = 0;
            foreach (var kvp in inputStates)
            {
                tree.SetString(TreeKeyInputNodePrefix + i, kvp.Key.NodeID.ToString("N"));
                tree.SetString(TreeKeyInputPortPrefix + i, kvp.Key.PortID);
                tree.SetBool(TreeKeyInputValuePrefix + i, kvp.Value);
                i++;
            }
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            dsc.AppendLine("Circuit: runs commands on rising signal");
        }
    }
}