using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

#nullable disable

namespace circuits;

public class ItemAdminAxe : ItemAxe
{
    private const int MaxWoodBlocks = 4096;
    private const int MaxLeafBlocks = 12000;

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

    public override float OnBlockBreaking(
        IPlayer player,
        BlockSelection blockSel,
        ItemSlot itemslot,
        float remainingResistance,
        float dt,
        int counter
    )
    {
        // Skip vanilla tree-resistance scaling. Admin axe breaks normally/fast.
        return base.OnBlockBreaking(player, blockSel, itemslot, remainingResistance, dt * 999f, counter);
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

        BlockPos clickedPos = blockSel.Position.Copy();
        Block startBlock = world.BlockAccessor.GetBlock(clickedPos);
        BlockPos treeStartPos = clickedPos.Copy();

        bool clickedProxy = IsVineBlock(startBlock) || CanAdminFellLeaves(startBlock);

        if (!CanAdminFellWood(startBlock))
        {
            if (!clickedProxy || !TryFindNearbyTreeWood(world, clickedPos, out treeStartPos, out startBlock))
            {
                return true;
            }
        }

        string targetTreeGroup = startBlock.Attributes?["treeFellingGroupCode"].AsString(null);

        List<BlockPos> wood = FindConnectedWood(
            world,
            treeStartPos.Copy(),
            targetTreeGroup
        );

        List<BlockPos> leaves = FindConnectedLeavesNearWood(
            world,
            wood,
            targetTreeGroup
        );
        List<BlockPos> attachments = FindCanopyAttachmentsNearLeaves(world, leaves);

        if (clickedProxy)
        {
            attachments.Add(clickedPos.Copy());
        }

        BreakPositions(world, player, attachments, dropQuantityMultiplier);
        BreakPositions(world, player, leaves, 0.25f);
        BreakPositions(world, player, wood, dropQuantityMultiplier);

        return true;
    }

    private bool IsVineBlock(Block block)
    {
        string path = block?.Code?.Path ?? "";
        return block != null
            && block.BlockId != 0
            && path.Contains("vine");
    }

    private bool TryFindNearbyTreeWood(
        IWorldAccessor world,
        BlockPos clickedPos,
        out BlockPos woodPos,
        out Block woodBlock
    )
    {
        woodPos = null;
        woodBlock = null;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;

                    BlockPos pos = clickedPos.AddCopy(dx, dy, dz);
                    Block block = world.BlockAccessor.GetBlock(pos);

                    if (!CanAdminFellWood(block)) continue;

                    woodPos = pos.Copy();
                    woodBlock = block;
                    return true;
                }
            }
        }

        return false;
    }

    private bool CanAdminFellWood(Block block)
    {
        if (block == null || block.BlockId == 0) return false;
        if (block.Attributes?["treeFellingCanChop"].AsBool(true) == false) return false;

        // Safer than "all wood", because planks/fences/buildings are also wood material.
        string path = block.Code?.Path ?? "";

        return block.BlockMaterial == EnumBlockMaterial.Wood
            && (
                block.Attributes?["treeFellingGroupCode"].AsString(null) != null
                || path.Contains("log")
                || path.Contains("branch")
                || path.Contains("bamboo")
            );
    }

    private bool CanAdminFellLeaves(Block block)
    {
        return block != null
            && block.BlockId != 0
            && block.BlockMaterial == EnumBlockMaterial.Leaves;
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

    private List<BlockPos> FindConnectedWood(
        IWorldAccessor world,
        BlockPos startPos,
        string targetTreeGroup
    )
    {
        Queue<BlockPos> queue = new();
        HashSet<BlockPos> visited = new();
        List<BlockPos> found = new();

        queue.Enqueue(startPos.Copy());
        visited.Add(startPos.Copy());

        while (queue.Count > 0 && found.Count < MaxWoodBlocks)
        {
            BlockPos pos = queue.Dequeue();
            Block block = world.BlockAccessor.GetBlock(pos);

            if (!MatchesTargetTreeWood(block, targetTreeGroup)) continue;

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
                        if (!MatchesTargetTreeWood(nblock, targetTreeGroup)) continue;

                        visited.Add(npos);
                        queue.Enqueue(npos);
                    }
                }
            }
        }

        return found;
    }
    private bool MatchesTargetTreeWood(Block block, string targetTreeGroup)
    {
        if (block == null || block.BlockId == 0) return false;
        if (block.Attributes?["treeFellingCanChop"].AsBool(true) == false) return false;

        string group = block.Attributes?["treeFellingGroupCode"].AsString(null);

        if (targetTreeGroup != null)
        {
            return group == targetTreeGroup
                && block.BlockMaterial == EnumBlockMaterial.Wood;
        }

        // Fallback for modded logs with no tree-felling metadata.
        string path = block.Code?.Path ?? "";

        return block.BlockMaterial == EnumBlockMaterial.Wood
            && (
                path.Contains("log")
                || path.Contains("branch")
                || path.Contains("bamboo")
            );
    }

    private bool MatchesTargetTreeLeaf(Block block, string targetTreeGroup)
    {
        if (block == null || block.BlockId == 0) return false;
        if (block.BlockMaterial != EnumBlockMaterial.Leaves) return false;

        string group = block.Attributes?["treeFellingGroupCode"].AsString(null);

        if (targetTreeGroup != null)
        {
            return group != null
                && group.Length == targetTreeGroup.Length + 1
                && group.EndsWith(targetTreeGroup);
        }

        // Fallback for modded leaves with no metadata.
        return group == null;
    }

    private List<BlockPos> FindConnectedLeavesNearWood(
        IWorldAccessor world,
        List<BlockPos> wood,
        string targetTreeGroup
    )
    {
        Queue<BlockPos> queue = new();
        HashSet<BlockPos> visited = new();
        List<BlockPos> found = new();

        foreach (BlockPos woodPos in wood)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        BlockPos pos = woodPos.AddCopy(dx, dy, dz);
                        if (visited.Contains(pos)) continue;

                        Block block = world.BlockAccessor.GetBlock(pos);
                        if (!MatchesTargetTreeLeaf(block, targetTreeGroup)) continue;

                        visited.Add(pos);
                        queue.Enqueue(pos);
                    }
                }
            }
        }

        while (queue.Count > 0 && found.Count < MaxLeafBlocks)
        {
            BlockPos pos = queue.Dequeue();
            Block block = world.BlockAccessor.GetBlock(pos);

            if (!MatchesTargetTreeLeaf(block, targetTreeGroup)) continue;

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
                        if (!MatchesTargetTreeLeaf(nblock, targetTreeGroup)) continue;

                        visited.Add(npos);
                        queue.Enqueue(npos);
                    }
                }
            }
        }

        return found;
    }

    private void BreakPositions(
        IWorldAccessor world,
        IPlayer player,
        List<BlockPos> positions,
        float dropQuantityMultiplier
    )
    {
        foreach (BlockPos pos in positions)
        {
            if (!world.Claims.TryAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak))
            {
                continue;
            }

            world.BlockAccessor.BreakBlock(pos, player, dropQuantityMultiplier);
            world.BlockAccessor.MarkBlockDirty(pos);
        }
    }
}