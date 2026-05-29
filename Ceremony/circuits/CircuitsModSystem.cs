using cbnormalizer;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
#nullable disable

namespace circuits
{
    public enum SignalType { Bool, Int, Float, String, Event }
    public enum PortDir { In, Out }

    public sealed class PortDef
    {
        public required string PortID { get; init; }
        public required PortDir Dir { get; init; }
        public required SignalType Type { get; init; }
        public int MaxInputs { get; init; } = 1;
        public int MaxOutputs { get; init; } = int.MaxValue;
        public string DisplayName { get; init; }
    }

    public readonly record struct PortKey(Guid NodeID, string PortID);

    public sealed class Link : IEquatable<Link>
    {
        public required PortKey From { get; init; }
        public required PortKey To { get; init; }

        public required BlockPos FromPos { get; set; }
        public required BlockPos ToPos { get; set; }
        public bool HasPositions { get; set; }

        public bool Equals(Link other)
            => other != null && From.Equals(other.From) && To.Equals(other.To);

        public override bool Equals(object obj) => obj is Link l && Equals(l);
        public override int GetHashCode() => HashCode.Combine(From, To);
    }

    public sealed class NodeRef
    {
        public required Guid NodeID { get; init; }
        public required BlockPos Pos { get; set; }
        public Vec3f RenderOffset { get; set; } = new Vec3f(0, 0, 0);
    }

    public interface IBaseNode
    {
        Guid NodeID { get; }
        BlockPos Pos { get; }
        IEnumerable<PortDef> GetPorts();
    }

    public interface INodePortsProvider
    {
        IEnumerable<PortDef> GetPorts();
    }

    public interface ISignalReceiver
    {
        bool OnSignal(string inPortId, SignalType type, object value, PortKey from);
        void OnSourceDisconnected(string inPortId, PortKey from);
    }

    public class CircuitsModSystem : ModSystem
    {
        private readonly HashSet<Link> links = [];
        private readonly Dictionary<Guid, NodeRef> nodesByID = [];
        private readonly Dictionary<Guid, Dictionary<string, PortDef>> portsByNode = [];
        private readonly Dictionary<PortKey, int> incomingCount = [];
        private readonly Dictionary<PortKey, (SignalType type, object value)> lastOutputValues = [];

        ICoreServerAPI sapi;
        ICoreClientAPI capi;

        IServerNetworkChannel sch;
        IClientNetworkChannel cch;

        public IServerNetworkChannel ServerChannel => sch;
        public IClientNetworkChannel ClientChannel => cch;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            api.RegisterBlockEntityBehaviorClass("CNetDoor", typeof(CircuitDoorBehavior));
            api.RegisterBlockEntityBehaviorClass("CNetVariants", typeof(CircuitVariantBehavior));
            api.RegisterBlockEntityBehaviorClass("CNetMemory", typeof(CircuitVariantBehavior));
            api.RegisterBlockEntityBehaviorClass("CNetGate", typeof(CircuitGateBehavior));
            api.RegisterBlockEntityBehaviorClass("CNetPressureSensor", typeof(CircuitPressureSensorBehavior));
            api.RegisterBlockEntityBehaviorClass("CNetLever", typeof(CircuitLeverBehavior));
            api.RegisterBlockEntityBehaviorClass("CNetAnimLever", typeof(CircuitLeverBehavior));
            api.RegisterBlockEntityBehaviorClass("CNetButton", typeof(CircuitButtonBehavior));
            api.RegisterBlockEntityBehaviorClass("CNetAnimButton", typeof(CircuitButtonBehavior));
            api.RegisterBlockEntityBehaviorClass("CNetDelay", typeof(CircuitDelayBehavior));
            api.RegisterBlockEntityBehaviorClass("CNetSRLatch", typeof(CircuitSRLatchBehavior));
            api.RegisterBlockEntityBehaviorClass("CNetClock", typeof(CircuitClockBehavior));
            api.RegisterBlockEntityBehaviorClass("CNetEffectorLightning", typeof(CircuitEffectorLightning));
            api.RegisterBlockEntityBehaviorClass("CNetProximitySensor", typeof(CircuitProximitySensor));
            api.RegisterBlockBehaviorClass("CNetDeferInteract", typeof(BBDeferInteract));
            api.RegisterItemClass("CircuitWand", typeof(ItemCircuitWand));
            api.RegisterItemClass("CircuitWrench", typeof(ItemCircuitWrench));

            // Ceremony
            api.RegisterItemClass("customlocatormap", typeof(ItemCustomLocatorMap));
            api.RegisterItemClass("AdminShears", typeof(ItemAdminShears));
            api.RegisterItemClass("AdminAxe", typeof(ItemAdminAxe));
            api.RegisterBlockEntityClass("BEFacade", typeof(BEFacade));
            api.RegisterBlockEntityBehaviorClass("BEBFacade", typeof(BEBFacade));
            api.RegisterBlockClass("BlockFacade", typeof(BlockFacade));
            api.RegisterBlockBehaviorClass("BBFacade", typeof(BBFacade)); 
            api.RegisterBlockEntityClass("landmine", typeof(BlockEntityLandmine));

