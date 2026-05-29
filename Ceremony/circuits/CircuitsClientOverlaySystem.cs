using Ceremony.circuits;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
#nullable disable

namespace circuits
{
    public class CircuitsClientOverlaySystem : ModSystem
    {
        ICoreClientAPI capi;
        CircuitNodeOverlayRenderer renderer;
        List<CircuitsLinkDelta> links = new();

        bool snapshotRequested;

        float scanAcc;
        const float ScanInterval = 0.10f;

        const float PortVerticalSpacingPx = 58f;
        const float WireShowDist = 50f;
        const int MaxRenderedLinks = 256;
        const int MaxEndpointNodes = 128;

        const float ConeMaxDist = 12f;

        const float FocusRadiusPx = 90f;
        const float FocusHysteresisPx = 30f;
        const float FocusMaxDist = 8f;

        const float HitRayMaxDist = 8f;

        BlockPos focused;
        PortMarker lastPickedPort;

        const float DimAlpha = 0.12f;
        const float LitAlpha = 1.00f;

        DragState drag;
            bool DebugWand = false;
            float pruneAcc;
        const float PruneInterval = 0.50f;

        readonly Dictionary<string, DisplayFrom> displayFrom = new(256);

        // Cache for render offsets to avoid repeated BE queries
        readonly Dictionary<string, Vec3f> offsetCache = new(256);
        readonly Dictionary<Guid, BlockPos> recentNodePositions = new(256);

        readonly struct DisplayFrom
        {
            public readonly BlockPos Pos;
            public readonly string PortId;
            public readonly PortDir Dir;

            public DisplayFrom(BlockPos pos, string portId, PortDir dir)
            {
                Pos = pos;
                PortId = portId;
                Dir = dir;
            }
        }

        static string ToKey(string toNodeIdN, string toPortId) => $"{toNodeIdN}|{toPortId}";

        RouteState routeState;

        // Helper method to safely get render offset from a block position
        Vec3f GetRenderOffset(BlockPos pos)
        {
            if (pos == null) return null;

            string key = Key(pos);
            if (offsetCache.TryGetValue(key, out var cached))
                return cached;

            var be = capi.World.BlockAccessor.GetBlockEntity(pos);
            if (be?.Behaviors == null)
            {
                offsetCache[key] = null;
                return null;
            }

            foreach (var beh in be.Behaviors)
            {
                if (beh is CircuitBehavior node)
                {
                    var offset = node.RenderOffset ?? new Vec3f(0, 0, 0);
                    offsetCache[key] = offset;
                    return offset;
                }
            }

            offsetCache[key] = null;
            return null;
        }

        // Clear offset cache periodically to handle block changes
        void ClearOffsetCache()
        {
            offsetCache.Clear();
        }

        sealed class RouteState
        {
            public double BaseY;
            public double XTrunk;
            public double ZTrunk;

            public readonly Dictionary<long, int> Occ = new Dictionary<long, int>(4096);

            public int RouteVersion;
        }

        static long Pack(int ix, int iz, int iyLayer)
            => ((long)ix << 42) ^ ((long)(uint)iz << 10) ^ (uint)iyLayer;

        sealed class LinkKey : IEquatable<LinkKey>
        {
            public string FromNodeN, FromPort, ToNodeN, ToPort;

            public bool Equals(LinkKey other)
                => other != null
                && FromNodeN == other.FromNodeN
                && FromPort == other.FromPort
                && ToNodeN == other.ToNodeN
                && ToPort == other.ToPort;

            public override bool Equals(object obj) => obj is LinkKey lk && Equals(lk);
            public override int GetHashCode() => HashCode.Combine(FromNodeN, FromPort, ToNodeN, ToPort);
        }

        static LinkKey MakeKey(CircuitsLinkDelta l) => new LinkKey
        {
            FromNodeN = l.FromNodeIdN,
            FromPort = l.FromPortId,
            ToNodeN = l.ToNodeIdN,
            ToPort = l.ToPortId
        };

        bool TryGetNodeIdAtPos(BlockPos pos, out string nodeIdN)
        {
            nodeIdN = null;
            if (pos == null) return false;

            var be = capi.World.BlockAccessor.GetBlockEntity(pos);
            var node = be?.Behaviors?.FirstOrDefault(b => b is CircuitBehavior) as CircuitBehavior;
            if (node == null) return false;

            nodeIdN = node.NodeID.ToString("N");
            return true;
        }

        HashSet<LinkKey> TraceFromPort(List<CircuitsLinkDelta> linkList, string startNodeN, string startPortId, PortDir dir)
        {
            var result = new HashSet<LinkKey>();
            if (linkList == null || linkList.Count == 0) return result;

            var outByNode = new Dictionary<string, List<CircuitsLinkDelta>>(256);
            var inByNode = new Dictionary<string, List<CircuitsLinkDelta>>(256);

            for (int i = 0; i < linkList.Count; i++)
            {
                var l = linkList[i];
                if (l == null) continue;

                if (!outByNode.TryGetValue(l.FromNodeIdN, out var ol))
                    outByNode[l.FromNodeIdN] = ol = new List<CircuitsLinkDelta>();
                ol.Add(l);

                if (!inByNode.TryGetValue(l.ToNodeIdN, out var il))
                    inByNode[l.ToNodeIdN] = il = new List<CircuitsLinkDelta>();
                il.Add(l);
            }

            var q = new Queue<string>();
            var visitedNodes = new HashSet<string>();

            if (dir == PortDir.Out)
            {
                for (int i = 0; i < linkList.Count; i++)
                {
                    var l = linkList[i];
                    if (l.FromNodeIdN != startNodeN) continue;
                    if (l.FromPortId != startPortId) continue;

                    result.Add(MakeKey(l));
                    q.Enqueue(l.ToNodeIdN);
                }
            }
            else
            {
                for (int i = 0; i < linkList.Count; i++)
                {
                    var l = linkList[i];
                    if (l.ToNodeIdN != startNodeN) continue;
                    if (l.ToPortId != startPortId) continue;

                    result.Add(MakeKey(l));
                    q.Enqueue(l.FromNodeIdN);
                }
            }

            while (q.Count > 0)
            {
                var nodeN = q.Dequeue();
                if (nodeN == null) continue;
                if (!visitedNodes.Add(nodeN)) continue;

                if (dir == PortDir.Out)
                {
                    if (!outByNode.TryGetValue(nodeN, out var outs)) continue;

                    for (int i = 0; i < outs.Count; i++)
                    {
                        var l = outs[i];
                        if (result.Add(MakeKey(l)))
                            q.Enqueue(l.ToNodeIdN);
                    }
                }
                else
                {
                    if (!inByNode.TryGetValue(nodeN, out var ins)) continue;

                    for (int i = 0; i < ins.Count; i++)
                    {
                        var l = ins[i];
                        if (result.Add(MakeKey(l)))
                            q.Enqueue(l.FromNodeIdN);
                    }
                }
            }

            return result;
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            renderer = new CircuitNodeOverlayRenderer(capi);
            capi.Event.RegisterRenderer(renderer, EnumRenderStage.Ortho);

            var ms = api.ModLoader.GetModSystem<CircuitsModSystem>();
            ms.ClientChannel
              .SetMessageHandler<CircuitsLinkDelta>(OnLinkDelta)
              .SetMessageHandler<CircuitsSnapshot>(OnSnapshot);

            capi.Event.MouseDown += OnMouseDown;
            capi.Event.MouseUp += OnMouseUp;

            capi.Event.RegisterGameTickListener(OnClientTick, 2);
            capi.Event.LevelFinalize += () =>
            {
                var cms = capi.ModLoader.GetModSystem<CircuitsModSystem>();
                cms?.ClientChannel?.SendPacket(new CircuitsRequestSnapshot());
                snapshotRequested = true;
            };
        }

