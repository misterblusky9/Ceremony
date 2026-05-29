using System;
using System.Collections.Generic;
using System.Runtime.ConstrainedExecution;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

#nullable disable

namespace cbnormalizer
{
    public class BlockEntityLandmine : BlockEntity
    {
        // Added for Landmine
        float triggerRadius;
        float triggerHeightOffset;
        int triggerRefreshMs;
        float armDelayLeft;
        bool pressed;
        string pressedByPlayerUid;
        float dudChance;

        public float RemainingSeconds = 0;
        bool lit;
        bool armed;
        string ignitedByPlayerUid;
        float blastRadius;
        float injureRadius;
        float trespasserDmgMult;


        EnumBlastType blastType;

        ILoadedSound fuseSound;
        public static SimpleParticleProperties smallSparks;

        public bool CascadeLit { get; set; }

        static BlockEntityLandmine()
        {
            smallSparks = new SimpleParticleProperties(
                1, 1,
                ColorUtil.ToRgba(255, 255, 233, 0),
                new Vec3d(), new Vec3d(),
                new Vec3f(-3f, 5f, -3f),
                new Vec3f(3f, 8f, 3f),
                0.03f,
                1f,
                0.05f, 0.15f,
                EnumParticleModel.Quad
            );
            smallSparks.VertexFlags = 255;
            smallSparks.AddPos.Set(1 / 16f, 0, 1 / 16f);
            smallSparks.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -0.05f);
        }


        public virtual float FuseTimeSeconds
        {
            get { return 4; }
        }


        public virtual EnumBlastType BlastType
        {
            get { return blastType; }
        }

        public virtual float BlastRadius
        {
            get { return blastRadius; }
        }

        public virtual float InjureRadius
        {
            get { return injureRadius; }
        }


        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            if (fuseSound == null && api.Side == EnumAppSide.Client)
            {
                fuseSound = ((IClientWorldAccessor)api.World).LoadSound(new SoundParams()
                {
                    Location = new AssetLocation("sounds/effect/fuse"),
                    ShouldLoop = true,
                    Position = Pos.ToVec3f().Add(0.5f, 0.25f, 0.5f),
                    DisposeOnFinish = false,
                    Volume = 0.1f,
                    Range = 16,
                });
            }

            blastRadius = Block.Attributes["blastRadius"].AsInt(4);
            injureRadius = Block.Attributes["injureRadius"].AsInt(8);
            blastType = (EnumBlastType)Block.Attributes["blastType"].AsInt((int)EnumBlastType.EntityBlast);

            triggerRadius = Block.Attributes["triggerRadius"].AsFloat(1f);
            triggerHeightOffset = Block.Attributes["triggerHeightOffset"].AsFloat(1f);
            triggerRefreshMs = Block.Attributes["triggerRefreshMs"].AsInt(20);
            armDelayLeft = Block.Attributes["armDelaySeconds"].AsFloat(2.0f);
            trespasserDmgMult = Block.Attributes["trespasserDmgMult"].AsFloat(3.0f);
            dudChance = Block.Attributes["dudChance"].AsFloat(0f);

