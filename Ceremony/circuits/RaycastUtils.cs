using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
#nullable disable

namespace Ceremony.circuits
{
    public static class RaycastUtils
    {
        public static bool TryRaycastHitPos(ICoreClientAPI capi, float maxDist, out Vec3d hitPos)
        {
            hitPos = null;

            var plrEnt = capi.World.Player?.Entity;
            if (plrEnt == null) return false;

            Vec3d from = plrEnt.CameraPos;

            float yaw = capi.World.Player.CameraYaw;
            float pitch = capi.World.Player.CameraPitch;

            Vec3d dir = new Vec3d(
                GameMath.Sin(yaw) * GameMath.Cos(pitch),
                GameMath.Sin(pitch),
                GameMath.Cos(yaw) * GameMath.Cos(pitch)
            ).Normalize();

            Vec3d to = from.AddCopy(dir.Mul(maxDist));

            BlockSelection bsel = null;
            EntitySelection esel = null;
            capi.World.RayTraceForSelection(from, to, ref bsel, ref esel);

            if (bsel?.Position == null) return false;

            var p = bsel.Position;
            var hp = bsel.HitPosition;
            hitPos = new Vec3d(p.X + hp.X, p.Y + hp.Y, p.Z + hp.Z);
            return true;
        }


        public static BlockPos GetLookedAtBlockPos(ICoreClientAPI capi)
        {
            var plr = capi.World.Player;
            if (plr?.CurrentBlockSelection?.Position == null) return null;

            var pos = plr.CurrentBlockSelection.Position.Copy();
            pos.dimension = plr.Entity.Pos.Dimension;
            return pos;
        }

        public static BlockPos RaycastBlockPos(ICoreClientAPI capi, float maxDist)
        {
            var plrEnt = capi.World.Player?.Entity;
            if (plrEnt == null) return null;

            Vec3d from = plrEnt.CameraPos;

            float yaw = capi.World.Player.CameraYaw;
            float pitch = capi.World.Player.CameraPitch;

            Vec3d dir = new Vec3d(
                GameMath.Sin(yaw) * GameMath.Cos(pitch),
                GameMath.Sin(pitch),
                GameMath.Cos(yaw) * GameMath.Cos(pitch)
            ).Normalize();

            Vec3d to = from.AddCopy(dir.Mul(maxDist));

            BlockSelection bsel = null;
            EntitySelection esel = null;

            capi.World.RayTraceForSelection(from, to, ref bsel, ref esel);

            if (bsel?.Position == null) return null;

            var pos = bsel.Position.Copy();
            pos.dimension = plrEnt.Pos.Dimension;
            return pos;
        }

        public static bool TryGetLookHit(ICoreClientAPI capi, out Vec3d hitPos)
        {
            hitPos = null;

            var plr = capi.World.Player;
            var bsel = plr?.CurrentBlockSelection;
            if (bsel?.Position == null) return false;

            var p = bsel.Position;
            var hp = bsel.HitPosition; // 0..1 inside block

            hitPos = new Vec3d(
                p.X + hp.X,
                p.Y + hp.Y,
                p.Z + hp.Z
            );

            return true;
        }

        public static bool TryRaycastFromScreen(ICoreClientAPI capi, double mouseX, double mouseY, float maxDist,
            out BlockSelection bsel, out EntitySelection esel)
        {
            bsel = null;
            esel = null;

            var plrEnt = capi.World.Player?.Entity;
            if (plrEnt == null) return false;

            // Build direction from screen pixel
            GetRayDirFromScreen(capi, mouseX, mouseY, out Vec3d dir);

            // IMPORTANT: start at camera pos
            Vec3d from = plrEnt.CameraPos;
            Vec3d to = from.AddCopy(dir.Mul(maxDist));

            capi.World.RayTraceForSelection(from, to, ref bsel, ref esel);
            return bsel != null || esel != null;
        }

        // Computes ONLY the ray direction for a given screen pixel
        public static void GetRayDirFromScreen(ICoreClientAPI capi, double x, double y, out Vec3d dir)
        {
            int w = capi.Render.FrameWidth;
            int h = capi.Render.FrameHeight;

            double ndcX = (x / w) * 2.0 - 1.0;
            double ndcY = 1.0 - (y / h) * 2.0;

            // clip-space
            Vec4d nearClip = new Vec4d(ndcX, ndcY, -1.0, 1.0);
            Vec4d farClip = new Vec4d(ndcX, ndcY, 1.0, 1.0);

            // invert PV
            double[] pv = MulMat4(capi.Render.PerspectiveProjectionMat, capi.Render.PerspectiveViewMat);
            double[] invPV = Mat4d.Create();
            Mat4d.Invert(invPV, pv);

            Vec4d nearW4 = Mul(invPV, nearClip);
            Vec4d farW4 = Mul(invPV, farClip);

            Vec3d nearW = new Vec3d(nearW4.X / nearW4.W, nearW4.Y / nearW4.W, nearW4.Z / nearW4.W);
            Vec3d farW = new Vec3d(farW4.X / farW4.W, farW4.Y / farW4.W, farW4.Z / farW4.W);

            dir = farW.SubCopy(nearW).Normalize();
        }

        // column-major 4x4 multiply: r = a * b
        static double[] MulMat4(double[] a, double[] b)
        {
            double[] r = new double[16];
            for (int col = 0; col < 4; col++)
                for (int row = 0; row < 4; row++)
                {
                    r[col * 4 + row] =
                        a[0 * 4 + row] * b[col * 4 + 0] +
                        a[1 * 4 + row] * b[col * 4 + 1] +
                        a[2 * 4 + row] * b[col * 4 + 2] +
                        a[3 * 4 + row] * b[col * 4 + 3];
                }
            return r;
        }

        static Vec4d Mul(double[] m, Vec4d v)
        {
            return new Vec4d(
                m[0] * v.X + m[4] * v.Y + m[8] * v.Z + m[12] * v.W,
                m[1] * v.X + m[5] * v.Y + m[9] * v.Z + m[13] * v.W,
                m[2] * v.X + m[6] * v.Y + m[10] * v.Z + m[14] * v.W,
                m[3] * v.X + m[7] * v.Y + m[11] * v.Z + m[15] * v.W
            );
        }
    }

}