            new Harmony("circuits").PatchAll();

            if (api.Side == EnumAppSide.Server)
            {
                sapi = (ICoreServerAPI)api;

                sch = sapi.Network.RegisterChannel("circuits")
                    .RegisterMessageType<CircuitsLinkDelta>()
                    .RegisterMessageType<CircuitsSnapshot>()
                    .RegisterMessageType<CircuitsRequestLink>()
                    .RegisterMessageType<CircuitsRequestClearPort>()
                    .RegisterMessageType<CircuitsRequestSnapshot>()
                    .RegisterMessageType<EditLocatorPacket>();
            }
            else
            {
                capi = (ICoreClientAPI)api;

                cch = capi.Network.RegisterChannel("circuits")
                    .RegisterMessageType<CircuitsLinkDelta>()
                    .RegisterMessageType<CircuitsSnapshot>()
                    .RegisterMessageType<CircuitsRequestLink>()
                    .RegisterMessageType<CircuitsRequestClearPort>()
                    .RegisterMessageType<CircuitsRequestSnapshot>()
                    .RegisterMessageType<EditLocatorPacket>();
            }
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            api.Event.SaveGameLoaded += OnSaveGameLoaded;
            api.Event.GameWorldSave += OnGameWorldSave;

            var parsers = api.ChatCommands.Parsers;

            api.ChatCommands.GetOrCreate("circuits")
                .WithDesc("Circuits gamedev tools")
                .RequiresPrivilege(Privilege.controlserver)
                    .BeginSubCommand("listlinks")
                        .WithDesc("List all links")
                        .HandleWith(OnListLinks)
                    .EndSubCommand()
                    .BeginSubCommand("emit")
                        .WithDesc("Emit a test signal from the looked-at node (bool only for now)")
                        .WithArgs(parsers.Word("outPortId"), parsers.Bool("value"))
                        .HandleWith(OnEmitBool)
                    .EndSubCommand()
                    .BeginSubCommand("selectout")
                        .WithDesc("Select an output port on the looked-at node")
                        .WithArgs(parsers.Word("outPortId"))
                        .HandleWith(OnSelectOut)
                    .EndSubCommand()
                    .BeginSubCommand("linkto")
                        .WithDesc("Create link from selected output to an input port on looked-at node")
                        .WithArgs(parsers.Word("inPortId"))
                        .HandleWith(OnLinkTo)
                    .EndSubCommand()
                    .BeginSubCommand("listports")
                        .WithDesc("List ports on the looked-at node")
                        .HandleWith(OnListPorts)
                    .EndSubCommand()
                .Validate();

            sch.SetMessageHandler<CircuitsRequestLink>(OnRequestLink);
            sch.SetMessageHandler<CircuitsRequestClearPort>(OnRequestClearPort);
            sch.SetMessageHandler<CircuitsRequestSnapshot>(OnRequestSnapshot);
            sch.SetMessageHandler<EditLocatorPacket>(OnEditLocatorPacket);
        }

        const string SaveKey = "circuits:savedata";
        bool saveDirty;

        void MarkDirty() => saveDirty = true;

        void OnSaveGameLoaded()
        {
            links.Clear();
            incomingCount.Clear();

            byte[] data = sapi.WorldManager.SaveGame.GetData(SaveKey);
            if (data == null || data.Length == 0) return;

            CircuitsSaveDto dto;
            using (var ms = new System.IO.MemoryStream(data))
                dto = ProtoBuf.Serializer.Deserialize<CircuitsSaveDto>(ms);

            foreach (var l in dto.Links)
            {
                if (!Guid.TryParse(l.FromNodeIdN, out var fromNode)) continue;
                if (!Guid.TryParse(l.ToNodeIdN, out var toNode)) continue;

                links.Add(new Link
                {
                    From = new PortKey(fromNode, l.FromPortId),
                    To = new PortKey(toNode, l.ToPortId),
                    FromPos = l.HasPositions ? new BlockPos(l.FromX, l.FromY, l.FromZ, l.FromDim) : new BlockPos(0, 0, 0),
                    ToPos = l.HasPositions ? new BlockPos(l.ToX, l.ToY, l.ToZ, l.ToDim) : new BlockPos(0, 0, 0),
                    HasPositions = l.HasPositions
                });
            }

            RebuildIncomingCounts();

            if (dto.OutputValues != null)
            {
                foreach (var ov in dto.OutputValues)
                {
                    if (!Guid.TryParse(ov.NodeIdN, out var ovNode)) continue;
                    var key = new PortKey(ovNode, ov.PortId);
                    var st = (SignalType)ov.SignalType;
                    object val = st switch
                    {
                        SignalType.Bool => (object)ov.BoolValue,
                        SignalType.Int => (object)ov.IntValue,
                        SignalType.Float => (object)ov.FloatValue,
                        SignalType.String => (object)(ov.StringValue ?? ""),
                        _ => null
                    };
                    if (val != null)
                        lastOutputValues[key] = (st, val);
                }
            }

            saveDirty = false;
        }

