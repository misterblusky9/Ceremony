using HarmonyLib;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

#nullable disable

namespace circuits
{
    public class CircuitTrapdoorBehavior : CircuitBehavior
    {
        public const string PortIdOpen = "trapdoor.open";

        private static readonly AssetLocation WandCode = new("circuits:circuitwand");
        private static readonly AssetLocation WrenchCode = new("circuits:circuitwrench");

        private readonly Dictionary<PortKey, bool> inputSignals = [];
        private CircuitsModSystem mgr;

        public CircuitTrapdoorBehavior(BlockEntity be) : base(be) { }

        public override void Initialize(ICoreAPI api, Vintagestory.API.Datastructures.JsonObject properties)
        {
            base.Initialize(api, properties);
            mgr = api.ModLoader.GetModSystem<CircuitsModSystem>();
        }

        protected override IEnumerable<PortSpec> BuildPorts()
        {
            yield return new PortSpec(
                new PortDef
                {
                    PortID = PortIdOpen,
                    Dir = PortDir.In,
                    Type = SignalType.Bool,
                    MaxInputs = int.MaxValue,
                    DisplayName = "Trapdoor Open/Closed"
                },
                new System.Func<object, PortKey, bool>(OnSetOpen)
            );
        }

        protected override void ConfigureSettings(NodeSettingsDescriptor descriptor)
        {
            descriptor.Title = "Trapdoor Settings";
            descriptor.AddItemSlot("keyItem", "Key Item");
        }

        public bool HandleManualToggle(IPlayer byPlayer)
        {
            var heldCode = byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible?.Code;

            // Let circuit tools interact with the circuit layer instead of opening the trapdoor.
            if (heldCode != null && (heldCode.Equals(WandCode) || heldCode.Equals(WrenchCode)))
            {
                return false;
            }

            // If circuit-controlled, block manual toggling server-side.
            if (Api.Side == EnumAppSide.Server && IsLinked())
            {
                return false;
            }

            var requiredStack = GetItemSlotStack("keyItem");
            if (requiredStack == null)
            {
                return true;
            }

            var held = byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            bool match = held != null && requiredStack.Equals(Api.World, held);

            if (!match && byPlayer is IServerPlayer sp)
            {
                string type = requiredStack.Class == EnumItemClass.Block ? "block" : "item";
                string code = requiredStack.Collectible.Code.ToString();

                sp.SendMessage(
                    GlobalConstants.GeneralChatGroup,
                    $"Requires: <itemstack floattype=\"none\" type=\"{type}\" code=\"{code}\" rsize=\"1.0\" offx=\"0\" offy=\"0\">{requiredStack.GetName()}</itemstack>",
                    EnumChatType.Notification
                );
            }

            return match;
        }

        private bool IsLinked()
        {
            if (mgr == null) return false;

            foreach (var link in mgr.GetLinks())
            {
                if (link.To.NodeID == NodeID && link.To.PortID == PortIdOpen)
                {
                    return true;
                }
            }

            return false;
        }

        private bool OnSetOpen(object value, PortKey from)
        {
            if (value is not bool v) return false;

            inputSignals[from] = v;
            return ApplyOpen(AnyTrue(inputSignals));
        }

        private bool ApplyOpen(bool open)
        {
            var trapdoor = Blockentity.GetBehavior<BEBehaviorTrapDoor>();
            if (trapdoor == null) return false;

            if (trapdoor.Opened == open) return true;

            trapdoor.ToggleDoorState(null, open);
            return true;
        }

        public override void OnSourceDisconnected(string inPortId, PortKey from)
        {
            if (inPortId != PortIdOpen) return;

            if (inputSignals.Remove(from))
            {
                ApplyOpen(AnyTrue(inputSignals));
            }
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);

            var keyStack = GetItemSlotStack("keyItem");
            if (keyStack != null)
            {
                dsc.AppendLine($"Requires: {keyStack.GetName()}");
            }
        }

        [HarmonyPatch(typeof(BEBehaviorTrapDoor), "ToggleDoorState")]
        static class Patch_TrapdoorToggle
        {
            static bool Prefix(BEBehaviorTrapDoor __instance, IPlayer byPlayer)
            {
                // Circuit/system toggles pass null. Do not block those.
                if (byPlayer == null) return true;

                var circuitbehavior = __instance.Blockentity?.GetBehavior<CircuitTrapdoorBehavior>();
                if (circuitbehavior == null) return true;

                return circuitbehavior.HandleManualToggle(byPlayer);
            }
        }
    }
}