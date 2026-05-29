using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
#nullable disable

namespace circuits
{
    public class CustomLocatorProps
    {
        public string WaypointText = "";
        public string WaypointIcon = "x";
        public int WaypointColorSwatch = 15727967; // Yellow Default
        public Vec3i WaypointPos = new(0, 0, 0);
        public bool IsWritten = false;
    }

    public class ItemCustomLocatorMap : Item
    {
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
            if (!firstEvent || slot.Empty) return;

            handling = EnumHandHandling.Handled;
            var wmm = api.ModLoader.GetModSystem<WorldMapManager>();
            var wml = wmm.MapLayers.FirstOrDefault(ml => ml is WaypointMapLayer) as WaypointMapLayer;

            var attr = slot.Itemstack.Attributes;
            var props = ReadProps(attr);

            if (api.Side == EnumAppSide.Client)
            {
                if (wml == null) return;
                if (!props.IsWritten && blockSel != null)
                {
                    var capi = api as ICoreClientAPI;
                    var channel = api.ModLoader.GetModSystem<CircuitsModSystem>().ClientChannel;
                    string currentVariant = slot.Itemstack.Item.Variant["type"] ?? "map-blank";

                    var dlg = new GuiDialogLocatorEditor(capi, wml, props, currentVariant, (newProps, variantType) =>
                    {
                        channel.SendPacket(new EditLocatorPacket
                        {
                            WaypointText = newProps.WaypointText,
                            WaypointIcon = newProps.WaypointIcon,
                            WaypointColorSwatch = newProps.WaypointColorSwatch,
                            VariantType = variantType
                        });
                    });
                    dlg.TryOpen();
                }

                return;
            }

            if (((EntityPlayer)byEntity)?.Player is not IServerPlayer player) return;

            if (!props.IsWritten)
            {
                if (blockSel != null)
                {
                    props.WaypointPos = new Vec3i(blockSel.Position.X, blockSel.Position.Y, blockSel.Position.Z);
                    props.IsWritten = true;
                    WriteProps(attr, props);
                    slot.MarkDirty();
                }
                return;
            }
            else
            {
                Vec3d pos = new(props.WaypointPos.X + 0.5, props.WaypointPos.Y + 0.5, props.WaypointPos.Z + 0.5);

                Vec3d travelVector = pos;
                travelVector -= byEntity.Pos.XYZ;
                travelVector.Y = 0;

                if (!byEntity.World.Config.GetBool("allowMap", true) || wml == null)
                {
                    player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("{0} blocks distance", (int)travelVector.Length()), EnumChatType.Notification);
                    return;
                }

                string puid = ((EntityPlayer)byEntity).PlayerUID;
                if (wml.Waypoints.Any(wp => wp.OwningPlayerUid == puid && wp.Position == pos))
                {
                    player.SendMessage(GlobalConstants.GeneralChatGroup,
                        Lang.Get("Location {0} already marked on your map... {1} blocks distance", Lang.Get(props.WaypointText), (int)travelVector.Length()),
                        EnumChatType.Notification);
                    return;
                }

                wml.AddWaypoint(new Waypoint
                {
                    Color = props.WaypointColorSwatch,
                    Icon = props.WaypointIcon,
                    Pinned = true,
                    Position = pos,
                    OwningPlayerUid = puid,
                    Title = Lang.Get(props.WaypointText)
                }, player);

                string msg = Lang.Get("Location of {0} added to your world map", Lang.Get(props.WaypointText));
                player.SendMessage(GlobalConstants.GeneralChatGroup, msg, EnumChatType.Notification);
                slot.MarkDirty();

            }
            return;
        }

        public static CustomLocatorProps ReadProps(ITreeAttribute tree)
        {
            var p = new CustomLocatorProps();
            p.WaypointText = tree.GetString("WaypointText", p.WaypointText);
            p.WaypointIcon = tree.GetString("WaypointIcon", p.WaypointIcon);
            p.WaypointColorSwatch = tree.GetInt("WaypointColorSwatch", p.WaypointColorSwatch);
            p.IsWritten = tree.GetBool("IsWritten", p.IsWritten);

            var posTree = tree.GetTreeAttribute("WaypointPos");
            p.WaypointPos = posTree != null
                ? new Vec3i(posTree.GetInt("X"), posTree.GetInt("Y"), posTree.GetInt("Z"))
                : p.WaypointPos;

            return p;
        }

        public static void WriteProps(ITreeAttribute tree, CustomLocatorProps p)
        {
            tree.SetString("WaypointText", p.WaypointText);
            tree.SetString("WaypointIcon", p.WaypointIcon);
            tree.SetInt("WaypointColorSwatch", p.WaypointColorSwatch);
            tree.SetBool("IsWritten", p.IsWritten);

            var posTree = tree.GetOrAddTreeAttribute("WaypointPos");
            posTree.SetInt("X", p.WaypointPos.X);
            posTree.SetInt("Y", p.WaypointPos.Y);
            posTree.SetInt("Z", p.WaypointPos.Z);
        }

        public override string GetHeldItemName(ItemStack itemStack)
        {
            // Read stored name
            var tree = itemStack.Attributes;

            bool written = tree.GetBool("IsWritten", false);
            if (!written)
            {
                return Lang.Get("circuits:item-customlocatormap-blank-name");
            }

            string wpName = tree.GetString("WaypointText", null);

            if (string.IsNullOrWhiteSpace(wpName))
            {
                wpName = Lang.Get("circuits:item-customlocatormap-blank-name");
            }

            // Localized template
            return Lang.Get("circuits:item-customlocatormap-name", wpName);
        }

    }
}