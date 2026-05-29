using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
#nullable disable

namespace circuits;

public class BEBehaviorCallLightningOnPlayerRange : BlockEntityBehavior
{
    float range = 12f;
    float cooldownSeconds = 20f;
    float checkIntervalSeconds = 0.5f;
    float strikeYOffset = 1f;

    long listenerId;
    double lastStrikeTotalHours = -999999;

    WeatherSystemServer weather;

    public BEBehaviorCallLightningOnPlayerRange(BlockEntity blockentity) : base(blockentity) { }

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        base.Initialize(api, properties);

        if (api.Side != EnumAppSide.Server) return;

        weather = api.ModLoader.GetModSystem<WeatherSystemServer>();
        if (weather == null) return;

        range = properties["range"].AsFloat(range);
        cooldownSeconds = properties["cooldownSeconds"].AsFloat(cooldownSeconds);
        checkIntervalSeconds = properties["checkIntervalSeconds"].AsFloat(checkIntervalSeconds);
        strikeYOffset = properties["strikeYOffset"].AsFloat(strikeYOffset);

        listenerId = Blockentity.RegisterGameTickListener(OnServerTick, (int)(checkIntervalSeconds * 10000));
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        if (listenerId != 0) Blockentity.UnregisterGameTickListener(listenerId);
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        if (listenerId != 0) Blockentity.UnregisterGameTickListener(listenerId);
    }

    void OnServerTick(float dt)
    {
        var sapi = Blockentity.Api as ICoreServerAPI;
        if (sapi == null) return;

        double nowHours = sapi.World.Calendar.TotalHours;
        double cooldownHours = cooldownSeconds / 3600.0;
        if (nowHours - lastStrikeTotalHours < cooldownHours) return;

        Vec3d center = new Vec3d(Blockentity.Pos.X + 0.5, Blockentity.Pos.Y + 0.5, Blockentity.Pos.Z + 0.5);

        var players = sapi.World.GetPlayersAround(center, range, range, p =>
        {
            if (p == null) return false;
            if (p.WorldData?.CurrentGameMode == EnumGameMode.Spectator) return false;

            var ent = p.Entity;
            if (ent == null || !ent.Alive) return false;

            return true;
        });

        if (players == null || players.Length == 0) return;

        Vec3d strikePos = new Vec3d(Blockentity.Pos.X + 0.5, Blockentity.Pos.Y + strikeYOffset, Blockentity.Pos.Z + 0.5);

        if (TryCallLightningStrike(strikePos))
        {
            lastStrikeTotalHours = nowHours;
            Blockentity.MarkDirty();
        }
    }

    bool IsEnabled()
    {
        try
        {
            var block = Blockentity.Api.World.BlockAccessor.GetBlock(Blockentity.Pos);
            if (block == null) return false;
            var state = block.Variant?["state"];

            return state == "on";
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool on)
    {
        if (Api == null || Api.Side != EnumAppSide.Server) return;

        var world = Api.World;
        var ba = world.BlockAccessor;
        var pos = Blockentity.Pos;

        var cur = ba.GetBlock(pos);
        if (cur == null) return;

        var curState = cur.Variant?["state"];
        if (curState == null) return;

        var desired = on ? "on" : "off";
        if (curState == desired) return;

        // Build the sibling variant code explicitly from the current code
        string path = cur.Code.Path; // lightningrod-evil-on/off
        if (path.EndsWith("-on")) path = path.Substring(0, path.Length - 3);
        if (path.EndsWith("-off")) path = path.Substring(0, path.Length - 4);

        var targetCode = new AssetLocation(cur.Code.Domain, $"{path}-{desired}");

        var newBlock = world.GetBlock(targetCode);

        if (newBlock == null) return;

        ba.ExchangeBlock(newBlock.BlockId, pos);
        ba.MarkChunkDecorsModified(pos);
        Blockentity.MarkDirty(true);
    }

    bool TryCallLightningStrike(Vec3d strikePos)
    {
        if (weather == null) return false;
        if (!IsEnabled()) return false;
        weather.SpawnLightningFlash(strikePos);
        return true;
    }

}