        void OnGameWorldSave()
        {
            if (!saveDirty) return;

            var dto = new CircuitsSaveDto();

            foreach (var link in links)
            {
                dto.Links.Add(new LinkDto
                {
                    FromNodeIdN = link.From.NodeID.ToString("N"),
                    FromPortId = link.From.PortID,
                    ToNodeIdN = link.To.NodeID.ToString("N"),
                    ToPortId = link.To.PortID,
                    FromX = link.FromPos.X,
                    FromY = link.FromPos.Y,
                    FromZ = link.FromPos.Z,
                    FromDim = link.FromPos.dimension,
                    ToX = link.ToPos.X,
                    ToY = link.ToPos.Y,
                    ToZ = link.ToPos.Z,
                    ToDim = link.ToPos.dimension,
                    HasPositions = link.HasPositions
                });
            }

            foreach (var kv in lastOutputValues)
            {
                var ov = new OutputValueDto
                {
                    NodeIdN = kv.Key.NodeID.ToString("N"),
                    PortId = kv.Key.PortID,
                    SignalType = (int)kv.Value.type
                };
                switch (kv.Value.type)
                {
                    case SignalType.Bool when kv.Value.value is bool bv: ov.BoolValue = bv; break;
                    case SignalType.Int when kv.Value.value is int iv: ov.IntValue = iv; break;
                    case SignalType.Float when kv.Value.value is float fv: ov.FloatValue = fv; break;
                    case SignalType.String when kv.Value.value is string sv: ov.StringValue = sv; break;
                    default: continue;
                }
                dto.OutputValues.Add(ov);
            }

            byte[] bytes;
            using (var ms = new System.IO.MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(ms, dto);
                bytes = ms.ToArray();
            }

            sapi.WorldManager.SaveGame.StoreData(SaveKey, bytes);
            saveDirty = false;
        }

        private void OnRequestSnapshot(IServerPlayer fromPlayer, CircuitsRequestSnapshot msg)
        {
            if (fromPlayer == null) return;
            if (!fromPlayer.HasPrivilege(Privilege.controlserver)) return;

            SendGlobalSnapshot(fromPlayer);
        }

