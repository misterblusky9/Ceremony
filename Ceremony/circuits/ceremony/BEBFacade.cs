using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

#nullable disable

namespace circuits;

public class BEBFacade : BlockEntityBehavior
{
    protected ItemStack facadeStack;

    protected ICoreClientAPI capi;
    protected ICoreServerAPI sapi;

    protected bool facadeLocked = false;
    protected bool dumped = false;

    protected MeshData meshdata;

    public const int resetmeshpacket = 32323;
    long gmListenerId;
    EnumGameMode? lastGm;

    public bool FacadeLocked => facadeLocked;
    public ItemStack FacadeStack => facadeStack;

    public BEBFacade(BlockEntity blockentity) : base(blockentity) { }

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        base.Initialize(api, properties);

        if (api is ICoreClientAPI clientApi)
        {
            capi = clientApi;

            // Start tracking local gamemode changes
            lastGm = capi.World?.Player?.WorldData?.CurrentGameMode; // :contentReference[oaicite:1]{index=1}
            gmListenerId = capi.Event.RegisterGameTickListener(_ =>
            {
                var gm = capi.World?.Player?.WorldData?.CurrentGameMode; // :contentReference[oaicite:2]{index=2}
                if (gm == null || gm == lastGm) return;

                lastGm = gm;

                // Only need redraw if this BE actually has a facade
                if (facadeStack?.Block != null)
                {
                    // Forces chunk retesselation so OnTesselation runs again
                    capi.World.BlockAccessor.MarkBlockDirty(Pos); // :contentReference[oaicite:3]{index=3}
                }
            }, 200);
        }
        else if (api is ICoreServerAPI serverApi)
        {
            sapi = serverApi;
        }
    }

    public override void OnBlockRemoved()
    {
        if (capi != null && gmListenerId != 0)
        {
            capi.Event.UnregisterGameTickListener(gmListenerId);
            gmListenerId = 0;
        }
        base.OnBlockRemoved();
    }

    public bool SetFacade(ItemSlot fromslot)
    {
        if (facadeLocked) return false;
        if (fromslot == null || fromslot.Empty) return false;

        ItemStack held = fromslot.Itemstack;
        if (held?.Block == null) return false;

        facadeStack = held.Clone();
        facadeStack.StackSize = 1;

        dumped = false;
        meshdata = null;

        Blockentity.MarkDirty(redrawOnClient: true);
        return true;
    }

    public void ToggleFacadeLock()
    {
        facadeLocked = !facadeLocked;
        Blockentity.MarkDirty();
    }

    public void ClearFacade(bool triggernewmesh)
    {
        if (facadeStack == null || dumped) return;

        dumped = true;
        facadeStack = null;
        meshdata = null;

        Blockentity.MarkDirty(redrawOnClient: true);

        if (triggernewmesh && sapi != null)
        {
            sapi.Network.BroadcastBlockEntityPacket(Pos, resetmeshpacket, []);
        }
    }

    public void GenMesh()
    {
        meshdata = null;
        if (capi == null) return;

        Block camo = facadeStack?.Block;
        if (camo == null) return;

        if (camo.Code.ToString() == "game:chiseledblock")
        {
            meshdata = CreateMeshFromChiseledItemStack(capi, facadeStack);
            return;
        }

        meshdata = capi.TesselatorManager.GetDefaultBlockMesh(camo);
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        if (capi != null)
        {
            var gm = capi.World?.Player?.WorldData?.CurrentGameMode;
            if (gm == EnumGameMode.Creative)
            {
                return false;
            }
        }

        if (facadeStack?.Block == null) return base.OnTesselation(mesher, tessThreadTesselator);

        if (meshdata == null) GenMesh();
        if (meshdata == null) return base.OnTesselation(mesher, tessThreadTesselator);

        mesher.AddMeshData(meshdata);
        return true;
    }


    public override void OnReceivedServerPacket(int packetid, byte[] data)
    {
        if (packetid == resetmeshpacket)
        {
            facadeStack = null;
            meshdata = null;
            Blockentity.MarkDirty(redrawOnClient: true);
            return;
        }

        base.OnReceivedServerPacket(packetid, data);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetItemstack("facadeStack", facadeStack);
        tree.SetBool("facadelocked", facadeLocked);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);

        facadeStack = tree.GetItemstack("facadeStack");
        facadeStack?.ResolveBlockOrItem(worldAccessForResolve);

        facadeLocked = tree.GetBool("facadelocked");

        meshdata = null;
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        if (forPlayer?.WorldData?.CurrentGameMode == EnumGameMode.Creative)
        {
            if (facadeStack != null)
            {
                if (facadeLocked)
                {
                    dsc.AppendLine("<font color=#ffff00>Facade Locked</font> (Ctrl click with wrench to unlock)");
                }
                else
                {
                    dsc.AppendLine("<font color=#aaaaaa>Facade Unlocked</font>");
                }
            } else
            {
                dsc.AppendLine("<font color=#ffff00>No Facade</font> (Ctrl click with wrench to unlock)");
            }
        }

        base.GetBlockInfo(forPlayer, dsc);
    }

    public Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        Block b = facadeStack?.Block;
        if (b == null) return null;

        if (b.Code.ToString() == "game:chiseledblock")
        {
            return GetChiseledBoxes(facadeStack);
        }

        return b.SelectionBoxes;
    }

    public Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        Block b = facadeStack?.Block;
        if (b == null) return null;

        if (b.Code.ToString() == "game:chiseledblock")
            return GetChiseledBoxes(facadeStack);

        return b.CollisionBoxes;
    }

    public static MeshData CreateMeshFromChiseledItemStack(ICoreClientAPI capi, ItemStack forStack)
    {
        ITreeAttribute tree = forStack.Attributes ?? new TreeAttribute();

        int[] mats = BlockEntityMicroBlock.MaterialIdsFromAttributes(tree, capi.World);

        uint[] cuboids = (tree["cuboids"] as IntArrayAttribute)?.AsUint
                      ?? (tree["cuboids"] as LongArrayAttribute)?.AsUint;

        List<uint> voxelCuboids = cuboids == null ? [] : [.. cuboids];

        MeshData mesh = BlockEntityMicroBlock.CreateMesh(capi, voxelCuboids, mats, null);
        mesh.Rgba.Fill(byte.MaxValue);
        return mesh;
    }

    public static Cuboidf[] GetChiseledBoxes(ItemStack chiseledStack)
    {
        ITreeAttribute attr = chiseledStack?.Attributes;
        if (attr == null) return null;

        uint[] cuboids = (attr["cuboids"] as IntArrayAttribute)?.AsUint
                      ?? (attr["cuboids"] as LongArrayAttribute)?.AsUint;

        if (cuboids == null || cuboids.Length == 0) return null;

        var boxes = new List<Cuboidf>(cuboids.Length);
        foreach (uint u in cuboids)
        {
            var cwm = new CuboidWithMaterial();
            BlockEntityMicroBlock.FromUint(u, cwm);
            boxes.Add(cwm.ToCuboidf());
        }

        return boxes.ToArray();
    }

    public override void OnPlacementBySchematic(ICoreServerAPI api, IBlockAccessor blockAccessor, BlockPos pos, Dictionary<int, Dictionary<int, int>> replaceBlocks, int centerrockblockid, Block layerBlock, bool resolveImports)
    {
        base.OnPlacementBySchematic(api, blockAccessor, pos, replaceBlocks, centerrockblockid, layerBlock, resolveImports);

        if (facadeStack == null) return;
        facadeStack.ResolveBlockOrItem(api.World);
        if (facadeStack.Block?.Code?.ToString() == "game:chiseledblock" && facadeStack.Attributes != null)
        {
            var intMats = facadeStack.Attributes["materials"] as IntArrayAttribute;
            var longMats = facadeStack.Attributes["materials"] as LongArrayAttribute;

            uint[] mats = intMats?.AsUint ?? longMats?.AsUint;
            if (mats != null && replaceBlocks != null)
            {
                for (int i = 0; i < mats.Length; i++)
                {
                    int oldId = (int)mats[i];

                    if (replaceBlocks.TryGetValue(oldId, out var map) &&
                        map != null &&
                        map.TryGetValue(centerrockblockid, out int newId))
                    {
                        mats[i] = (uint)newId;
                    }
                }

                if (intMats != null) facadeStack.Attributes["materials"] = new IntArrayAttribute(mats.Select(v => (int)v).ToArray());
                else if (longMats != null) facadeStack.Attributes["materials"] = new LongArrayAttribute(mats.Select(v => (long)v).ToArray());
            }
        }

        meshdata = null;
        dumped = false;
        Blockentity.MarkDirty(true);
    }

}