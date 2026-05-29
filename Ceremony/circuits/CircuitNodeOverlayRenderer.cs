using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static circuits.CircuitsClientOverlaySystem;

#nullable disable

namespace circuits
{
    public sealed class CircuitNodeOverlayRenderer : IRenderer, IDisposable
    {
        readonly ICoreClientAPI capi;

        MeshRef quad;
        readonly Matrixf model = new Matrixf();
        readonly Vec4f rgba = new Vec4f(0.5f, 0.85f, 1.0f, 0.95f);

        const float PortBaseSizePx = 42f;
        const float PortMinSizePx = 42f;
        const float PortMaxSizePx = 42f;

        const float NodeBaseSizePx = 64f;
        const float NodeMinSizePx = 40f;
        const float NodeMaxSizePx = 120f;

        float logAcc;

        List<NodeMarker> nodeMarkers;
        List<PortMarker> portMarkers;
        List<WireMarker> wireMarkers;
        BlockPos focusedNode;

        PortMarker hoveredPort;
        float hoveredPortBlend;

        const float HoverBlendSpeed = 12f;
        const float HoverWhiteStrength = 0.85f;
        public double RenderOrder => 0.41;
        public int RenderRange => 9999;

        const float SizeNearDist = 2f;
        const float SizeFarDist = 10f;
        const float FarMinScale = 0.15f;

        LoadedTexture circleTex;
        string samplerName = "tex2d";

        bool hasDragPreview;
        float dragAx, dragAy, dragBx, dragBy;
        float dragAlpha = 1f;
        float dragWidth = 3f;

        readonly List<Vec2f> scratchScreen = new List<Vec2f>(16);
        readonly List<Vec3d> scratchWorld = new List<Vec3d>(64);
        List<Vec3d> dragRouteWorld;

        const int SplineSubdivisions = 8;
        const double NearClipZ = -0.1;

        const float FocusBlendSpeed = 5f;
        readonly Dictionary<string, float> focusBlend = new Dictionary<string, float>(256);

        // Screen-space cache
        readonly Dictionary<string, (float x, float y, float a)> centerByPos = new();
        readonly Dictionary<string, (float x, float y)> portByPosAndId = new();
        readonly Dictionary<string, Vec3f> offsetByPos = new();

        const float WireDimBlendSpeed = 10f;
        readonly Dictionary<string, float> wireBlend = new Dictionary<string, float>(512);
        readonly HashSet<string> wireSeen = new HashSet<string>();

        static string WireKey(WireMarker w)
        {
            // stable key across frames (includes ports)
            string a = $"{w.APos.X},{w.APos.Y},{w.APos.Z},{w.APos.dimension}";
            string b = $"{w.BPos.X},{w.BPos.Y},{w.BPos.Z},{w.BPos.dimension}";
            return $"{a}|{w.APortId ?? ""}->{b}|{w.BPortId ?? ""}";
        }

        static string Key(BlockPos p) => $"{p.X},{p.Y},{p.Z},{p.dimension}";
        static string PortKey(BlockPos p, string portId) => $"{Key(p)}|{portId}";

        static bool SamePort(PortMarker a, PortMarker b)
        {
            if (a == null || b == null) return false;
            return a.BlockPos.Equals(b.BlockPos) && a.PortId == b.PortId && a.Dir == b.Dir && a.Type == b.Type;
        }

        static float MoveTowards(float cur, float target, float maxDelta)
        {
            if (cur < target) return Math.Min(cur + maxDelta, target);
            if (cur > target) return Math.Max(cur - maxDelta, target);
            return cur;
        }

        public void SetMarkers(List<NodeMarker> nodes, List<PortMarker> ports, BlockPos focused, List<WireMarker> wires)
        {
            nodeMarkers = nodes;
            portMarkers = ports;
            focusedNode = focused;
            wireMarkers = wires;
        }


        public void SetDragPreview(bool enabled, float ax = 0, float ay = 0, float bx = 0, float by = 0, float alpha = 1f, float widthPx = 3f)
        {
            hasDragPreview = enabled;
            dragAx = ax; dragAy = ay;
            dragBx = bx; dragBy = by;
            dragAlpha = alpha;
            dragWidth = widthPx;
        }

        public CircuitNodeOverlayRenderer(ICoreClientAPI capi)
        {
            this.capi = capi;

            quad = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());