        private TextCommandResult OnListLinks(TextCommandCallingArgs args)
        {
            var list = links.ToList();
            if (list.Count == 0) return TextCommandResult.Success("No links.");

            var sb = new StringBuilder();
            sb.AppendLine($"Links ({list.Count}):");
            foreach (var l in list)
                sb.AppendLine($"{l.From.NodeID}:{l.From.PortID} -> {l.To.NodeID}:{l.To.PortID}");

            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult OnEmitBool(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Must be a player.");

            var sel = player.CurrentBlockSelection;
            if (sel?.Position == null) return TextCommandResult.Error("Look at a block.");

            var be = sapi.World.BlockAccessor.GetBlockEntity(sel.Position);
            if (be == null) return TextCommandResult.Error("No block entity here.");

            var node = be.Behaviors?.FirstOrDefault(b => b is CircuitBehavior) as CircuitBehavior;
            if (node == null) return TextCommandResult.Error("Not a circuit node.");

            string outPortId = (string)args[0];
            bool value = (bool)args[1];

            if (!TryEmit(node.NodeID, outPortId, SignalType.Bool, value, out string reason))
                return TextCommandResult.Error(reason);

            return TextCommandResult.Success("Emitted.");
        }

        private TextCommandResult OnSelectOut(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Must be a player.");

            var sel = player.CurrentBlockSelection;
            if (sel?.Position == null) return TextCommandResult.Error("Look at a block.");

            var be = sapi.World.BlockAccessor.GetBlockEntity(sel.Position);
            if (be == null) return TextCommandResult.Error("No block entity here.");

            var node = be.Behaviors?.FirstOrDefault(b => b is CircuitBehavior) as CircuitBehavior;
            if (node == null) return TextCommandResult.Error("Not a circuit node.");

            string outPortId = (string)args[0];

            if (!portsByNode.TryGetValue(node.NodeID, out var ports) || !ports.TryGetValue(outPortId, out var pd) || pd.Dir != PortDir.Out)
                return TextCommandResult.Error("That output port does not exist on this node.");

            selectedOutByPlayerUid[player.PlayerUID] = (node.NodeID, outPortId);
            return TextCommandResult.Success($"Selected OUT {node.NodeID}:{outPortId}");
        }

        private TextCommandResult OnLinkTo(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Must be a player.");

            if (!selectedOutByPlayerUid.TryGetValue(player.PlayerUID, out var selOut))
                return TextCommandResult.Error("No output selected. Use /circuits selectout <outPortId> first.");

            var sel = player.CurrentBlockSelection;
            if (sel?.Position == null) return TextCommandResult.Error("Look at a block.");

            var be = sapi.World.BlockAccessor.GetBlockEntity(sel.Position);
            if (be == null) return TextCommandResult.Error("No block entity here.");

            var node = be.Behaviors?.FirstOrDefault(b => b is CircuitBehavior) as CircuitBehavior;
            if (node == null) return TextCommandResult.Error("Not a circuit node.");

            string inPortId = (string)args[0];

            if (!TryCreateLink(selOut.nodeId, selOut.outPortId, node.NodeID, inPortId, out string reason))
                return TextCommandResult.Error(reason);

            return TextCommandResult.Success($"Linked {selOut.nodeId}:{selOut.outPortId} -> {node.NodeID}:{inPortId}");
        }

        private TextCommandResult OnListPorts(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Must be a player.");

            var sel = player.CurrentBlockSelection;
            if (sel?.Position == null) return TextCommandResult.Error("Look at a block.");

            var be = sapi.World.BlockAccessor.GetBlockEntity(sel.Position);
            if (be == null) return TextCommandResult.Error("No block entity here.");

            var node = be.Behaviors?.FirstOrDefault(b => b is CircuitBehavior) as CircuitBehavior;
            if (node == null) return TextCommandResult.Error("Not a circuit node.");

            var sb = new StringBuilder();
            sb.AppendLine($"Node {node.NodeID}");
            sb.AppendLine($"Pos {be.Pos}");

            if (TryGetPorts(node.NodeID, out var ports) && ports.Count > 0)
            {
                AppendPorts(sb, ports.Values);
                return TextCommandResult.Success(sb.ToString());
            }

            var collected = CollectPortsFromBE(be).ToList();
            if (collected.Count == 0) return TextCommandResult.Success(sb.AppendLine("No ports found.").ToString());

            AppendPorts(sb, collected);
            return TextCommandResult.Success(sb.ToString());
        }

        private void OnRequestLink(IServerPlayer fromPlayer, CircuitsRequestLink msg)
        {
            if (fromPlayer == null || msg == null) return;
            if (!fromPlayer.HasPrivilege(Privilege.controlserver)) return;

            if (!Guid.TryParse(msg.FromNodeIdN, out var fromNodeId)) return;
            if (!Guid.TryParse(msg.ToNodeIdN, out var toNodeId)) return;

            if (!nodesByID.ContainsKey(fromNodeId) && msg.HasPositions)
                TryRegisterNodeAtPosition(fromNodeId, new BlockPos(msg.FromX, msg.FromY, msg.FromZ, msg.FromDim));

            if (!nodesByID.ContainsKey(toNodeId) && msg.HasPositions)
                TryRegisterNodeAtPosition(toNodeId, new BlockPos(msg.ToX, msg.ToY, msg.ToZ, msg.ToDim));

            if (!nodesByID.ContainsKey(fromNodeId))
            {
                fromPlayer.SendMessage(GlobalConstants.GeneralChatGroup,
                    "Link failed: source node not registered/loaded.", EnumChatType.Notification);
                return;
            }
            if (!nodesByID.ContainsKey(toNodeId))
            {
                fromPlayer.SendMessage(GlobalConstants.GeneralChatGroup,
                    "Link failed: target node not registered/loaded.", EnumChatType.Notification);
                return;
            }

            EnsurePortsRegistered(fromNodeId);
            EnsurePortsRegistered(toNodeId);

            if (!TryCreateLink(fromNodeId, msg.FromPortId, toNodeId, msg.ToPortId, out var reason))
            {
                fromPlayer.SendMessage(GlobalConstants.GeneralChatGroup,
                    $"Link failed: {reason}", EnumChatType.Notification);
            }

            SendGlobalSnapshot(fromPlayer);
        }


        private bool TryRegisterNodeAtPosition(Guid nodeId, BlockPos pos)
        {
            if (sapi == null || pos == null) return false;

            var be = sapi.World.BlockAccessor.GetBlockEntity(pos);
            var node = be?.Behaviors?.FirstOrDefault(b => b is CircuitBehavior cb && cb.NodeID == nodeId) as CircuitBehavior;
            if (node == null) return false;

            RegisterOrUpdateNode(node.NodeID, be.Pos);
            RegisterPorts(node.NodeID, CollectPortsFromBE(be));
            return true;
        }

        private void EnsurePortsRegistered(Guid nodeId)
        {
            if (portsByNode.TryGetValue(nodeId, out var ports) && ports.Count > 0) return;
            if (!nodesByID.TryGetValue(nodeId, out var nodeRef)) return;

            var be = sapi.World.BlockAccessor.GetBlockEntity(nodeRef.Pos);
            if (be == null) return;

            RegisterPorts(nodeId, CollectPortsFromBE(be));
        }

        public void RemoveAllLinksForNode(Guid nodeId)
        {
            var toRemove = links.Where(l => l.From.NodeID == nodeId || l.To.NodeID == nodeId).ToList();
            if (toRemove.Count == 0) return;

            foreach (var l in toRemove)
            {
                links.Remove(l);
                BumpIncoming(l.To, -1);

                if (l.From.NodeID == nodeId)
                    DeliverDisconnectSignal(l);

                var delta = new CircuitsLinkDelta
                {
                    FromNodeIdN = l.From.NodeID.ToString("N"),
                    FromPortId = l.From.PortID,
                    FromX = l.FromPos.X,
                    FromY = l.FromPos.Y,
                    FromZ = l.FromPos.Z,
                    FromDim = l.FromPos.dimension,

                    ToNodeIdN = l.To.NodeID.ToString("N"),
                    ToPortId = l.To.PortID,
                    ToX = l.ToPos.X,
                    ToY = l.ToPos.Y,
                    ToZ = l.ToPos.Z,
                    ToDim = l.ToPos.dimension,

                    Added = false
                };

                foreach (var plr in sapi.World.AllOnlinePlayers)
                    sch.SendPacket(delta, (IServerPlayer)plr);
            }

            MarkDirty();
        }

        private void OnRequestClearPort(IServerPlayer fromPlayer, CircuitsRequestClearPort msg)
        {
            if (fromPlayer == null || msg == null) return;
            if (!fromPlayer.HasPrivilege(Privilege.controlserver)) return;

            if (!Guid.TryParse(msg.NodeIdN, out var nodeId)) return;
            if (!portsByNode.TryGetValue(nodeId, out var pmap) || !pmap.TryGetValue(msg.PortId, out var pd)) return;

            var toRemove = links.Where(l =>
                (pd.Dir == PortDir.In  && l.To.NodeID   == nodeId && l.To.PortID   == msg.PortId) ||
                (pd.Dir == PortDir.Out && l.From.NodeID == nodeId && l.From.PortID == msg.PortId)
            ).ToList();

            if (toRemove.Count == 0) return;

            foreach (var l in toRemove)
            {
                links.Remove(l);
                BumpIncoming(l.To, -1);
                DeliverDisconnectSignal(l);

                if (nodesByID.TryGetValue(l.From.NodeID, out var fr) && nodesByID.TryGetValue(l.To.NodeID, out var tr))
                {
                    var delta = new CircuitsLinkDelta
                    {
                        FromNodeIdN = l.From.NodeID.ToString("N"),
                        FromPortId = l.From.PortID,
                        FromX = fr.Pos.X,
                        FromY = fr.Pos.Y,
                        FromZ = fr.Pos.Z,
                        FromDim = fr.Pos.dimension,
                        ToNodeIdN = l.To.NodeID.ToString("N"),
                        ToPortId = l.To.PortID,
                        ToX = tr.Pos.X,
                        ToY = tr.Pos.Y,
                        ToZ = tr.Pos.Z,
                        ToDim = tr.Pos.dimension,
                        Added = false
                    };

                    foreach (var plr in sapi.World.AllOnlinePlayers)
                        sch.SendPacket(delta, (IServerPlayer)plr);
                }
            }

            MarkDirty();
        }

        private void AppendPorts(StringBuilder sb, IEnumerable<PortDef> ports)
        {
            var list = ports.OrderBy(p => p.Dir).ThenBy(p => p.PortID, StringComparer.Ordinal).ToList();
            sb.AppendLine($"Ports ({list.Count}):");
            foreach (var p in list)
            {
                string dir = p.Dir == PortDir.In ? "IN " : "OUT";
                sb.AppendLine($" - {dir} {p.Type,-5} {p.PortID}" +
                              $"  inMax={p.MaxInputs} outMax={p.MaxOutputs}" +
                              $"{(string.IsNullOrEmpty(p.DisplayName) ? "" : $"  \"{p.DisplayName}\"")}");
            }
        }