            if (api.Side == EnumAppSide.Server)
            {
                RegisterGameTickListener(OnProximityTick, triggerRefreshMs);
            }

        }

        private void OnProximityTick(float dt)
        {
            if (combusted) return;

            // Arming delay
            if (!armed)
            {
                if (armDelayLeft > 0)
                {
                    armDelayLeft -= dt;

                    if (GetTriggerVariant() != "disarmed")
                    {
                        SetTriggerVariant("disarmed");
                    }
                    return;
                }

                armed = true;
                SetTriggerVariant("armed");
                MarkDirty(true);
            }

            var above = Api.World.BlockAccessor.GetBlock(Pos.UpCopy(1));
            bool ceiling = above != null && above.BlockId != 0 && above.SideSolid[BlockFacing.UP.Index];
            int yBoost = ceiling ? 1 : 0;


            Vec3d center = Pos.ToVec3d().Add(0.5, 0.5 + triggerHeightOffset * 0.5f + yBoost, 0.5);

            var ents = Api.World.GetEntitiesAround(center, triggerRadius + 1.75f, triggerHeightOffset + 0.25f + yBoost);

            string foundPlayerUid = null;
            double minY = Pos.Y - 0.01;
            double maxY = Pos.Y + triggerHeightOffset + 0.51 + yBoost;

            for (int i = 0; i < ents.Length; i++)
            {
                if (ents[i] is not Vintagestory.API.Common.EntityPlayer ep) continue;
                if (!ep.Alive) continue;

                var p = ep.Pos;

                if (p.Y < minY || p.Y > maxY) continue;

                double dx = p.X - (Pos.X + 0.5);
                double dz = p.Z - (Pos.Z + 0.5);
                if ((dx * dx + dz * dz) > (triggerRadius * triggerRadius)) continue;

                foundPlayerUid = ep.PlayerUID;
                break;
            }

            bool playerInRange = foundPlayerUid != null;


            // Press
            if (!pressed && playerInRange)
            {
                pressed = true;
                pressedByPlayerUid = foundPlayerUid;
                SetTriggerVariant("pressed");
                //SendTrespasserMessage(foundPlayerUid, "You hear a sharp *click* beneath your feet... do you feel lucky?");
                MarkDirty(true);
                return;
            }

            // Release -> explode
            if (pressed && !playerInRange)
            {
                ignitedByPlayerUid = pressedByPlayerUid;

                // Dud roll (server-side only)
                if (dudChance > 0f && Api.Side == EnumAppSide.Server)
                {
                    // NextDouble is [0,1)
                    if ((float)Api.World.Rand.NextDouble() < dudChance)
                    {
                        // Defuse / dud behavior
                        pressed = false;
                        //SendTrespasserMessage(pressedByPlayerUid, "Must have been a dud...");
                        pressedByPlayerUid = null;

                        armed = false;
                        armDelayLeft = 2f;

                        SetTriggerVariant("disarmed");

                        Api.World.PlaySoundAt(
                            new AssetLocation("game:sounds/effect/extinguish"), // or another "click/fail" sound
                            Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5,
                            null, true, 16
                        );
                        

                        MarkDirty(true);
                        return;
                    }
                }
                PlayToggleSound();
                if (armed)
                {
                    Combust(0);
                } else
                {
                    SendTrespasserMessage(pressedByPlayerUid, "Must have been a dud...");
                }
                return;
            }

        }
        
        private void SendTrespasserMessage(string playerUid, string msg)
        {
            if (Api.Side != EnumAppSide.Server) return;
            var splr = Api.World.PlayerByUid(playerUid) as IServerPlayer;
            if (splr == null) return;

            splr.SendMessage(
                GlobalConstants.InfoLogChatGroup,
                msg,
                EnumChatType.Notification
            );
        }

        private void PlayToggleSound()
        {
            Api.World.PlaySoundAt(
                new AssetLocation("game:sounds/effect/woodswitch"),
                Pos.X + 0.5, Pos.Y + 1.5, Pos.Z + 0.5,
                null, true, 16
            );
        }

        bool combusted = false;
        public void Combust(float dt)
        {
            if (Api.Side != EnumAppSide.Server)
            {
                combusted = true;
                return;
            }

            if (!HasPermissionToUse())
            {
                Api.World.PlaySoundAt(new AssetLocation("sounds/effect/extinguish"), Pos, -0.5, null, false, 16);
                lit = false;
                MarkDirty(true);
                return;
            }

            combusted = true;
            Api.World.BlockAccessor.SetBlock(0, Pos);
            ((IServerWorldAccessor)Api.World).CreateExplosion(Pos, BlastType, BlastRadius, injureRadius, 1f, ignitedByPlayerUid);
            ApplyTrespasserDamage();
        }

        private void ApplyTrespasserDamage()
        {
            if (string.IsNullOrEmpty(pressedByPlayerUid)) return;
            var plr = Api.World.PlayerByUid(pressedByPlayerUid);
            if (plr?.Entity == null) return;
            if (!plr.Entity.Alive) return;

            var src = new DamageSource()
            {
                Source = EnumDamageSource.Explosion,
                Type = EnumDamageType.PiercingAttack,
                SourcePos = Pos.ToVec3d().Add(0.5, 0.5, 0.5)
            };
            plr.Entity.ReceiveDamage(src, InjureRadius * trespasserDmgMult);
        }

        public bool HasPermissionToUse()
        {
            // Client cannot evaluate landclaims
            if (Api?.Side != EnumAppSide.Server) return true;

            var sapi = Api as ICoreServerAPI;
            var claims = sapi?.WorldManager?.LandClaims;
            if (claims == null) return true;

            // If we don't know who triggered it, don't deny by default
            if (string.IsNullOrEmpty(ignitedByPlayerUid)) return true;

            var player = Api.World.PlayerByUid(ignitedByPlayerUid);
            if (player == null) return true;

            int rad = (int)Math.Ceiling(BlastRadius);
            var exploArea = new Cuboidi(Pos.AddCopy(-rad, -rad, -rad), Pos.AddCopy(rad, rad, rad));

            for (int i = 0; i < claims.Count; i++)
            {
                var claim = claims[i];
                if (!claim.Intersects(exploArea)) continue;

                return claim.TestPlayerAccess(player, EnumBlockAccessFlags.BuildOrBreak) != EnumPlayerAccessResult.Denied;
            }

            return true;
        }

        // Modified for Landmine
        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);

            RemainingSeconds = tree.GetFloat("remainingSeconds", 0);
            bool wasLit = lit;

            lit = tree.GetInt("lit") > 0;
            ignitedByPlayerUid = tree.GetString("ignitedByPlayerUid");

            armed = tree.GetBool("armed");
            pressed = tree.GetBool("pressed");
            pressedByPlayerUid = tree.GetString("pressedByPlayerUid");

        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetBool("armed", armed);
            tree.SetBool("pressed", pressed);
            tree.SetString("pressedByPlayerUid", pressedByPlayerUid);

        }

        private void SetTriggerVariant(string state)
        {
            var cur = Api.World.BlockAccessor.GetBlock(Pos);
            if (cur?.Code == null) return;

            string curTrigger = cur.Variant?["trigger"];
            if (curTrigger == state) return;

            string targetPath = "landmine-" + state;
            var target = Api.World.GetBlock(new AssetLocation(cur.Code.Domain, targetPath));
            if (target == null) return;

            PlayToggleSound();
            Api.World.BlockAccessor.ExchangeBlock(target.Id, Pos);
        }

        private string GetTriggerVariant()
        {
            var cur = Api.World.BlockAccessor.GetBlock(Pos);
            return cur?.Variant?["trigger"] ?? "";
        }

        public override void OnBlockBroken(IPlayer byPlayer = null)
        {
            base.OnBlockBroken(byPlayer);

            if (byPlayer is IServerPlayer splr && splr.WorldData?.CurrentGameMode == EnumGameMode.Creative)
            {
                ignitedByPlayerUid = splr.PlayerUID;
                pressed = false;
                armed = false;
                combusted = true;
                return;
            }

            ignitedByPlayerUid = byPlayer?.PlayerUID ?? ignitedByPlayerUid;
            if (GetTriggerVariant() != "disarmed")
            {
                PlayToggleSound();
                Combust(0);
            }
        }
        
        ~BlockEntityLandmine()
        {
            if (fuseSound != null)
            {
                fuseSound.Dispose();
            }
        }


        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();
            if (fuseSound != null) fuseSound.Stop();
        }
    }
}