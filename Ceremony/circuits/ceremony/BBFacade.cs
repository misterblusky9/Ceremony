using Vintagestory.API.Common;
#nullable disable

namespace circuits;

public class BBFacade : BlockBehavior
{
    public BBFacade(Block block) : base(block) { }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
    {
        if (TrySetFacade(world, byPlayer, blockSel))
        {
            handling = EnumHandling.Handled;
            return true;
        }

        if (DoWrenchClick(world, byPlayer, blockSel))
        {
            handling = EnumHandling.Handled;
            return true;
        }

        return base.OnBlockInteractStart(world, byPlayer, blockSel, ref handling);
    }

    public virtual bool DoWrenchClick(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (byPlayer?.InventoryManager == null || !byPlayer.InventoryManager.ActiveTool.HasValue) return false;
        if (byPlayer.InventoryManager.ActiveTool != EnumTool.Wrench) return false;

        BlockEntity be = world.BlockAccessor.GetBlockEntity(blockSel.Position);
        if (be == null) return false;

        BEBFacade beh = be.GetBehavior<BEBFacade>();
        if (beh == null) return false;

        if (byPlayer.Entity.Controls.CtrlKey)
        {
            beh.ToggleFacadeLock();
            return true;
        }

        if (beh.FacadeLocked) return false;

        beh.ClearFacade(triggernewmesh: true);
        return true;
    }

    public virtual bool TrySetFacade(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        BlockEntity be = world.BlockAccessor.GetBlockEntity(blockSel.Position);
        if (be == null || byPlayer?.InventoryManager == null) return false;

        BEBFacade beh = be.GetBehavior<BEBFacade>();
        if (beh == null) return false;

        ItemSlot slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        if (slot == null || slot.Empty) return false;

        Block heldBlock = slot.Itemstack?.Block;
        if (heldBlock == null) return false;

        // Prevent setting facade to the same block type (optional)
        if (heldBlock.Code == block.Code) return false;

        // Call the BEB method you actually implemented
        if (!beh.SetFacade(slot)) return false;

        // Consume in survival
        if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
        {
            slot.Itemstack.StackSize--;
            if (slot.Itemstack.StackSize <= 0) slot.Itemstack = null;
            slot.MarkDirty();
        }

        return true;
    }

}