        private IEnumerable<PortDef> CollectPortsFromBE(BlockEntity be)
        {
            if (be is IBaseNode bn)
                return bn.GetPorts() ?? Enumerable.Empty<PortDef>();

            if (be.Behaviors == null)
                return Enumerable.Empty<PortDef>();

            var list = new List<PortDef>();
            foreach (var b in be.Behaviors)
            {
                if (b is INodePortsProvider pp)
                {
                    var ports = pp.GetPorts();
                    if (ports != null) list.AddRange(ports);
                }
            }
            return list;
        }

        public void RegisterOrUpdateNode(Guid nodeID, BlockPos pos, Vec3f renderOffset = null)
        {
            var nodeRef = new NodeRef { NodeID = nodeID, Pos = pos.Copy() };
            if (renderOffset != null) nodeRef.RenderOffset = renderOffset;

            nodesByID[nodeID] = nodeRef;

            foreach (var l in links)
            {
                bool updated = false;
                if (l.From.NodeID == nodeID)
                {
                    l.FromPos = pos.Copy();
                    updated = true;
                }
                if (l.To.NodeID == nodeID)
                {
                    l.ToPos = pos.Copy();
                    updated = true;
                }

                if (updated && nodesByID.ContainsKey(l.From.NodeID) && nodesByID.ContainsKey(l.To.NodeID))
                    l.HasPositions = true;
            }
        }

        public void UnregisterNode(Guid nodeID)
        {
            nodesByID.Remove(nodeID);
            if (portsByNode.TryGetValue(nodeID, out var ports))
            {
                foreach (var kv in ports)
                    lastOutputValues.Remove(new PortKey(nodeID, kv.Key));
            }
            portsByNode.Remove(nodeID);
        }

