using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace circuits;

public class BlockFacade : Block
{
    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        var be = blockAccessor.GetBlockEntity(pos);
        var beh = be?.GetBehavior<BEBFacade>();
        return beh?.GetSelectionBoxes(blockAccessor, pos) ?? base.GetSelectionBoxes(blockAccessor, pos);
    }

    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        var be = blockAccessor.GetBlockEntity(pos);
        var beh = be?.GetBehavior<BEBFacade>();
        return beh?.GetCollisionBoxes(blockAccessor, pos) ?? base.GetCollisionBoxes(blockAccessor, pos);
    }


    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        var be = world.BlockAccessor.GetBlockEntity(pos);
        var beh = be?.GetBehavior<BEBFacade>();

        if (beh?.FacadeStack?.Block != null)
        {
            if (world.Side == EnumAppSide.Client)
            {
                var capi = (world as IClientWorldAccessor)?.Api as ICoreClientAPI;
                if (capi?.World?.Player?.WorldData?.CurrentGameMode == EnumGameMode.Creative)
                {
                    return base.GetPlacedBlockName(world, pos);
                }
            }

            return beh.FacadeStack.GetName(); // localized item/block name
        }

        return base.GetPlacedBlockName(world, pos);
    }

}