        private void OnMouseDown(MouseEvent args)
        {
            if (args == null) return;
            if (args.Button != EnumMouseButton.Right) return;

            var plr = capi.World.Player;
            if (plr == null || !IsWandHeld(plr)) return;

            bool shift =
                capi.Input.KeyboardKeyState[(int)GlKeys.ShiftLeft] ||
                capi.Input.KeyboardKeyState[(int)GlKeys.ShiftRight];

            if (shift) { args.Handled = true; return; }

            if (lastPickedPort != null)
            {
                var pm = lastPickedPort;
                var be = capi.World.BlockAccessor.GetBlockEntity(pm.BlockPos);
                if (be == null) return;

                drag = new DragState { FromPos = pm.BlockPos, FromPortId = pm.PortId, FromDir = pm.Dir, Type = pm.Type, IsBidirectional = pm.IsBidirectional, AltPortId = pm.AltPortId };
                args.Handled = true;
            }
        }

        private void OnMouseUp(MouseEvent args)
        {
            if (args == null) return;

            var plr = capi.World.Player;
            if (plr == null || !IsWandHeld(plr)) return;

            bool shift =
                    capi.Input.KeyboardKeyState[(int)GlKeys.ShiftLeft] ||
                    capi.Input.KeyboardKeyState[(int)GlKeys.ShiftRight];

            if (args.Button == EnumMouseButton.Right && shift)
            {
                if (renderer.TryPickPortAtScreen(args.X, args.Y, out var pm))
                {
                    var be = capi.World.BlockAccessor.GetBlockEntity(pm.BlockPos);
                    var node = be?.Behaviors?.FirstOrDefault(b => b is CircuitBehavior) as CircuitBehavior;
                    if (node != null)
                    {
                        var ms = capi.ModLoader.GetModSystem<CircuitsModSystem>();
                        ms.ClientChannel.SendPacket(new CircuitsRequestClearPort
                        {
                            NodeIdN = node.NodeID.ToString("N"),
                            PortId = pm.PortId,
                            Mode = 0
                        });
                        if (pm.IsBidirectional && pm.AltPortId != null)
                        {
                            ms.ClientChannel.SendPacket(new CircuitsRequestClearPort
                            {
                                NodeIdN = node.NodeID.ToString("N"),
                                PortId = pm.AltPortId,
                                Mode = 0
                            });
                        }
                        args.Handled = true;
                    }
                }
                return;
            }

            if (args.Button != EnumMouseButton.Right) return;
            if (drag == null) return;

            if (renderer.TryPickPortAtScreen(args.X, args.Y, out var tgtPort))
            {
                TrySendLinkToPort(tgtPort);
                drag = null;
                args.Handled = true;
                return;
            }

            if (focused != null)
            {
                var be = capi.World.BlockAccessor.GetBlockEntity(focused);
                var tgtNode = be?.Behaviors?.FirstOrDefault(b => b is CircuitBehavior) as CircuitBehavior;
                if (tgtNode != null)
                {
                    if (drag.IsBidirectional)
                    {
                        var srcBe = capi.World.BlockAccessor.GetBlockEntity(drag.FromPos);
                        var srcNode = srcBe?.Behaviors?.FirstOrDefault(b => b is CircuitBehavior) as CircuitBehavior;
                        if (srcNode != null)
                        {
                            if (TryPickCompatiblePortOnBE(be, PortDir.Out, drag.Type, out string tgtInPort))
                                SendLinkNormalized(srcNode.NodeID, drag.AltPortId, tgtNode.NodeID, tgtInPort);
                            else if (TryPickCompatiblePortOnBE(be, PortDir.In, drag.Type, out string tgtOutPort))
                                SendLinkNormalized(tgtNode.NodeID, tgtOutPort, srcNode.NodeID, drag.FromPortId);
                        }
                    }
                    else if (TryPickCompatiblePortOnBE(be, drag.FromDir, drag.Type, out string tgtPortId))
                    {
                        var srcBe = capi.World.BlockAccessor.GetBlockEntity(drag.FromPos);
                        var srcNode = srcBe?.Behaviors?.FirstOrDefault(b => b is CircuitBehavior) as CircuitBehavior;
                        if (srcNode != null)
                        {
                            if (TryGetPortDirOnBE(be, tgtPortId, out var tgtDir))
                            {
                                if (drag.FromDir != tgtDir)
                                {
                                    if (drag.FromDir == PortDir.Out && tgtDir == PortDir.In)
                                        SendLinkNormalized(srcNode.NodeID, drag.FromPortId, tgtNode.NodeID, tgtPortId);
                                    else if (drag.FromDir == PortDir.In && tgtDir == PortDir.Out)
                                        SendLinkNormalized(tgtNode.NodeID, tgtPortId, srcNode.NodeID, drag.FromPortId);
                                }
                            }
                        }
                    }
                }
            }

            drag = null;
            args.Handled = true;
        }

        private bool TryPickCompatiblePortOnBE(BlockEntity be, PortDir fromDir, SignalType type, out string portId)
        {
            portId = null;
            if (be?.Behaviors == null) return false;

            PortDir wantDir = (fromDir == PortDir.Out) ? PortDir.In : PortDir.Out;

            for (int i = 0; i < be.Behaviors.Count; i++)
            {
                if (be.Behaviors[i] is INodePortsProvider pp)
                {
                    foreach (var p in pp.GetPorts())
                    {
                        if (p == null) continue;
                        if (p.Dir != wantDir) continue;
                        if (p.Type != type) continue;
                        portId = p.PortID;
                        return true;
                    }
                }
            }
            return false;
        }