        public void RegisterPorts(Guid nodeID, IEnumerable<PortDef> ports)
        {
            portsByNode[nodeID] = ports.ToDictionary(p => p.PortID, p => p);

            if (sapi != null)
                ScheduleReDelivery(nodeID);
        }

        private readonly HashSet<Guid> pendingReDelivery = [];

        private void ScheduleReDelivery(Guid nodeID)
        {
            bool first = pendingReDelivery.Count == 0;
            pendingReDelivery.Add(nodeID);

            if (first)
            {
                sapi.World.RegisterCallback(_ =>
                {
                    var pending = new List<Guid>(pendingReDelivery);
                    pendingReDelivery.Clear();

                    foreach (var nid in pending)
                        ReDeliverStoredSignals(nid);
                }, 1);
            }
        }

        private void ReDeliverStoredSignals(Guid nodeID)
        {
            foreach (var link in links)
            {
                if (link.To.NodeID != nodeID) continue;
                if (!lastOutputValues.TryGetValue(link.From, out var stored)) continue;
                if (!nodesByID.TryGetValue(link.To.NodeID, out var toRef)) continue;
                if (!portsByNode.TryGetValue(link.To.NodeID, out var toPorts)) continue;
                if (!toPorts.TryGetValue(link.To.PortID, out var toPort)) continue;
                if (toPort.Dir != PortDir.In || toPort.Type != stored.type) continue;

                var be = sapi.World.BlockAccessor.GetBlockEntity(toRef.Pos);
                if (be?.Behaviors == null) continue;

                foreach (var b in be.Behaviors)
                {
                    if (b is ISignalReceiver rx)
                    {
                        rx.OnSignal(link.To.PortID, stored.type, stored.value, link.From);
                        break;
                    }
                }
            }
        }

        public bool TryCreateLink(Guid fromNodeId, string fromPortId, Guid toNodeId, string toPortId, out string reason)
        {
            if (sch == null) { reason = "Server channel not initialized."; return false; }

            reason = "";

            if (!nodesByID.TryGetValue(fromNodeId, out var fromRef) || !nodesByID.TryGetValue(toNodeId, out var toRef))
            {
                reason = "One or both nodes are not loaded/registered.";
                return false;
            }

            if (!portsByNode.TryGetValue(fromNodeId, out var fromPorts) || !fromPorts.TryGetValue(fromPortId, out var fromPort))
            {
                reason = "Source port not found.";
                return false;
            }
            if (!portsByNode.TryGetValue(toNodeId, out var toPorts) || !toPorts.TryGetValue(toPortId, out var toPort))
            {
                reason = "Target port not found.";
                return false;
            }

            if (fromNodeId == toNodeId)
            {
                reason = "Nodes cannot be linked to themselves.";
                return false;
            }

            // Allow flexible linking: Out->In, In->In, Out->Out
            // Type must match regardless of direction
            if (fromPort.Type != toPort.Type)
            {
                reason = $"Port types do not match ({fromPort.Type} -> {toPort.Type}).";
                return false;
            }

            // Out->Out linking: both must be outputs
            if (fromPort.Dir == PortDir.Out && toPort.Dir == PortDir.Out)
            {
                // This creates a "shared output" - both outputs will drive the same targets
                // We'll store it as-is and handle it specially in TryEmit
            }
            // In->In linking: both must be inputs (already handled by client-side chaining)
            else if (fromPort.Dir == PortDir.In && toPort.Dir == PortDir.In)
            {
                reason = "Input-to-input linking should be handled client-side via chaining.";
                return false;
            }
            // Standard Out->In linking
            else if (fromPort.Dir != PortDir.Out || toPort.Dir != PortDir.In)
            {
                reason = "Invalid port direction combination.";
                return false;
            }

            // Fan-in/fan-out checks (only for standard Out->In and Out->Out links)
            if (toPort.Dir == PortDir.In)
            {
                var toKey = new PortKey(toNodeId, toPortId);
                int incoming = GetIncomingCount(toKey);
                if (incoming >= Math.Max(1, toPort.MaxInputs))
                {
                    reason = "Target input already has a connection (fan-in not allowed for this port).";
                    return false;
                }
            }

            var fromKey = new PortKey(fromNodeId, fromPortId);
            int outgoing = GetOutgoingCount(fromKey);
            if (outgoing >= Math.Max(1, fromPort.MaxOutputs))
            {
                reason = "Source output has reached its outgoing link limit.";
                return false;
            }

            var link = new Link
            {
                From = new PortKey(fromNodeId, fromPortId),
                To = new PortKey(toNodeId, toPortId),
                FromPos = fromRef.Pos.Copy(),
                ToPos = toRef.Pos.Copy(),
                HasPositions = true
            };

            if (!links.Add(link)) { reason = "Link already exists."; return false; }

            // Only bump incoming count for In ports (not for Out->Out links)
            if (toPort.Dir == PortDir.In)
            {
                var toKey = new PortKey(toNodeId, toPortId);
                BumpIncoming(toKey, +1);
            }

            MarkDirty();

            var delta = new CircuitsLinkDelta
            {
                FromNodeIdN = fromNodeId.ToString("N"),
                FromPortId = fromPortId,
                FromX = fromRef.Pos.X,
                FromY = fromRef.Pos.Y,
                FromZ = fromRef.Pos.Z,
                FromDim = fromRef.Pos.dimension,

                ToNodeIdN = toNodeId.ToString("N"),
                ToPortId = toPortId,
                ToX = toRef.Pos.X,
                ToY = toRef.Pos.Y,
                ToZ = toRef.Pos.Z,
                ToDim = toRef.Pos.dimension,

                Added = true
            };

            foreach (var plr in sapi.World.AllOnlinePlayers)
            {
                var ent = plr.Entity;
                if (ent == null) continue;

                double r2 = 64 * 64;
                if (ent.Pos.SquareDistanceTo(fromRef.Pos.X + 0.5, fromRef.Pos.Y + 0.5, fromRef.Pos.Z + 0.5) > r2 &&
                    ent.Pos.SquareDistanceTo(toRef.Pos.X + 0.5, toRef.Pos.Y + 0.5, toRef.Pos.Z + 0.5) > r2)
                    continue;

                sch.SendPacket(delta, (IServerPlayer)plr);
            }

            // Propagate current output state to the newly connected input
            if (lastOutputValues.TryGetValue(new PortKey(fromNodeId, fromPortId), out var stored))
            {
                TryEmit(fromNodeId, fromPortId, stored.type, stored.value, out _);
            }

            return true;
        }

