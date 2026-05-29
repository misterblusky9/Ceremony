using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace circuits;
public class ItemCircuitWand : Item
{
    public override void OnHeldAttackStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandHandling handling) { handling = EnumHandHandling.PreventDefault; }
    public override bool OnHeldAttackStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel) { return false; }
    public override void OnHeldAttackStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel) { }

    public override void OnHeldInteractStart(
        ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel,
        bool firstEvent, ref EnumHandHandling handling)
    {
        if (!firstEvent) return;

        handling = EnumHandHandling.PreventDefault;

        if (api.Side != EnumAppSide.Server) return;

        var sapi = api as ICoreServerAPI;
        var player = (byEntity as EntityPlayer)?.Player as IServerPlayer;
        if (sapi == null || player == null) return;

        if (!player.HasPrivilege(Privilege.controlserver)) return;

        if (blockSel?.Position == null) return;

        var be = sapi.World.BlockAccessor.GetBlockEntity(blockSel.Position);
        if (be?.Behaviors == null) return;

        var node = be.Behaviors.OfType<CircuitBehavior>().FirstOrDefault();
        if (node == null) return;

        var ms = sapi.ModLoader.GetModSystem<CircuitsModSystem>();
        ms?.RegisterOrUpdateNode(node.NodeID, be.Pos);
        ms?.SendGlobalSnapshot(player);

        player.SendMessage(GlobalConstants.GeneralChatGroup,
            $"Node: {node.NodeID}", EnumChatType.Notification);
    }
}