        private bool TryGetPortDirOnBE(BlockEntity be, string portId, out PortDir dir)
        {
            dir = default;
            if (be?.Behaviors == null) return false;

            foreach (var b in be.Behaviors)
            {
                if (b is INodePortsProvider pp)
                {
                    foreach (var p in pp.GetPorts())
                    {
                        if (p?.PortID == portId)
                        {
                            dir = p.Dir;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private void TrySendLinkToPort(PortMarker tgt)
        {
            if (tgt == null || drag == null) return;
            if (tgt.Type != drag.Type) return;

            if (!drag.IsBidirectional && !tgt.IsBidirectional)
            {
                if (tgt.Dir == drag.FromDir && tgt.Dir == PortDir.Out) return;
            }

            var tgtBe = capi.World.BlockAccessor.GetBlockEntity(tgt.BlockPos);
            var tgtNode = tgtBe?.Behaviors?.FirstOrDefault(b => b is CircuitBehavior) as CircuitBehavior;
            if (tgtNode == null) return;

            var srcBe = capi.World.BlockAccessor.GetBlockEntity(drag.FromPos);
            var srcNode = srcBe?.Behaviors?.FirstOrDefault(b => b is CircuitBehavior) as CircuitBehavior;
            if (srcNode == null) return;

            if (drag.IsBidirectional || tgt.IsBidirectional)
            {
                Guid outNode, inNode;
                string outPort, inPort;

                if (drag.IsBidirectional && (tgt.Dir == PortDir.In || tgt.IsBidirectional))
                {
                    outNode = srcNode.NodeID; outPort = drag.AltPortId;
                    inNode = tgtNode.NodeID; inPort = tgt.PortId;
                }
                else if (drag.IsBidirectional && tgt.Dir == PortDir.Out)
                {
                    outNode = tgtNode.NodeID; outPort = tgt.PortId;
                    inNode = srcNode.NodeID; inPort = drag.FromPortId;
                }
                else if (tgt.IsBidirectional && drag.FromDir == PortDir.Out)
                {
                    outNode = srcNode.NodeID; outPort = drag.FromPortId;
                    inNode = tgtNode.NodeID; inPort = tgt.PortId;
                }
                else
                {
                    outNode = tgtNode.NodeID; outPort = tgt.AltPortId;
                    inNode = srcNode.NodeID; inPort = drag.FromPortId;
                }

                SendLinkNormalized(outNode, outPort, inNode, inPort);
                return;
            }

            if (drag.FromDir == PortDir.In && tgt.Dir == PortDir.In)
            {
                if (links == null || links.Count == 0) return;

                if (!TryFindDriverForInput(links, srcNode.NodeID.ToString("N"), drag.FromPortId, out var driverNodeN, out var driverPortId))
                    return;

                if (!Guid.TryParse(driverNodeN, out var driverNodeId))
                    return;

                SendLinkNormalized(driverNodeId, driverPortId, tgtNode.NodeID, tgt.PortId);

                displayFrom[ToKey(tgtNode.NodeID.ToString("N"), tgt.PortId)] =
                    new DisplayFrom(drag.FromPos, drag.FromPortId, PortDir.In);

                return;
            }

            Guid fromNodeId;
            string fromPortId;
            Guid toNodeId;
            string toPortId;

            if (drag.FromDir == PortDir.Out && tgt.Dir == PortDir.In)
            {
                fromNodeId = srcNode.NodeID;
                fromPortId = drag.FromPortId;
                toNodeId = tgtNode.NodeID;
                toPortId = tgt.PortId;
            }
            else if (drag.FromDir == PortDir.In && tgt.Dir == PortDir.Out)
            {
                fromNodeId = tgtNode.NodeID;
                fromPortId = tgt.PortId;
                toNodeId = srcNode.NodeID;
                toPortId = drag.FromPortId;
            }
            else
            {
                return;
            }

            SendLinkNormalized(fromNodeId, fromPortId, toNodeId, toPortId);
        }

        bool TryFindDriverForInput(List<CircuitsLinkDelta> linkList, string toNodeIdN, string toPortId, out string fromNodeIdN, out string fromPortId)
        {
            fromNodeIdN = null;
            fromPortId = null;
            if (linkList == null) return false;

            for (int i = 0; i < linkList.Count; i++)
            {
                var l = linkList[i];
                if (l == null) continue;
                if (l.ToNodeIdN == toNodeIdN && l.ToPortId == toPortId)
                {
                    fromNodeIdN = l.FromNodeIdN;
                    fromPortId = l.FromPortId;
                    return true;
                }
            }
            return false;
        }

        private void SendLinkNormalized(Guid fromNodeId, string fromPortId, Guid toNodeId, string toPortId)
        {
            var ms = capi.ModLoader.GetModSystem<CircuitsModSystem>();
            if (ms?.ClientChannel == null) return;

            Debug($"SendLink from={fromNodeId}:{fromPortId} -> to={toNodeId}:{toPortId}");
            recentNodePositions.TryGetValue(fromNodeId, out var fromPos);
            recentNodePositions.TryGetValue(toNodeId, out var toPos);
            bool hasPositions = fromPos != null && toPos != null;

            ms.ClientChannel.SendPacket(new CircuitsRequestLink
            {
                FromNodeIdN = fromNodeId.ToString("N"),
                FromPortId = fromPortId,
                ToNodeIdN = toNodeId.ToString("N"),
                ToPortId = toPortId,
                FromX = fromPos?.X ?? 0,
                FromY = fromPos?.Y ?? 0,
                FromZ = fromPos?.Z ?? 0,
                FromDim = fromPos?.dimension ?? 0,
                ToX = toPos?.X ?? 0,
                ToY = toPos?.Y ?? 0,
                ToZ = toPos?.Z ?? 0,
                ToDim = toPos?.dimension ?? 0,
                HasPositions = hasPositions
            });
        }

        static void WorldToCamera(double[] viewMat, double wx, double wy, double wz, out double cx, out double cy, out double cz)
        {
            cx = viewMat[0] * wx + viewMat[4] * wy + viewMat[8] * wz + viewMat[12];
            cy = viewMat[1] * wx + viewMat[5] * wy + viewMat[9] * wz + viewMat[13];
            cz = viewMat[2] * wx + viewMat[6] * wy + viewMat[10] * wz + viewMat[14];
        }

        void OnClientTick(float dt)
        {
            var plr = capi.World.Player;
            var plrEnt = plr?.Entity;
            if (plrEnt == null) return;

            if (!IsWandHeld(plr))
            {
                focused = null;
                renderer.SetMarkers(null, null, null, null);
                ClearOffsetCache();
                snapshotRequested = false; // reset so next pickup re-syncs
                return;
            }

            if (!snapshotRequested)
            {
                var ms = capi.ModLoader.GetModSystem<CircuitsModSystem>();
                ms?.ClientChannel?.SendPacket(new CircuitsRequestSnapshot());
                snapshotRequested = true;
            }

            scanAcc += dt;
            if (scanAcc < ScanInterval) return;
            scanAcc = 0;

            // Clear offset cache periodically to handle block state changes
            ClearOffsetCache();

            RaycastUtils.GetRayDirFromScreen(capi, capi.Input.MouseX, capi.Input.MouseY, out Vec3d look);
            Vec3d camPos = plrEnt.CameraPos;

            RaycastUtils.TryRaycastFromScreen(capi, capi.Input.MouseX, capi.Input.MouseY, HitRayMaxDist, out var bsel, out var esel);

            double showR2 = ConeMaxDist * ConeMaxDist;
            int scanR = (int)Math.Ceiling(ConeMaxDist);

            BlockPos min = new BlockPos((int)camPos.X - scanR, (int)camPos.Y - scanR, (int)camPos.Z - scanR, plrEnt.Pos.Dimension);
            BlockPos max = new BlockPos((int)camPos.X + scanR, (int)camPos.Y + scanR, (int)camPos.Z + scanR, plrEnt.Pos.Dimension);

            var nodes = new List<NodeMarker>(128);
            var ports = new List<PortMarker>(128);
            var wires = new List<WireMarker>(128);

            float cxScreen = capi.Input.MouseX;
            float cyScreen = capi.Input.MouseY;

            float focusR2 = FocusRadiusPx * FocusRadiusPx;
            float keepR2 = (FocusRadiusPx + FocusHysteresisPx) * (FocusRadiusPx + FocusHysteresisPx);

            BlockPos best = null;
            double bestWorldDist2 = double.MaxValue;

            double[] viewMat = capi.Render.PerspectiveViewMat;

            capi.World.BlockAccessor.WalkBlocks(min, max, (block, x, y, z) =>
            {
                double wx = x + 0.5;
                double wy = y + 0.5;
                double wz = z + 0.5;

                double dx = wx - camPos.X;
                double dy = wy - camPos.Y;
                double dz = wz - camPos.Z;
                double dist2 = dx * dx + dy * dy + dz * dz;
                bool inFocusDist = dist2 <= (FocusMaxDist * FocusMaxDist);
                if (dist2 > showR2) return;

                double len = Math.Sqrt(dist2);
                if (len < 1e-6) return;

                // Resolve block entity and render offset BEFORE any geometric checks so that
                // all visibility tests use the actual visual position of the node, not the raw
                // block center (which can differ significantly when a render offset is set).
                var pos = new BlockPos(x, y, z, plrEnt.Pos.Dimension);

                var be = capi.World.BlockAccessor.GetBlockEntity(pos);
                if (be?.Behaviors == null) return;

                CircuitBehavior nodeBeh = null;
                for (int i = 0; i < be.Behaviors.Count; i++)
                {
                    if (be.Behaviors[i] is CircuitBehavior n) { nodeBeh = n; break; }
                }
                if (nodeBeh == null) return;

                Vec3f offset = GetRenderOffset(pos);
                double ox = offset?.X ?? 0;
                double oy = offset?.Y ?? 0;
                double oz = offset?.Z ?? 0;

                // All geometric checks use the offset-adjusted visual position.
                double wxOffset = wx + ox;
                double wyOffset = wy + oy;
                double wzOffset = wz + oz;

                double dxo = wxOffset - camPos.X;
                double dyo = wyOffset - camPos.Y;
                double dzo = wzOffset - camPos.Z;
                double leno = Math.Sqrt(dxo * dxo + dyo * dyo + dzo * dzo);
                if (leno < 1e-6) return;

                double dot = (dxo / leno) * look.X + (dyo / leno) * look.Y + (dzo / leno) * look.Z;
                if (dot <= 0) return;

                WorldToCamera(viewMat, wxOffset, wyOffset, wzOffset, out double cx, out double cy, out double cz);
                if (cz >= 0) return;

                Vec3d scrOffset = MatrixToolsd.Project(
                    new Vec3d(wxOffset, wyOffset, wzOffset),
                    capi.Render.PerspectiveProjectionMat,
                    viewMat,
                    capi.Render.FrameWidth,
                    capi.Render.FrameHeight
                );
                if (scrOffset.Z < 0) return;

                const double MarginPx = 120;
                if (scrOffset.X < -MarginPx || scrOffset.X > capi.Render.FrameWidth + MarginPx) return;
                if (scrOffset.Y < -MarginPx || scrOffset.Y > capi.Render.FrameHeight + MarginPx) return;

                recentNodePositions[nodeBeh.NodeID] = pos.Copy();

                nodes.Add(new NodeMarker
                {
                    BlockPos = pos,
                    NodeId = nodeBeh.NodeID,
                    ConeAlpha = 1f,
                    Dist = (float)Math.Sqrt(dist2),
                    RenderOffset = offset
                });

                float sx = (float)scrOffset.X;
                float sy = capi.Render.FrameHeight - (float)scrOffset.Y;

                float rdx = sx - cxScreen;
                float rdy = sy - cyScreen;
                float d2 = rdx * rdx + rdy * rdy;

                bool isCurrent = (focused != null && pos.Equals(focused));
                float limit2 = isCurrent ? keepR2 : focusR2;

                if (!inFocusDist) return;
                if (d2 > limit2) return;

                if (dist2 < bestWorldDist2)
                {
                    bestWorldDist2 = dist2;
                    best = pos;
                }
            });

            focused = best;
            if (focused != null)
            {
                var be = capi.World.BlockAccessor.GetBlockEntity(focused);
                if (be?.Behaviors != null)
                {
                    List<PortDef> outs = null;
                    List<PortDef> ins = null;

                    CircuitBehavior nodeBeh = null;

                    for (int i = 0; i < be.Behaviors.Count; i++)
                    {
                        if (be.Behaviors[i] is CircuitBehavior n)
                        {
                            nodeBeh = n;
                        }

                        if (be.Behaviors[i] is INodePortsProvider pp)
                        {
                            foreach (var p in pp.GetPorts())
                            {
                                if (p == null) continue;
                                if (p.Dir == PortDir.Out) { outs ??= new List<PortDef>(); outs.Add(p); }
                                else { ins ??= new List<PortDef>(); ins.Add(p); }
                            }
                        }
                    }

                    // Always use helper method for render offset
                    Vec3f renderOffset = GetRenderOffset(focused);

                    float fdist = (float)plrEnt.CameraPos.DistanceTo(new Vec3d(focused.X + 0.5, focused.Y + 0.5, focused.Z + 0.5));
                    float fcone = 1f;
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        if (nodes[i].BlockPos.Equals(focused)) { fcone = nodes[i].ConeAlpha; break; }
                    }

                    AddStackedPorts(ports, focused, ins, outs, 0f, PortVerticalSpacingPx, fcone, fdist, renderOffset);
                }
            }

            pruneAcc += dt;
            if (pruneAcc >= PruneInterval)
            {
                pruneAcc = 0;
                PruneDeadLinks();
            }

            if (links.Count > 0)
            {
                double show2 = WireShowDist * WireShowDist;

                HashSet<LinkKey> tracedLinks = null;
                if (lastPickedPort != null)
                {
                    if (TryGetNodeIdAtPos(lastPickedPort.BlockPos, out var startNodeN))
                    {
                        tracedLinks = TraceFromPort(links, startNodeN, lastPickedPort.PortId, lastPickedPort.Dir);
                        if (lastPickedPort.IsBidirectional && lastPickedPort.AltPortId != null)
                        {
                            var altTraced = TraceFromPort(links, startNodeN, lastPickedPort.AltPortId, PortDir.Out);
                            tracedLinks.UnionWith(altTraced);
                        }
                    }
                }

                var visibleLinks = BuildVisibleLinkList(links, plrEnt.Pos.Dimension, plrEnt.CameraPos, WireShowDist, focused);
                if (visibleLinks.Count > MaxRenderedLinks)
                {
                    visibleLinks.Sort((a, b) => LinkDistanceScore(a, plrEnt.CameraPos).CompareTo(LinkDistanceScore(b, plrEnt.CameraPos)));
                    visibleLinks.RemoveRange(MaxRenderedLinks, visibleLinks.Count - MaxRenderedLinks);
                }

                var rs = GetOrBuildRouteState(visibleLinks);
                rs.Occ.Clear();

                var ordered = visibleLinks
                    .OrderBy(l => $"{l.FromX},{l.FromY},{l.FromZ},{l.FromDim}|{l.FromPortId}->{l.ToX},{l.ToY},{l.ToZ},{l.ToDim}|{l.ToPortId}")
                    .ToList();

                // PASS 1: baseline stamp
                for (int i = 0; i < ordered.Count; i++)
                {
                    var l = ordered[i];

                    var aPos = new BlockPos(l.FromX, l.FromY, l.FromZ, l.FromDim);
                    var bPos = new BlockPos(l.ToX, l.ToY, l.ToZ, l.ToDim);

                    // Get render offsets for PASS 1 too
                    Vec3f aOff = GetRenderOffset(aPos);
                    Vec3f bOff = GetRenderOffset(bPos);

                    double aOx = aOff?.X ?? 0, aOy = aOff?.Y ?? 0, aOz = aOff?.Z ?? 0;
                    double bOx = bOff?.X ?? 0, bOy = bOff?.Y ?? 0, bOz = bOff?.Z ?? 0;

                    Vec3d A = new Vec3d(aPos.X + 0.5 + aOx, aPos.Y + 0.5 + aOy, aPos.Z + 0.5 + aOz);
                    Vec3d B = new Vec3d(bPos.X + 0.5 + bOx, bPos.Y + 0.5 + bOy, bPos.Z + 0.5 + bOz);

                    string stableKey = $"{l.FromX},{l.FromY},{l.FromZ},{l.FromDim}|{l.FromPortId}->{l.ToX},{l.ToY},{l.ToZ},{l.ToDim}|{l.ToPortId}";

                    var tmp = new List<Vec3d>(8);
                    BuildRoute3D_Routed(A, B, stableKey, rs, tmp,
                        laneStep: 0.25, lanesPerSide: 6,
                        cell: 0.5, occWeight: 0.0);
                }

                // PASS 2: build wire markers
                for (int i = 0; i < ordered.Count; i++)
                {
                    var l = ordered[i];

                    var aPos = new BlockPos(l.FromX, l.FromY, l.FromZ, l.FromDim);
                    var bPos = new BlockPos(l.ToX, l.ToY, l.ToZ, l.ToDim);

                    double da2 = plrEnt.CameraPos.SquareDistanceTo(aPos.X + 0.5, aPos.Y + 0.5, aPos.Z + 0.5);
                    double db2 = plrEnt.CameraPos.SquareDistanceTo(bPos.X + 0.5, bPos.Y + 0.5, bPos.Z + 0.5);
                    if (da2 > show2 && db2 > show2) continue;

                    var abe = capi.World.BlockAccessor.GetBlockEntity(aPos);
                    var bbe = capi.World.BlockAccessor.GetBlockEntity(bPos);
                    if (abe == null && bbe == null) continue;

                    float targetAlpha = LitAlpha;

                    if (tracedLinks != null)
                    {
                        targetAlpha = tracedLinks.Contains(MakeKey(l)) ? LitAlpha : DimAlpha;
                    }
                    else if (focused != null)
                    {
                        bool touchesFocused = aPos.Equals(focused) || bPos.Equals(focused);
                        targetAlpha = touchesFocused ? LitAlpha : DimAlpha;
                    }

                    var realFromPos = new BlockPos(l.FromX, l.FromY, l.FromZ, l.FromDim);
                    var realToPos = new BlockPos(l.ToX, l.ToY, l.ToZ, l.ToDim);

                    BlockPos drawFromPos = realFromPos;
                    string drawFromPort = l.FromPortId;

                    var dk = ToKey(l.ToNodeIdN, l.ToPortId);
                    if (displayFrom.TryGetValue(dk, out var df))
                    {
                        drawFromPos = df.Pos;
                        drawFromPort = df.PortId;
                    }

                    // Always use helper method to get render offsets
                    Vec3f aOffset = GetRenderOffset(drawFromPos);
                    Vec3f bOffset = GetRenderOffset(realToPos);

                    var wm = new WireMarker
                    {
                        APos = drawFromPos,
                        APortId = drawFromPort,
                        ARenderOffset = aOffset,
                        BPos = realToPos,
                        BPortId = l.ToPortId,
                        BRenderOffset = bOffset,
                        WidthPx = 3f,
                        Alpha = targetAlpha,
                        Traced = (tracedLinks != null && tracedLinks.Contains(MakeKey(l))),
                        RouteWorld = new List<Vec3d>(8)
                    };

                    // Apply render offsets to wire route endpoints - use the correct positions!
                    double aOx = aOffset?.X ?? 0;
                    double aOy = aOffset?.Y ?? 0;
                    double aOz = aOffset?.Z ?? 0;
                    double bOx = bOffset?.X ?? 0;
                    double bOy = bOffset?.Y ?? 0;
                    double bOz = bOffset?.Z ?? 0;

                    Vec3d A = new Vec3d(drawFromPos.X + 0.5 + aOx, drawFromPos.Y + 0.5 + aOy, drawFromPos.Z + 0.5 + aOz);
                    Vec3d B = new Vec3d(realToPos.X + 0.5 + bOx, realToPos.Y + 0.5 + bOy, realToPos.Z + 0.5 + bOz);

                    string stableKey = $"{l.FromX},{l.FromY},{l.FromZ},{l.FromDim}|{l.FromPortId}->{l.ToX},{l.ToY},{l.ToZ},{l.ToDim}|{l.ToPortId}";

                    BuildRoute3D_Routed(A, B, stableKey, rs, wm.RouteWorld,
                        laneStep: 0.25, lanesPerSide: 6,
                        cell: 0.5, occWeight: 0.2);

                    wires.Add(wm);
                }

                // Add endpoint node markers from the link cache
                var have = new HashSet<string>(nodes.Count);
                for (int i = 0; i < nodes.Count; i++) have.Add(Key(nodes[i].BlockPos));

                int endpointNodesAdded = 0;
                for (int i = 0; i < ordered.Count && endpointNodesAdded < MaxEndpointNodes; i++)
                {
                    var l = ordered[i];

                    var aPos = new BlockPos(l.FromX, l.FromY, l.FromZ, l.FromDim);
                    var bPos = new BlockPos(l.ToX, l.ToY, l.ToZ, l.ToDim);

                    AddEndpointNode(aPos);
                    AddEndpointNode(bPos);

                    void AddEndpointNode(BlockPos p)
                    {
                        if (p == null || p.dimension != plrEnt.Pos.Dimension) return;
                        if (plrEnt.CameraPos.SquareDistanceTo(p.X + 0.5, p.Y + 0.5, p.Z + 0.5) > show2) return;
                        string k = Key(p);
                        if (have.Contains(k)) return;

                        // Get render offset before projection check
                        Vec3f offset = GetRenderOffset(p);

                        if (!TryProjectOnScreen(p, offset, capi, out _)) return;

                        var be = capi.World.BlockAccessor.GetBlockEntity(p);
                        if (be == null) return;

                        float dist = (float)plrEnt.CameraPos.DistanceTo(new Vec3d(p.X + 0.5, p.Y + 0.5, p.Z + 0.5));

                        nodes.Add(new NodeMarker
                        {
                            BlockPos = p,
                            NodeId = Guid.Empty,
                            ConeAlpha = 0.85f,
                            Dist = dist,
                            RenderOffset = offset
                        });

                        have.Add(k);
                        endpointNodesAdded++;
                    }
                }
            }

            renderer.SetMarkers(nodes, ports, focused, wires);

            if (drag != null)
            {
                if (renderer.TryGetPortScreenPos(drag.FromPos, drag.FromPortId, out float ax, out float ay)
                    && TryGetWandTipScreen(out float bx, out float by))
                {
                    renderer.SetDragPreview(true, ax, ay, bx, by, alpha: 0.95f, widthPx: 3f);
                }
                else
                {
                    renderer.SetDragPreview(false);
                }
            }
            else
            {
                renderer.SetDragPreview(false);
            }

            if (renderer.TryPickPortAtScreen(capi.Input.MouseX, capi.Input.MouseY, out var pm))
                lastPickedPort = pm;
            else
                lastPickedPort = null;
        }


        static List<CircuitsLinkDelta> BuildVisibleLinkList(List<CircuitsLinkDelta> linkList, int dimension, Vec3d cameraPos, float showDist, BlockPos focused)
        {
            var result = new List<CircuitsLinkDelta>(Math.Min(linkList?.Count ?? 0, MaxRenderedLinks));
            if (linkList == null || linkList.Count == 0) return result;

            double show2 = showDist * showDist;
            for (int i = 0; i < linkList.Count; i++)
            {
                var l = linkList[i];
                if (l == null) continue;
                if (l.FromDim != dimension || l.ToDim != dimension) continue;

                bool touchesFocused = focused != null
                    && ((l.FromX == focused.X && l.FromY == focused.Y && l.FromZ == focused.Z && l.FromDim == focused.dimension)
                        || (l.ToX == focused.X && l.ToY == focused.Y && l.ToZ == focused.Z && l.ToDim == focused.dimension));

                if (!touchesFocused && LinkDistanceScore(l, cameraPos) > show2) continue;

                result.Add(l);
            }

            return result;
        }

        static double LinkDistanceScore(CircuitsLinkDelta l, Vec3d cameraPos)
        {
            double da2 = cameraPos.SquareDistanceTo(l.FromX + 0.5, l.FromY + 0.5, l.FromZ + 0.5);
            double db2 = cameraPos.SquareDistanceTo(l.ToX + 0.5, l.ToY + 0.5, l.ToZ + 0.5);
            return Math.Min(da2, db2);
        }

        static void BuildRoute3D_Routed(
            Vec3d A, Vec3d B, string stableKey,
            RouteState rs,
            List<Vec3d> outPts,
            double laneStep = 0.25, int lanesPerSide = 6,
            double cell = 0.5, double occWeight = 0.2)
        {
            outPts.Clear();
            if (A.SquareDistanceTo(B) < 1e-6) { outPts.Add(A); outPts.Add(B); return; }

            double y0 = Math.Round((Math.Max(A.Y, B.Y) + 0.25) * 4) / 4.0;
            double[] yLayers = { y0, y0 + 0.25, y0 + 0.5, y0 + 0.75 };
            int laneX = HashToLane(stableKey + "|X", lanesPerSide);
            int laneZ = HashToLane(stableKey + "|Z", lanesPerSide);

            double bestCost = double.MaxValue;
            List<Vec3d> best = null;
            int bestLayer = 0;

            for (int li = 0; li < yLayers.Length; li++)
            {
                double y = yLayers[li];

                var direct = new List<Vec3d>(2) { A, B };
                Consider(direct, li, CostLengthOnly(direct));

                var l1 = new List<Vec3d>(6);
                BuildPlaneL_XthenZ(A, B, y, l1);
                Consider(l1, li, CostWithOcc(rs.Occ, l1, cell, occWeight, li));

                var l2 = new List<Vec3d>(6);
                BuildPlaneL_ZthenX(A, B, y, l2);
                Consider(l2, li, CostWithOcc(rs.Occ, l2, cell, occWeight, li));

                var t1 = new List<Vec3d>(8);
                var t2 = new List<Vec3d>(8);
                BuildCandidate_ZthenX(A, B, stableKey, rs, y, laneX, laneZ, laneStep, t1);
                BuildCandidate_XthenZ(A, B, stableKey, rs, y, laneX, laneZ, laneStep, t2);
                Consider(t1, li, CostWithOcc(rs.Occ, t1, cell, occWeight, li));
                Consider(t2, li, CostWithOcc(rs.Occ, t2, cell, occWeight, li));
            }

            outPts.AddRange(best);

            StampRoute(rs.Occ, outPts, cell, bestLayer, 1);

            void Consider(List<Vec3d> cand, int layer, double cost)
            {
                if (cost < bestCost) { bestCost = cost; best = cand; bestLayer = layer; }
            }

            static double CostLengthOnly(List<Vec3d> pts)
            {
                double len = 0;
                for (int i = 1; i < pts.Count; i++) len += pts[i - 1].DistanceTo(pts[i]);
                return len;
            }

            static double CostWithOcc(Dictionary<long, int> occ, List<Vec3d> pts, double cell, double occWeight, int iyLayer)
                => CostLengthOnly(pts) + TurnPenalty(pts, 0.05) + occWeight * OccAlong(occ, pts, cell, iyLayer);

            static double OccAlong(Dictionary<long, int> occ, List<Vec3d> pts, double cell, int iyLayer)
            {
                double occSum = 0;
                for (int i = 1; i < pts.Count; i++)
                {
                    var p0 = pts[i - 1];
                    var p1 = pts[i];
                    if (!NearlyEqual(p0.Y, p1.Y)) continue;

                    int steps = (int)Math.Ceiling(Math.Max(Math.Abs(p1.X - p0.X), Math.Abs(p1.Z - p0.Z)) / cell);
                    if (steps < 1) steps = 1;

                    for (int s = 0; s <= steps; s++)
                    {
                        double t = s / (double)steps;
                        double x = p0.X + (p1.X - p0.X) * t;
                        double z = p0.Z + (p1.Z - p0.Z) * t;
                        int ix = (int)Math.Round(x / cell);
                        int iz = (int)Math.Round(z / cell);
                        occ.TryGetValue(Pack(ix, iz, iyLayer), out int v);
                        occSum += v;
                    }
                }
                return occSum;
            }
        }

        static void BuildPlaneL_XthenZ(Vec3d A, Vec3d B, double y, List<Vec3d> pts)
        {
            pts.Clear();
            pts.Add(A);
            pts.Add(new Vec3d(A.X, y, A.Z));
            pts.Add(new Vec3d(B.X, y, A.Z));
            pts.Add(new Vec3d(B.X, y, B.Z));
            pts.Add(B);
            RemoveDegenerate(pts);
        }

        static void BuildPlaneL_ZthenX(Vec3d A, Vec3d B, double y, List<Vec3d> pts)
        {
            pts.Clear();
            pts.Add(A);
            pts.Add(new Vec3d(A.X, y, A.Z));
            pts.Add(new Vec3d(A.X, y, B.Z));
            pts.Add(new Vec3d(B.X, y, B.Z));
            pts.Add(B);
            RemoveDegenerate(pts);
        }

        static double TurnPenalty(List<Vec3d> pts, double perTurn)
        {
            int turns = 0;
            for (int i = 2; i < pts.Count; i++)
            {
                var a = pts[i - 2];
                var b = pts[i - 1];
                var c = pts[i];

                if (Math.Abs(a.Y - b.Y) > 1e-6 || Math.Abs(b.Y - c.Y) > 1e-6) continue;

                var abx = b.X - a.X; var abz = b.Z - a.Z;
                var bcx = c.X - b.X; var bcz = c.Z - b.Z;

                bool abX = Math.Abs(abx) > Math.Abs(abz);
                bool bcX = Math.Abs(bcx) > Math.Abs(bcz);
                if (abX != bcX) turns++;
            }
            return turns * perTurn;
        }

        static int HashToLane(string key, int lanesPerSide)
        {
            unchecked
            {
                int h = 17;
                for (int i = 0; i < key.Length; i++) h = h * 31 + key[i];
                int lanes = lanesPerSide * 2 + 1;
                int idx = Math.Abs(h) % lanes;
                return idx - lanesPerSide;
            }
        }

        static void BuildCandidate_ZthenX(
            Vec3d A, Vec3d B, string stableKey, RouteState rs,
            double y, int laneX, int laneZ, double laneStep,
            List<Vec3d> pts)
        {
            pts.Clear();
            pts.Add(A);
            pts.Add(new Vec3d(A.X, y, A.Z));

            double zLane = rs.ZTrunk + laneZ * laneStep;
            double xLane = rs.XTrunk + laneX * laneStep;

            pts.Add(new Vec3d(A.X, y, zLane));
            pts.Add(new Vec3d(xLane, y, zLane));
            pts.Add(new Vec3d(xLane, y, B.Z));
            pts.Add(new Vec3d(B.X, y, B.Z));
            pts.Add(B);

            RemoveDegenerate(pts);
        }

        static void BuildCandidate_XthenZ(
            Vec3d A, Vec3d B, string stableKey, RouteState rs,
            double y, int laneX, int laneZ, double laneStep,
            List<Vec3d> pts)
        {
            pts.Clear();
            pts.Add(A);
            pts.Add(new Vec3d(A.X, y, A.Z));

            double zLane = rs.ZTrunk + laneZ * laneStep;
            double xLane = rs.XTrunk + laneX * laneStep;

            pts.Add(new Vec3d(xLane, y, A.Z));
            pts.Add(new Vec3d(xLane, y, zLane));
            pts.Add(new Vec3d(B.X, y, zLane));
            pts.Add(new Vec3d(B.X, y, B.Z));
            pts.Add(B);

            RemoveDegenerate(pts);
        }

        static void RemoveDegenerate(List<Vec3d> pts)
        {
            for (int i = pts.Count - 2; i >= 0; i--)
            {
                if (pts[i].SquareDistanceTo(pts[i + 1]) < 1e-10) pts.RemoveAt(i + 1);
            }

            for (int i = pts.Count - 2; i >= 1; i--)
            {
                var a = pts[i - 1];
                var b = pts[i];
                var c = pts[i + 1];

                bool colX = NearlyEqual(a.X, b.X) && NearlyEqual(b.X, c.X);
                bool colY = NearlyEqual(a.Y, b.Y) && NearlyEqual(b.Y, c.Y);
                bool colZ = NearlyEqual(a.Z, b.Z) && NearlyEqual(b.Z, c.Z);

                if ((colX && colY) || (colX && colZ) || (colY && colZ))
                    pts.RemoveAt(i);
            }
        }

        static bool NearlyEqual(double a, double b, double eps = 1e-6) => Math.Abs(a - b) <= eps;

        static void StampRoute(Dictionary<long, int> occ, List<Vec3d> pts, double cell, int iyLayer, int inc)
        {
            for (int i = 1; i < pts.Count; i++)
            {
                var p0 = pts[i - 1];
                var p1 = pts[i];

                if (!NearlyEqual(p0.Y, p1.Y)) continue;

                int steps = (int)Math.Ceiling(Math.Max(Math.Abs(p1.X - p0.X), Math.Abs(p1.Z - p0.Z)) / cell);
                if (steps < 1) steps = 1;

                for (int s = 0; s <= steps; s++)
                {
                    double t = s / (double)steps;
                    double x = p0.X + (p1.X - p0.X) * t;
                    double z = p0.Z + (p1.Z - p0.Z) * t;

                    int ix = (int)Math.Round(x / cell);
                    int iz = (int)Math.Round(z / cell);

                    long k = Pack(ix, iz, iyLayer);
                    occ[k] = occ.TryGetValue(k, out int v) ? (v + inc) : inc;
                }
            }
        }

        RouteState GetOrBuildRouteState(List<CircuitsLinkDelta> linkList)
        {
            routeState ??= new RouteState();

            double sx = 0, sy = 0, sz = 0;
            int n = 0;

            for (int i = 0; i < linkList.Count; i++)
            {
                var l = linkList[i];
                sx += l.FromX + 0.5; sy += l.FromY + 0.5; sz += l.FromZ + 0.5; n++;
                sx += l.ToX + 0.5; sy += l.ToY + 0.5; sz += l.ToZ + 0.5; n++;
            }

            if (n == 0) { routeState.BaseY = 0; routeState.XTrunk = 0; routeState.ZTrunk = 0; return routeState; }

            double cy = sy / n;
            double cx = sx / n;
            double cz = sz / n;

            routeState.BaseY = Math.Round((cy + 0.2) * 4) / 4.0;
            routeState.XTrunk = Math.Round(cx * 2) / 2.0;
            routeState.ZTrunk = Math.Round(cz * 2) / 2.0;

            return routeState;
        }

        void OnLinkDelta(CircuitsLinkDelta msg)
        {
            if (msg == null) return;

            links.RemoveAll(x =>
                x.FromNodeIdN == msg.FromNodeIdN && x.FromPortId == msg.FromPortId &&
                x.ToNodeIdN == msg.ToNodeIdN && x.ToPortId == msg.ToPortId);

            if (msg.Added) links.Add(msg);

            scanAcc = ScanInterval; // wake the scan immediately
        }

        void OnSnapshot(CircuitsSnapshot msg)
        {
            if (msg == null) return;

            links = msg.Links ?? new List<CircuitsLinkDelta>();
            displayFrom.Clear();

            scanAcc = ScanInterval; // wake the scan immediately
        }

        static string Key(BlockPos p) => $"{p.X},{p.Y},{p.Z},{p.dimension}";

        static bool TryProjectOnScreen(BlockPos pos, Vec3f renderOffset, ICoreClientAPI capi, out Vec3d scr)
        {
            double ox = renderOffset?.X ?? 0;
            double oy = renderOffset?.Y ?? 0;
            double oz = renderOffset?.Z ?? 0;

            Vec3d world = new Vec3d(pos.X + 0.5 + ox, pos.Y + 0.5 + oy, pos.Z + 0.5 + oz);
            WorldToCamera(capi.Render.PerspectiveViewMat, world.X, world.Y, world.Z, out _, out _, out double cz);
            if (cz >= 0) return false;

            scr = MatrixToolsd.Project(
                world,
                capi.Render.PerspectiveProjectionMat,
                capi.Render.PerspectiveViewMat,
                capi.Render.FrameWidth,
                capi.Render.FrameHeight
            );
            if (scr.Z < 0) return false;

            const double Margin = 200;
            return scr.X >= -Margin && scr.X <= capi.Render.FrameWidth + Margin
                && scr.Y >= -Margin && scr.Y <= capi.Render.FrameHeight + Margin;
        }

        static void AddStackedPorts(
            List<PortMarker> dest,
            BlockPos pos,
            List<PortDef> ins,
            List<PortDef> outs,
            float colX,
            float vSpacing,
            float coneAlpha,
            float dist,
            Vec3f renderOffset)
        {
            static List<PortDef> Filter(List<PortDef> src)
            {
                if (src == null) return null;
                var res = new List<PortDef>(src.Count);
                for (int i = 0; i < src.Count; i++)
                {
                    var p = src[i];
                    if (p?.PortID == null) continue;
                    res.Add(p);
                }
                return res;
            }

            var fin = Filter(ins);
            var fout = Filter(outs);

            int nin = fin?.Count ?? 0;
            int nout = fout?.Count ?? 0;
            int n = nin + nout;
            if (n == 0) return;

            if (nin == 1 && nout == 1 && fin[0].Type == fout[0].Type)
            {
                dest.Add(new PortMarker
                {
                    BlockPos = pos,
                    PortId = fin[0].PortID,
                    AltPortId = fout[0].PortID,
                    Dist = dist,
                    Dir = PortDir.In,
                    Type = fin[0].Type,
                    DxPx = 0,
                    DyPx = 0,
                    ConeAlpha = coneAlpha,
                    RenderOffset = renderOffset,
                    IsBidirectional = true
                });
                return;
            }

            float y = -((n - 1) * vSpacing) * 0.5f;

            for (int i = 0; i < nin; i++)
            {
                var p = fin[i];
                dest.Add(new PortMarker
                {
                    BlockPos = pos,
                    PortId = p.PortID,
                    Dist = dist,
                    Dir = p.Dir,
                    Type = p.Type,
                    DxPx = colX,
                    DyPx = y,
                    ConeAlpha = coneAlpha,
                    RenderOffset = renderOffset
                });
                y += vSpacing;
            }

            for (int i = 0; i < nout; i++)
            {
                var p = fout[i];
                dest.Add(new PortMarker
                {
                    BlockPos = pos,
                    PortId = p.PortID,
                    Dist = dist,
                    Dir = p.Dir,
                    Type = p.Type,
                    DxPx = colX,
                    DyPx = y,
                    ConeAlpha = coneAlpha,
                    RenderOffset = renderOffset
                });
                y += vSpacing;
            }
        }

        ItemSlot GetWandSlot(IPlayer player)
        {
            var im = player?.InventoryManager;
            if (im == null) return null;

            var a = im.ActiveHotbarSlot;
            if (IsWandStack(a?.Itemstack)) return a;

            var o = im.OffhandHotbarSlot;
            if (IsWandStack(o?.Itemstack)) return o;

            return null;
        }

        static bool IsWandStack(ItemStack stack)
        {
            var code = stack?.Collectible?.Code;
            return code != null && code.Domain == "circuits" && code.Path == "circuitwand";
        }

        bool IsWandHeld(IPlayer player) => GetWandSlot(player) != null;

        public override void Dispose()
        {
            if (capi != null)
            {
                if (renderer != null)
                {
                    capi.Event.UnregisterRenderer(renderer, EnumRenderStage.Ortho);
                    renderer.Dispose();
                    renderer = null;
                }
                capi = null;
            }
            base.Dispose();
        }

        static bool SamePort(PortMarker a, PortMarker b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return a.BlockPos?.Equals(b.BlockPos) == true
                && a.PortId == b.PortId
                && a.Dir == b.Dir
                && a.Type == b.Type;
        }

        public sealed class NodeMarker
        {
            public BlockPos BlockPos;
            public Guid NodeId;
            public float ConeAlpha;
            public float Dist;
            public Vec3f RenderOffset;
        }

        public sealed class PortMarker
        {
            public BlockPos BlockPos;
            public string PortId;
            public PortDir Dir;
            public SignalType Type;
            public float DxPx;
            public float DyPx;
            public float ConeAlpha;
            public float Dist;
            public Vec3f RenderOffset;
            public bool IsBidirectional;
            public string AltPortId;
        }

        public sealed class WireMarker
        {
            public BlockPos APos;
            public string APortId;
            public Vec3f ARenderOffset;

            public BlockPos BPos;
            public string BPortId;
            public Vec3f BRenderOffset;

            public float Alpha = 1f;
            public float WidthPx = 3f;
            public List<Vec3d> RouteWorld;
            public bool Traced;
        }

        private sealed class DragState
        {
            public BlockPos FromPos;
            public string FromPortId;
            public PortDir FromDir;
            public SignalType Type;
            public bool IsBidirectional;
            public string AltPortId;
        }

        void Debug(string msg)
        {
            if (!DebugWand) return;
            capi.ShowChatMessage("[DEBUG] " + msg);
        }

        bool TryGetWandTipScreen(out float sx, out float sy)
        {
            sx = sy = 0;

            var ent = capi.World.Player?.Entity;
            if (ent == null) return false;

            Vec3d cam = ent.CameraPos;
            float yaw = ent.Pos.Yaw;
            float pitch = ent.Pos.Pitch;
            Vec3d fwd = new Vec3d(
                -Math.Sin(yaw) * Math.Cos(pitch),
                Math.Sin(pitch),
                -Math.Cos(yaw) * Math.Cos(pitch)
            );

            Vec3d up = new Vec3d(0, 1, 0);
            Vec3d right = fwd.Cross(up).Normalize();

            Vec3d tipWorld =
                cam
                + fwd * 0.65
                + right * 0.28
                + up * -0.22;

            Vec3d scr = MatrixToolsd.Project(
                tipWorld,
                capi.Render.PerspectiveProjectionMat,
                capi.Render.PerspectiveViewMat,
                capi.Render.FrameWidth,
                capi.Render.FrameHeight
            );
            if (scr.Z < 0) return false;

            sx = (float)scr.X;
            sy = capi.Render.FrameHeight - (float)scr.Y;
            return true;
        }

        void PruneDeadLinks()
        {
            if (links == null || links.Count == 0) return;

            links.RemoveAll(l =>
            {
                var aPos = new BlockPos(l.FromX, l.FromY, l.FromZ, l.FromDim);
                var bPos = new BlockPos(l.ToX, l.ToY, l.ToZ, l.ToDim);
                return ShouldPruneEndpoint(aPos) || ShouldPruneEndpoint(bPos);
            });
        }

        bool ShouldPruneEndpoint(BlockPos p)
        {
            var ba = capi.World.BlockAccessor;

            int chunkX = p.X >> 5;
            int chunkY = p.Y >> 5;
            int chunkZ = p.Z >> 5;

            var chunk = ba.GetChunk(chunkX, chunkY, chunkZ);
            if (chunk == null) return false;
            return ba.GetBlockEntity(p) == null;
        }
    }
}