        public void SendGlobalSnapshot(IServerPlayer player)
        {
            if (player == null || sch == null) return;

            var snap = new CircuitsSnapshot { Links = new List<CircuitsLinkDelta>() };

            foreach (var l in links)
            {
                if (!l.HasPositions) continue;

                snap.Links.Add(new CircuitsLinkDelta
                {
                    FromNodeIdN = l.From.NodeID.ToString("N"),
                    FromPortId = l.From.PortID,
                    FromX = l.FromPos.X,
                    FromY = l.FromPos.Y,
                    FromZ = l.FromPos.Z,
                    FromDim = l.FromPos.dimension,
                    ToNodeIdN = l.To.NodeID.ToString("N"),
                    ToPortId = l.To.PortID,
                    ToX = l.ToPos.X,
                    ToY = l.ToPos.Y,
                    ToZ = l.ToPos.Z,
                    ToDim = l.ToPos.dimension,
                    Added = true
                });
            }

            sch.SendPacket(snap, player);
        }

        private void OnEditLocatorPacket(IServerPlayer player, EditLocatorPacket packet)
        {
            var slot = player.InventoryManager.ActiveHotbarSlot;
            if (slot?.Itemstack?.Item is not ItemCustomLocatorMap) return;

            var attr = slot.Itemstack.Attributes;
            if (packet.WaypointText != null) attr.SetString("WaypointText", packet.WaypointText);
            if (packet.WaypointIcon != null) attr.SetString("WaypointIcon", packet.WaypointIcon);
            if (packet.WaypointColorSwatch.HasValue) attr.SetInt("WaypointColorSwatch", packet.WaypointColorSwatch.Value);

            if (!string.IsNullOrEmpty(packet.VariantType))
            {
                var newCode = slot.Itemstack.Item.Code.CopyWithPath(
                    slot.Itemstack.Item.Code.Path.Replace(
                        slot.Itemstack.Item.Variant["type"], packet.VariantType));
                var newItem = sapi.World.GetItem(newCode);
                if (newItem != null)
                    slot.Itemstack = new ItemStack(newItem, 1) { Attributes = attr };
            }

            slot.MarkDirty();
        }

        public bool TryRemoveLink(PortKey from, PortKey to, out string reason)
        {
            reason = "";
            var dummy = new BlockPos(0, 0, 0);
            var link = new Link { From = from, To = to, FromPos = dummy, ToPos = dummy };

            if (!links.Remove(link))
            {
                reason = "Link not found.";
                return false;
            }

            BumpIncoming(to, -1);
            DeliverDisconnectSignal(link);
            MarkDirty();
            return true;
        }

        public IEnumerable<Link> GetLinks() => links;

        private int GetIncomingCount(PortKey toKey)
            => incomingCount.TryGetValue(toKey, out var c) ? c : 0;

        private int GetOutgoingCount(PortKey fromKey)
        {
            int count = 0;
            foreach (var l in links) if (l.From.Equals(fromKey)) count++;
            return count;
        }

        private void RebuildIncomingCounts()
        {
            incomingCount.Clear();
            foreach (var l in links)
            {
                incomingCount.TryGetValue(l.To, out var cur);
                incomingCount[l.To] = cur + 1;
            }
        }

        private void BumpIncoming(PortKey toKey, int delta)
        {
            incomingCount.TryGetValue(toKey, out int cur);
            int next = cur + delta;
            if (next <= 0) incomingCount.Remove(toKey);
            else incomingCount[toKey] = next;
        }