            circleTex = new LoadedTexture(capi);
            var loc = new AssetLocation("circuits", "textures/gui/circle.png");
            var bmp = capi.Assets.Get(loc).ToBitmap(capi);
            capi.Render.LoadTexture(bmp, ref circleTex);
            bmp.Dispose();

            var gui = capi.Render.GetEngineShader(EnumShaderProgram.Gui);
            if (gui != null)
            {
                if (gui.HasUniform("tex2d")) samplerName = "tex2d";
                else if (gui.HasUniform("tex")) samplerName = "tex";
                else if (gui.HasUniform("tex0")) samplerName = "tex0";
            }
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Ortho) return;

            bool haveNodes = nodeMarkers != null && nodeMarkers.Count > 0;
            bool havePorts = portMarkers != null && portMarkers.Count > 0;
            bool haveWires = wireMarkers != null && wireMarkers.Count > 0;
            if (!haveNodes && !havePorts && !haveWires) return;

            IShaderProgram sh = capi.Render.GetEngineShader(EnumShaderProgram.Gui);
            if (sh == null) return;

            IShaderProgram prev = capi.Render.CurrentActiveShader;

            // IMPORTANT: match vanilla pattern
            if (prev != null && prev != sh) prev.Stop();
            sh.Use();
            try
            {
                // Always set the matrices you rely on
                GL.Disable(EnableCap.ScissorTest);
                //GL.Disable(EnableCap.DepthTest);
                GL.Disable(EnableCap.CullFace);
                GL.Enable(EnableCap.Blend);
                GL.BlendEquation(BlendEquationMode.FuncAdd);
                GL.ColorMask(true, true, true, true);
                GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha,
                                     BlendingFactorSrc.One,      BlendingFactorDest.OneMinusSrcAlpha);
                GL.BlendEquation(BlendEquationMode.FuncAdd);
                sh.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);

                logAcc += dt;

                // update focus blend per node
                if (nodeMarkers != null)
                {
                    for (int i = 0; i < nodeMarkers.Count; i++)
                    {
                        var nm = nodeMarkers[i];
                        string k = Key(nm.BlockPos);

                        bool isFocused = focusedNode != null && nm.BlockPos.Equals(focusedNode);
                        float target = isFocused ? 1f : 0f;

                        focusBlend.TryGetValue(k, out float cur);
                        cur = MoveTowards(cur, target, dt * FocusBlendSpeed);
                        focusBlend[k] = cur;
                    }
                }

                float tFocus = 0f;
                if (focusedNode != null && focusBlend.TryGetValue(Key(focusedNode), out float fblend))
                    tFocus = Smoothstep(fblend);

                // build center + offset caches
                centerByPos.Clear();
                offsetByPos.Clear();
                if (nodeMarkers != null)
                {
                    for (int i = 0; i < nodeMarkers.Count; i++)
                    {
                        var m = nodeMarkers[i];
                        offsetByPos[Key(m.BlockPos)] = m.RenderOffset;
                        if (TryProjectCenter(m.BlockPos, m.RenderOffset, out float ax, out float ay))
                        {
                            centerByPos[Key(m.BlockPos)] = (ax, ay, m.ConeAlpha);
                        }
                    }
                }

                // build port cache for focused node
                portByPosAndId.Clear();
                if (focusedNode != null && portMarkers != null && portMarkers.Count > 0 && tFocus > 0.001f)
                {
                    var fKey = Key(focusedNode);

                    // Get the focused node's render offset
                    Vec3f focusedOffset = null;
                    for (int i = 0; i < nodeMarkers.Count; i++)
                    {
                        if (nodeMarkers[i].BlockPos.Equals(focusedNode))
                        {
                            focusedOffset = nodeMarkers[i].RenderOffset;
                            break;
                        }
                    }

                    float ax, ay;
                    if (centerByPos.TryGetValue(fKey, out var c))
                    {
                        ax = c.x; ay = c.y;
                    }
                    else if (!TryProjectCenter(focusedNode, focusedOffset, out ax, out ay))
                    {
                        ax = ay = 0;
                    }

                    if (ax != 0 || ay != 0)
                    {
                        for (int i = 0; i < portMarkers.Count; i++)
                        {
                            var pm = portMarkers[i];
                            if (!pm.BlockPos.Equals(focusedNode)) continue;

                            float px = ax + pm.DxPx;
                            float py = ay + pm.DyPx;

                            portByPosAndId[PortKey(focusedNode, pm.PortId)] = (px, py);
                            if (pm.IsBidirectional && pm.AltPortId != null)
                                portByPosAndId[PortKey(focusedNode, pm.AltPortId)] = (px, py);
                        }
                    }
                }

                // hover pick
                bool portsVisible =
                    focusedNode != null &&
                    portMarkers != null && portMarkers.Count > 0 &&
                    tFocus > 0.01f;

                if (portsVisible && TryPickPortAtScreen(capi.Input.MouseX, capi.Input.MouseY, out var pick, 6f))
                {
                    if (!SamePort(hoveredPort, pick)) hoveredPortBlend = 0f;
                    hoveredPort = pick;
                    hoveredPortBlend = MoveTowards(hoveredPortBlend, 1f, dt * HoverBlendSpeed);
                }
                else
                {
                    hoveredPort = null;
                    hoveredPortBlend = MoveTowards(hoveredPortBlend, 0f, dt * HoverBlendSpeed);
                }

                // ---- PASS: untextured (wires + drag) ----
                if (wireMarkers != null)
                {
                    sh.Uniform("noTexture", 1f);
                    wireSeen.Clear();

                    for (int i = 0; i < wireMarkers.Count; i++)
                    {
                        var w = wireMarkers[i];
                        string k = WireKey(w);
                        wireSeen.Add(k);

                        float target = GameMath.Clamp(w.Alpha, 0f, 1f);

                        wireBlend.TryGetValue(k, out float cur);
                        cur = MoveTowards(cur, target, dt * WireDimBlendSpeed);
                        wireBlend[k] = cur;

                        float alpha = cur * 0.85f;
                        if (alpha <= 0.01f) continue;

                        DrawWireScreen(sh, w, w.WidthPx, 0.9f, 0.9f, 0.9f, alpha);
                    }

                    if (wireBlend.Count > 1024)
                    {
                        var keys = wireBlend.Keys.ToList();
                        for (int i = 0; i < keys.Count; i++)
                            if (!wireSeen.Contains(keys[i])) wireBlend.Remove(keys[i]);
                    }
                }

                if (hasDragPreview)
                {
                    sh.Uniform("noTexture", 1f);

                    if (dragAx != 0 || dragAy != 0 || dragBx != 0 || dragBy != 0)
                    {
                        DrawLinePx(sh, dragAx, dragAy, dragBx, dragBy, dragWidth, 0.9f, 0.9f, 0.9f, dragAlpha);
                    }
                }

                // ---- PASS: textured markers ----
                sh.Uniform("noTexture", 0f);
                sh.BindTexture2D(samplerName, circleTex.TextureId, 0);

                if (nodeMarkers != null)
                {
                    for (int i = 0; i < nodeMarkers.Count; i++)
                    {
                        var m = nodeMarkers[i];
                        var pos = m.BlockPos;

                        focusBlend.TryGetValue(Key(pos), out float blend);
                        float t = Smoothstep(blend);

                        float nodeAlpha = m.ConeAlpha * (1f - t);
                        if (nodeAlpha <= 0.01f) continue;

                        float scaledBase = GameMath.Lerp(NodeBaseSizePx, PortBaseSizePx, t);
                        float scaledMin = GameMath.Lerp(NodeMinSizePx, PortMinSizePx, t);
                        float scaledMax = GameMath.Lerp(NodeMaxSizePx, PortMaxSizePx, t);

                        float pop = 1f + (1.25f - 1f) * (4f * blend * (1f - blend));

                        DrawMarkerAtBlock(sh, pos, m.RenderOffset, 0, 0,
                            scaledBase * pop, scaledMin * pop, scaledMax * pop,
                            nodeAlpha, 0.35f, 0.75f, 1.0f, m.Dist, false);
                    }
                }

                if (portMarkers != null && portMarkers.Count > 0 && focusedNode != null)
                {
                    if (tFocus > 0.01f)
                    {
                        float portScale = (float)Math.Sqrt(tFocus);

                        for (int i = 0; i < portMarkers.Count; i++)
                        {
                            var m = portMarkers[i];

                            float rr, gg, bb;
                            if (m.IsBidirectional) { rr = 0.75f; gg = 0.45f; bb = 0.90f; }
                            else if (m.Dir == PortDir.In) { rr = 0.50f; gg = 0.85f; bb = 1.00f; }
                            else { rr = 1.00f; gg = 0.75f; bb = 0.35f; }

                            if (hoveredPortBlend > 0.001f && SamePort(m, hoveredPort))
                            {
                                float h = Smoothstep(hoveredPortBlend * tFocus);
                                float w = HoverWhiteStrength * h;
                                rr = GameMath.Lerp(rr, 1f, w);
                                gg = GameMath.Lerp(gg, 1f, w);
                                bb = GameMath.Lerp(bb, 1f, w);
                            }

                            float portAlpha = m.ConeAlpha * tFocus;
                            if (portAlpha <= 0.01f) continue;

                            float dx = m.DxPx * tFocus;
                            float dy = m.DyPx * tFocus;

                            DrawMarkerAtBlock(
                                sh, m.BlockPos, m.RenderOffset,
                                dx, dy,
                                PortBaseSizePx * portScale, PortMinSizePx * portScale, PortMaxSizePx * portScale,
                                portAlpha,
                                rr, gg, bb,
                                m.Dist,
                                true
                            );
                        }
                    }
                }
            }
            finally
            {
                sh.Uniform("noTexture", 0f);
                sh.Uniform("rgbaIn", new Vec4f(1, 1, 1, 1));
                sh.UniformMatrix("modelViewMatrix", capi.Render.CurrentModelviewMatrix);

                sh.Stop();
                if (prev != null) prev.Use();
            }
        }
        public void SetDragPreview3D(bool enabled, List<Vec3d> routeWorld, float alpha = 1f, float widthPx = 3f)
        {
            hasDragPreview = enabled;
            dragRouteWorld = enabled ? routeWorld : null;
            dragAlpha = alpha;
            dragWidth = widthPx;
        }

        public bool TryGetPortScreenPos(BlockPos pos, string portId, out float x, out float y)
        {
            return TryGetPortScreenPos(pos, portId, null, out x, out y);
        }

        public bool TryGetPortScreenPos(BlockPos pos, string portId, Vec3f renderOffset, out float x, out float y)
        {
            x = y = 0;

            // prefer port
            string pk = PortKey(pos, portId);
            if (focusedNode != null && pos.Equals(focusedNode) && focusBlend.TryGetValue(Key(focusedNode), out float fblend))
            {
                float t = Smoothstep(fblend);
                if (portByPosAndId.TryGetValue(pk, out var p) && centerByPos.TryGetValue(Key(pos), out var c2))
                {
                    x = GameMath.Lerp(c2.x, p.x, t);
                    y = GameMath.Lerp(c2.y, p.y, t);
                    return true;
                }
            }

            string k = Key(pos);
            if (centerByPos.TryGetValue(k, out var c))
            {
                x = c.x; y = c.y;
                return true;
            }

            // Resolve the best available render offset: explicit > cached > none
            Vec3f offset = renderOffset;
            if (offset == null) offsetByPos.TryGetValue(k, out offset);

            return TryProjectCenter(pos, offset, out x, out y);
        }

        bool TryProjectCenter(BlockPos pos, out float ax, out float ay)
        {
            return TryProjectCenter(pos, null, out ax, out ay);
        }

        bool TryProjectCenter(BlockPos pos, Vec3f offset, out float ax, out float ay)
        {
            ax = ay = 0;

            double ox = offset?.X ?? 0;
            double oy = offset?.Y ?? 0;
            double oz = offset?.Z ?? 0;

            Vec3d world = new Vec3d(pos.X + 0.5 + ox, pos.Y + 0.5 + oy, pos.Z + 0.5 + oz);
            Vec3d scr = MatrixToolsd.Project(
                world,
                capi.Render.PerspectiveProjectionMat,
                capi.Render.PerspectiveViewMat,
                capi.Render.FrameWidth,
                capi.Render.FrameHeight
            );
            if (scr.Z < 0) return false;

            ax = (float)scr.X;
            ay = capi.Render.FrameHeight - (float)scr.Y;
            return true;
        }
        void DrawMarkerAtBlock(IShaderProgram sh, BlockPos pos, Vec3f offset, float dxPx, float dyPx,
            float baseSizePx, float minSizePx, float maxSizePx, float coneAlpha,
            float r, float g, float b, float distBlocks, bool fixedSize)
        {
            double ox = offset?.X ?? 0;
            double oy = offset?.Y ?? 0;
            double oz = offset?.Z ?? 0;

            Vec3d world = new Vec3d(pos.X + 0.5 + ox, pos.Y + 0.5 + oy, pos.Z + 0.5 + oz);

            Vec3d scr = MatrixToolsd.Project(
                world,
                capi.Render.PerspectiveProjectionMat,
                capi.Render.PerspectiveViewMat,
                capi.Render.FrameWidth,
                capi.Render.FrameHeight
            );
            if (scr.Z < 0) return;

            float size;

            if (fixedSize)
            {
                size = GameMath.Clamp(baseSizePx, minSizePx, maxSizePx);
                size = (float)Math.Round(size);
            }
            else
            {
                float z = Math.Max(1f, (float)scr.Z);
                const float RefZ = 4f;

                size = baseSizePx * (RefZ / z);
                size = GameMath.Clamp(size, minSizePx, maxSizePx);
                size = (float)Math.Round(size);
                size = GameMath.Clamp(size, minSizePx, maxSizePx);

                float t = GameMath.Clamp((distBlocks - SizeNearDist) / (SizeFarDist - SizeNearDist), 0f, 1f);
                float proxScale = GameMath.Lerp(1.0f, FarMinScale, t * t);
                size *= proxScale;

                float dynamicMin = minSizePx * proxScale;
                size = GameMath.Clamp(size, dynamicMin, maxSizePx);
            }

            float alpha = 0.90f * coneAlpha;

            float ax = (float)scr.X;
            float ay = capi.Render.FrameHeight - (float)scr.Y;

            float x = ax + dxPx - size * 0.5f;
            float y = ay + dyPx - size * 0.5f;

            x = (float)Math.Round(x);
            y = (float)Math.Round(y);

            rgba.Set(r, g, b, alpha);
            sh.Uniform("rgbaIn", rgba);

            DrawQuadPx(sh, x, y, size, size, rgba);
        }

        void DrawQuadPx(IShaderProgram sh, float x, float y, float w, float h, Vec4f color)
        {
            sh.Uniform("rgbaIn", color);              // force every call
            sh.Uniform("noTexture", 0f);              // or 1f depending on pass
            sh.UniformMatrix("modelViewMatrix", model.Values);

            model.Set(capi.Render.CurrentModelviewMatrix)
                 .Translate(x, y, 20f)
                 .Scale(w, h, 0f)
                 .Translate(0.5f, 0.5f, 0f)
                 .Scale(0.5f, 0.5f, 0f);

            sh.UniformMatrix("modelViewMatrix", model.Values);
            capi.Render.RenderMesh(quad);
        }

        void DrawLinePx(IShaderProgram sh, float x0, float y0, float x1, float y1, float thicknessPx, float r, float g, float b, float a)
        {
            rgba.Set(r, g, b, a);
            sh.Uniform("rgbaIn", rgba);
            sh.Uniform("noTexture", 1f);              // or 1f depending on pass
            sh.UniformMatrix("modelViewMatrix", model.Values);


            float dx = x1 - x0;
            float dy = y1 - y0;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.5f) return;

            float ang = (float)Math.Atan2(dy, dx);


            model.Set(capi.Render.CurrentModelviewMatrix)
                 .Translate(x0, y0, 19f)
                 .RotateZ(ang)
                 .Translate(0, -thicknessPx * 0.5f, 0)
                 .Scale(len, thicknessPx, 0f)
                 .Translate(0.5f, 0.5f, 0f)
                 .Scale(0.5f, 0.5f, 0f);

            sh.UniformMatrix("modelViewMatrix", model.Values);
            capi.Render.RenderMesh(quad);
        }

        double ViewZ(Vec3d w)
        {
            var m = capi.Render.PerspectiveViewMat;
            return m[2] * w.X + m[6] * w.Y + m[10] * w.Z + m[14];
        }

        void ProjectForce(Vec3d world, out float sx, out float sy)
        {
            Vec3d scr = MatrixToolsd.Project(
                world,
                capi.Render.PerspectiveProjectionMat,
                capi.Render.PerspectiveViewMat,
                capi.Render.FrameWidth,
                capi.Render.FrameHeight
            );
            sx = (float)scr.X;
            sy = capi.Render.FrameHeight - (float)scr.Y;
        }

        bool TryProject(Vec3d world, out float sx, out float sy)
        {
            sx = sy = 0;
            Vec3d scr = MatrixToolsd.Project(
                world,
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

        static Vec3d LerpVec3d(Vec3d a, Vec3d b, double t)
        {
            return new Vec3d(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t);
        }

        static Vec3d MirrorVec3d(Vec3d anchor, Vec3d other)
        {
            return new Vec3d(
                2 * anchor.X - other.X,
                2 * anchor.Y - other.Y,
                2 * anchor.Z - other.Z);
        }

        static Vec3d CatmullRom(Vec3d p0, Vec3d p1, Vec3d p2, Vec3d p3, double t)
        {
            double t2 = t * t;
            double t3 = t2 * t;
            return new Vec3d(
                0.5 * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                0.5 * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3),
                0.5 * (2 * p1.Z + (-p0.Z + p2.Z) * t + (2 * p0.Z - 5 * p1.Z + 4 * p2.Z - p3.Z) * t2 + (-p0.Z + 3 * p1.Z - 3 * p2.Z + p3.Z) * t3));
        }

        Vec3d WorldFromEndpoint(BlockPos pos, Vec3f offset)
        {
            double ox = offset?.X ?? 0;
            double oy = offset?.Y ?? 0;
            double oz = offset?.Z ?? 0;
            return new Vec3d(pos.X + 0.5 + ox, pos.Y + 0.5 + oy, pos.Z + 0.5 + oz);
        }

        void DrawWireScreen(IShaderProgram sh, WireMarker w, float widthPx, float r, float g, float b, float alpha)
        {
            // Build world-space polyline from route or synthesize from endpoints
            var world = scratchWorld;
            world.Clear();

            if (w.RouteWorld != null && w.RouteWorld.Count >= 2)
            {
                for (int i = 0; i < w.RouteWorld.Count; i++)
                    world.Add(w.RouteWorld[i]);
            }
            else
            {
                world.Add(WorldFromEndpoint(w.APos, w.ARenderOffset));
                world.Add(WorldFromEndpoint(w.BPos, w.BRenderOffset));
            }

            if (world.Count < 2) return;

            // Port-aware screen overrides for wire endpoints
            bool hasAScr = TryGetWireEndpointScreen(w.APos, w.APortId, w.ARenderOffset, out float asx, out float asy);
            bool hasBScr = TryGetWireEndpointScreen(w.BPos, w.BPortId, w.BRenderOffset, out float bsx, out float bsy);

            bool useSpline = world.Count >= 3;
            int spanCount = world.Count - 1;

            for (int span = 0; span < spanCount; span++)
            {
                Vec3d cm0 = span > 0              ? world[span - 1] : MirrorVec3d(world[0], world[1]);
                Vec3d cm1 = world[span];
                Vec3d cm2 = world[span + 1];
                Vec3d cm3 = span + 2 < world.Count ? world[span + 2] : MirrorVec3d(world[world.Count - 1], world[world.Count - 2]);

                int subs = useSpline ? SplineSubdivisions : 1;

                for (int s = 0; s < subs; s++)
                {
                    double st0 = s / (double)subs;
                    double st1 = (s + 1) / (double)subs;

                    Vec3d w0 = useSpline ? CatmullRom(cm0, cm1, cm2, cm3, st0) : cm1;
                    Vec3d w1 = useSpline ? CatmullRom(cm0, cm1, cm2, cm3, st1) : cm2;

                    // Near-plane clip
                    double z0 = ViewZ(w0);
                    double z1 = ViewZ(w1);
                    bool front0 = z0 < NearClipZ;
                    bool front1 = z1 < NearClipZ;

                    if (!front0 && !front1) continue;

                    Vec3d c0 = w0, c1 = w1;
                    bool clipped0 = false, clipped1 = false;

                    if (!front0)
                    {
                        double t = (NearClipZ - z0) / (z1 - z0);
                        c0 = LerpVec3d(w0, w1, t);
                        clipped0 = true;
                    }
                    else if (!front1)
                    {
                        double t = (NearClipZ - z0) / (z1 - z0);
                        c1 = LerpVec3d(w0, w1, t);
                        clipped1 = true;
                    }

                    ProjectForce(c0, out float sx0, out float sy0);
                    ProjectForce(c1, out float sx1, out float sy1);

                    // Override first/last unclipped endpoints with port-aware positions
                    if (span == 0 && s == 0 && !clipped0 && hasAScr)
                    { sx0 = asx; sy0 = asy; }
                    if (span == spanCount - 1 && s == subs - 1 && !clipped1 && hasBScr)
                    { sx1 = bsx; sy1 = bsy; }

                    DrawLinePx(sh, sx0, sy0, sx1, sy1, widthPx, r, g, b, alpha);
                }
            }
        }

        static Vec2f Perp(Vec2f v)
        {
            float len = (float)Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (len < 1e-4f) return new Vec2f(0, 0);
            return new Vec2f(-v.Y / len, v.X / len);
        }

        public void Dispose()
        {
            if (quad != null)
            {
                capi.Render.DeleteMesh(quad);
                quad = null;
            }
            circleTex?.Dispose();
        }

        public bool TryPickPortAtScreen(float screenX, float screenY, out PortMarker picked, float extraRadiusPx = 6f)
        {
            picked = null;
            if (portMarkers == null || portMarkers.Count == 0 || focusedNode == null) return false;

            if (!focusBlend.TryGetValue(Key(focusedNode), out float fblend)) return false;
            float t = Smoothstep(fblend);
            if (t <= 0.01f) return false;

            float bestD2 = float.MaxValue;

            for (int i = 0; i < portMarkers.Count; i++)
            {
                var m = portMarkers[i];
                if (m.ConeAlpha <= 0.01f) continue;

                // Apply render offset
                Vec3f offset = m.RenderOffset;
                double ox = offset?.X ?? 0;
                double oy = offset?.Y ?? 0;
                double oz = offset?.Z ?? 0;

                Vec3d world = new Vec3d(m.BlockPos.X + 0.5 + ox, m.BlockPos.Y + 0.5 + oy, m.BlockPos.Z + 0.5 + oz);
                Vec3d scr = MatrixToolsd.Project(
                    world,
                    capi.Render.PerspectiveProjectionMat,
                    capi.Render.PerspectiveViewMat,
                    capi.Render.FrameWidth,
                    capi.Render.FrameHeight
                );
                if (scr.Z < 0) continue;

                float ax = (float)scr.X;
                float ay = capi.Render.FrameHeight - (float)scr.Y;

                float px = ax + (m.DxPx * t);
                float py = ay + (m.DyPx * t);

                float size = GameMath.Clamp(PortBaseSizePx, PortMinSizePx, PortMaxSizePx);
                float radius = (size * 0.5f) + extraRadiusPx;

                float ddx = px - screenX;
                float ddy = py - screenY;
                float d2 = ddx * ddx + ddy * ddy;

                if (d2 <= radius * radius && d2 < bestD2)
                {
                    bestD2 = d2;
                    picked = m;
                }
            }

            return picked != null;
        }

        bool TryGetWireEndpointScreen(BlockPos pos, string portId, Vec3f renderOffset, out float sx, out float sy)
        {
            sx = sy = 0;

            // If this is a port and we have port data, use the full port position logic with interpolation
            if (!string.IsNullOrEmpty(portId))
            {
                if (TryGetPortScreenPos(pos, portId, renderOffset, out sx, out sy))
                    return true;

                // Even if TryGetPortScreenPos failed, if we're in a focused state, try to calculate port offset manually
                string pk = PortKey(pos, portId);
                if (focusedNode != null && pos.Equals(focusedNode) && focusBlend.TryGetValue(Key(focusedNode), out float fblend))
                {
                    float t = Smoothstep(fblend);
                    if (t > 0.001f)
                    {
                        // Find the port marker for this port to get its offset
                        PortMarker portMarker = null;
                        if (portMarkers != null)
                        {
                            for (int i = 0; i < portMarkers.Count; i++)
                            {
                                if (portMarkers[i].BlockPos.Equals(pos) && portMarkers[i].PortId == portId)
                                {
                                    portMarker = portMarkers[i];
                                    break;
                                }
                            }
                        }

                        if (portMarker != null)
                        {
                            // Project the center with render offset
                            if (TryProjectCenter(pos, renderOffset, out float cx, out float cy))
                            {
                                // Apply port offset interpolation
                                float dx = portMarker.DxPx * t;
                                float dy = portMarker.DyPx * t;
                                sx = cx + dx;
                                sy = cy + dy;
                                return true;
                            }
                        }
                    }
                }
            }

            string k = Key(pos);
            if (centerByPos.TryGetValue(k, out var c))
            {
                sx = c.x; sy = c.y;
                return true;
            }

            return TryProjectCenter(pos, renderOffset, out sx, out sy);
        }

        static float Smoothstep(float x) => x * x * (3f - 2f * x);
    }
}
