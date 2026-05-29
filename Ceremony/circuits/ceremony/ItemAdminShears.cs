using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

#nullable disable

namespace circuits;

public class ItemAdminShears : ItemShears
{
    private const int MaxConnectedBreaks = 256;

    public override int MultiBreakQuantity => MaxConnectedBreaks;

    public override void DamageItem(
        IWorldAccessor world,
        Entity byEntity,
        ItemSlot itemSlot,
        int amount = 1,
        bool destroyOnZeroDurability = true
    )
    {
        itemSlot?.MarkDirty();
    }

    public override bool CanMultiBreak(Block block)
    {
        return block != null
            && block.BlockId != 0
            && block.BlockMaterial == EnumBlockMaterial.Leaves;
    }

    public override bool OnBlockBrokenWith(
        IWorldAccessor world,
        Entity byEntity,
        ItemSlot itemslot,
        BlockSelection blockSel,
        float dropQuantityMultiplier = 1f
    )
    {
        if (world.Side != EnumAppSide.Server) return true;
        if (blockSel?.Position == null) return true;
        if (byEntity is not EntityPlayer entityPlayer) return true;

        IPlayer player = world.PlayerByUid(entityPlayer.PlayerUID);
        if (player == null) return true;

        Block startBlock = world.BlockAccessor.GetBlock(blockSel.Position);
        if (!CanMultiBreak(startBlock)) return true;

        string targetLeafGroup = startBlock.Attributes?["treeFellingGroupCode"].AsString(null);

        List<BlockPos> leaves = FindConnectedLeaves(
            world,
            blockSel.Position.Copy(),
            targetLeafGroup
        );

        List<BlockPos> attachments = FindCanopyAttachmentsNearLeaves(world, leaves);

        BreakPositions(world, player, attachments, dropQuantityMultiplier);
        BreakPositions(world, player, leaves, dropQuantityMultiplier);

        return true;
    }

    private List<BlockPos> FindConnectedLeaves(
        IWorldAccessor world,
        BlockPos startPos,
        string targetLeafGroup
    )
    {
        Queue<BlockPos> queue = new();
        HashSet<BlockPos> visited = new();
        List<BlockPos> found = new();

        queue.Enqueue(startPos.Copy());
        visited.Add(startPos.Copy());

        while (queue.Count > 0 && found.Count < MaxConnectedBreaks)
        {
            BlockPos pos = queue.Dequeue();
            Block block = world.BlockAccessor.GetBlock(pos);

            if (!MatchesTargetLeaf(block, targetLeafGroup)) continue;

            found.Add(pos.Copy());

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;

                        BlockPos npos = pos.AddCopy(dx, dy, dz);
                        if (visited.Contains(npos)) continue;

                        Block nblock = world.BlockAccessor.GetBlock(npos);
                        if (!MatchesTargetLeaf(nblock, targetLeafGroup)) continue;

                        visited.Add(npos);
                        queue.Enqueue(npos);
                    }
                }
            }
        }

        return found;
    }
    private bool MatchesTargetLeaf(Block block, string targetLeafGroup)
    {
        if (!CanMultiBreak(block)) return false;

        string group = block.Attributes?["treeFellingGroupCode"].AsString(null);

        if (targetLeafGroup != null)
        {
            return group == targetLeafGroup;
        }

        return group == null;
    }

    private void BreakPositions(
        IWorldAccessor world,
        IPlayer player,
        List<BlockPos> positions,
        float dropQuantityMultiplier
    )
    {
        bool bypassClaims = HasAdminBypass(player);

        foreach (BlockPos pos in positions)
        {
            if (!bypassClaims && !world.Claims.TryAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak))
            {
                continue;
            }

            world.BlockAccessor.BreakBlock(pos, player, dropQuantityMultiplier);
            world.BlockAccessor.MarkBlockDirty(pos);
        }
    }

    private bool CanAdminBreakCanopyAttachment(Block block)
    {
        if (block == null || block.BlockId == 0) return false;
        if (block.IsLiquid()) return false;

        string path = block.Code?.Path ?? "";

        return block.BlockMaterial == EnumBlockMaterial.Plant
            || path.Contains("vine")
            || path.Contains("moss")
            || path.Contains("lichen");
    }

    private List<BlockPos> FindCanopyAttachmentsNearLeaves(
        IWorldAccessor world,
        List<BlockPos> leaves
    )
    {
        HashSet<BlockPos> visited = new();
        List<BlockPos> found = new();

        foreach (BlockPos leafPos in leaves)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;

                        BlockPos pos = leafPos.AddCopy(dx, dy, dz);
                        if (visited.Contains(pos)) continue;

                        visited.Add(pos);

                        Block block = world.BlockAccessor.GetBlock(pos);
                        if (CanAdminBreakCanopyAttachment(block))
                        {
                            found.Add(pos.Copy());
                        }
                    }
                }
            }
        }

        return found;
    }

    private static bool HasAdminBypass(IPlayer player)
    {
        if (player?.WorldData?.CurrentGameMode == EnumGameMode.Creative) return true;

        return player is IServerPlayer serverPlayer
            && serverPlayer.HasPrivilege(Privilege.controlserver);
    }
}