        public bool TryEmit(Guid fromNodeId, string fromPortId, SignalType type, object value, out string reason)
        {
            reason = "";

            if (!nodesByID.TryGetValue(fromNodeId, out _))
            {
                reason = "Emitter node not registered.";
                return false;
            }

            if (!portsByNode.TryGetValue(fromNodeId, out var fromPorts) || !fromPorts.TryGetValue(fromPortId, out var fromPort))
            {
                reason = "Emitter port not found.";
                return false;
            }
            if (fromPort.Dir != PortDir.Out)
            {
                reason = "Emitter port is not an output.";
                return false;
            }
            if (fromPort.Type != type)
            {
                reason = $"Emitter port type is {fromPort.Type}, not {type}.";
                return false;
            }

            var fromKey = new PortKey(fromNodeId, fromPortId);
            lastOutputValues[fromKey] = (type, value);
            MarkDirty();

            int delivered = 0;
            foreach (var link in links)
            {
                if (!link.From.Equals(fromKey)) continue;

                if (!nodesByID.TryGetValue(link.To.NodeID, out var toRef)) continue;

                if (!portsByNode.TryGetValue(link.To.NodeID, out var toPorts) ||
                    !toPorts.TryGetValue(link.To.PortID, out var toPort) ||
                    toPort.Type != type)
                {
                    continue;
                }

                // Out->Out link: forward the signal to the target output's links
                if (toPort.Dir == PortDir.Out)
                {
                    // Recursively emit from the target output
                    TryEmit(link.To.NodeID, link.To.PortID, type, value, out _);
                    delivered++;
                    continue;
                }

                // Standard Out->In link: deliver to receivers
                if (toPort.Dir != PortDir.In) continue;

                var be = sapi.World.BlockAccessor.GetBlockEntity(toRef.Pos);
                if (be?.Behaviors == null) continue;

                bool handled = false;
                foreach (var b in be.Behaviors)
                {
                    if (b is ISignalReceiver rx)
                    {
                        if (rx.OnSignal(link.To.PortID, type, value, fromKey))
                        {
                            handled = true;
                            break;
                        }
                    }
                }

                if (handled) delivered++;
            }

            reason = delivered > 0 ? "" : "No receivers handled the signal.";
            return delivered > 0;
        }

        private static object GetDefaultValue(SignalType type) => type switch
        {
            SignalType.Bool => (object)false,
            SignalType.Int => (object)0,
            SignalType.Float => (object)0f,
            SignalType.String => (object)"",
            _ => null
        };

        private void DeliverDisconnectSignal(Link removedLink)
        {
            if (sapi == null) return;
            if (!nodesByID.TryGetValue(removedLink.To.NodeID, out var toRef)) return;

            var be = sapi.World.BlockAccessor.GetBlockEntity(toRef.Pos);
            if (be?.Behaviors == null) return;

            foreach (var b in be.Behaviors)
            {
                if (b is ISignalReceiver rx)
                {
                    rx.OnSourceDisconnected(removedLink.To.PortID, removedLink.From);
                    break;
                }
            }
        }

        public bool TryGetPorts(Guid nodeId, out Dictionary<string, PortDef> ports)
            => portsByNode.TryGetValue(nodeId, out ports);

        public bool TryPickFirstPort(Guid nodeId, PortDir dir, out string portId)
        {
            portId = null;
            if (!portsByNode.TryGetValue(nodeId, out var ports)) return false;

            foreach (var kv in ports)
            {
                if (kv.Value.Dir == dir)
                {
                    portId = kv.Key;
                    return true;
                }
            }
            return false;
        }

        public bool TryPickFirstPort(Guid nodeId, PortDir dir, SignalType type, out string portId)
        {
            portId = null;
            if (!portsByNode.TryGetValue(nodeId, out var ports)) return false;

            foreach (var kv in ports)
            {
                var p = kv.Value;
                if (p.Dir == dir && p.Type == type)
                {
                    portId = kv.Key;
                    return true;
                }
            }
            return false;
        }

        public bool TryPickFirstFreeInPort(Guid nodeId, out string portId)
            => TryPickFirstFreeInPort(nodeId, (SignalType?)null, out portId);

        public bool TryPickFirstFreeInPort(Guid nodeId, SignalType type, out string portId)
            => TryPickFirstFreeInPort(nodeId, (SignalType?)type, out portId);

        private bool TryPickFirstFreeInPort(Guid nodeId, SignalType? type, out string portId)
        {
            portId = null;
            if (!portsByNode.TryGetValue(nodeId, out var ports)) return false;

            foreach (var kv in ports.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var p = kv.Value;
                if (p.Dir != PortDir.In) continue;
                if (type != null && p.Type != type.Value) continue;

                var toKey = new PortKey(nodeId, p.PortID);
                int incoming = GetIncomingCount(toKey);
                int max = Math.Max(1, p.MaxInputs);

                if (incoming < max)
                {
                    portId = p.PortID;
                    return true;
                }
            }

            return false;
        }

        private readonly Dictionary<string, (Guid nodeId, string outPortId)> selectedOutByPlayerUid = [];
    }
}
