using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Server;

#nullable disable

namespace circuits
{
    public sealed class PvpSafezoneConfig
    {
        public bool Enabled { get; set; } = true;

        // false safezone disables PvP inside
        // true safezone enables PvP inside
        public bool PvpInside { get; set; } = false;

        // "box" or "radius"
        public string Mode { get; set; } = "box";

        public double MinX { get; set; } = -100;
        public double MinY { get; set; } = -999999;
        public double MinZ { get; set; } = -100;

        public double MaxX { get; set; } = 100;
        public double MaxY { get; set; } = 999999;
        public double MaxZ { get; set; } = 100;

        public double CenterX { get; set; } = 0;
        public double CenterZ { get; set; } = 0;
        public double Radius { get; set; } = 100;

        public int CheckIntervalMs { get; set; } = 500;

        public bool AnnounceChanges { get; set; } = true;

        public string EnterSafezoneMessage { get; set; } = "You entered the safezone. PvP is now disabled.";
        public string LeaveSafezoneMessage { get; set; } = "You left the safezone. PvP is now enabled.";
        public string EnterPvpzoneMessage { get; set; } = "You entered the PvP zone. PvP is now enabled.";
        public string LeavePvpzoneMessage { get; set; } = "You left the PvP zone. PvP is now disabled.";
    }

    public sealed class PvpSafezoneSystem : ModSystem
    {
        private const string ConfigName = "pvpsafezone.json";

        private ICoreServerAPI sapi;
        private PvpSafezoneConfig config;

        private long tickListenerId;
        private readonly Dictionary<string, bool> lastAppliedPvpByUid = new();

        private object rpciSystem;
        private Type rpciCharacterInfoType;
        private MethodInfo rpciSetCharacterInfoMethod;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            LoadConfig();
            RegisterCommands();

            int interval = Math.Max(100, config.CheckIntervalMs);
            tickListenerId = sapi.Event.RegisterGameTickListener(OnServerTick, interval);
        }

        private void LoadConfig()
        {
            config = sapi.LoadModConfig<PvpSafezoneConfig>(ConfigName);

            if (config == null)
            {
                config = new PvpSafezoneConfig();
                SaveConfig();
            }

            NormalizeConfig();
        }

        private void SaveConfig()
        {
            sapi.StoreModConfig(config, ConfigName);
        }

        private void NormalizeConfig()
        {
            config.Mode = string.IsNullOrWhiteSpace(config.Mode)
                ? "box"
                : config.Mode.Trim().ToLowerInvariant();

            if (config.Mode != "box" && config.Mode != "radius")
            {
                config.Mode = "box";
            }

            if (config.MinX > config.MaxX)
            {
                double minX = config.MinX;
                double maxX = config.MaxX;
                Swap(ref minX, ref maxX);
                config.MinX = minX;
                config.MaxX = maxX;
            }

            if (config.MinY > config.MaxY)
            {
                double minY = config.MinY;
                double maxY = config.MaxY;
                Swap(ref minY, ref maxY);
                config.MinY = minY;
                config.MaxY = maxY;
            }

            if (config.MinZ > config.MaxZ)
            {
                double minZ = config.MinZ;
                double maxZ = config.MaxZ;
                Swap(ref minZ, ref maxZ);
                config.MinZ = minZ;
                config.MaxZ = maxZ;
            }

            config.Radius = Math.Max(1, config.Radius);
            config.CheckIntervalMs = Math.Max(100, config.CheckIntervalMs);
        }

        private static void Swap(ref double a, ref double b)
        {
            double tmp = a;
            a = b;
            b = tmp;
        }

        private void RegisterCommands()
        {
            var parsers = sapi.ChatCommands.Parsers;

            sapi.ChatCommands.GetOrCreate("pvpsafezone")
                .WithDesc("Configure RPCharacterInfo PvP safezone automation")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(OnSafezoneStatus)

                    .BeginSubCommand("status")
                        .WithDesc("Show current PvP safezone config")
                        .HandleWith(OnSafezoneStatus)
                    .EndSubCommand()

                    .BeginSubCommands("enable", "on")
                        .WithDesc("Enable PvP safezone automation")
                        .HandleWith(OnSafezoneEnable)
                    .EndSubCommand()

                    .BeginSubCommands("disable", "off")
                        .WithDesc("Disable PvP safezone automation")
                        .HandleWith(OnSafezoneDisable)
                    .EndSubCommand()

                    .BeginSubCommand("reload")
                        .WithDesc("Reload PvP safezone config from disk")
                        .HandleWith(OnSafezoneReload)
                    .EndSubCommand()

                    .BeginSubCommand("save")
                        .WithDesc("Save PvP safezone config")
                        .HandleWith(OnSafezoneSave)
                    .EndSubCommand()

                    .BeginSubCommand("inside")
                        .WithDesc("Set whether PvP is enabled inside the configured zone")
                        .WithArgs(parsers.WordRange("on|off", "on", "off"))
                        .HandleWith(OnSafezoneInside)
                    .EndSubCommand()

                    .BeginSubCommand("mode")
                        .WithDesc("Set zone mode")
                        .WithArgs(parsers.WordRange("box|radius", "box", "radius"))
                        .HandleWith(OnSafezoneMode)
                    .EndSubCommand()

                    .BeginSubCommand("box")
                        .WithDesc("Set box zone bounds")
                        .WithArgs(
                            parsers.Double("minX"),
                            parsers.Double("minY"),
                            parsers.Double("minZ"),
                            parsers.Double("maxX"),
                            parsers.Double("maxY"),
                            parsers.Double("maxZ")
                        )
                        .HandleWith(OnSafezoneBox)
                    .EndSubCommand()

                    .BeginSubCommand("radius")
                        .WithDesc("Set radius zone")
                        .WithArgs(
                            parsers.Double("centerX"),
                            parsers.Double("centerZ"),
                            parsers.Double("radius"),
                            parsers.OptionalDouble("minY", double.NaN),
                            parsers.OptionalDouble("maxY", double.NaN)
                        )
                        .HandleWith(OnSafezoneRadius)
                    .EndSubCommand()

                    .BeginSubCommand("sethere")
                        .WithDesc("Set radius zone center to your current position")
                        .WithArgs(parsers.OptionalDouble("radius", -1))
                        .HandleWith(OnSafezoneSetHere)
                    .EndSubCommand()

                    .BeginSubCommand("interval")
                        .WithDesc("Set check interval in milliseconds")
                        .WithArgs(parsers.IntRange("milliseconds", 100, 600000))
                        .HandleWith(OnSafezoneInterval)
                    .EndSubCommand()

                    .BeginSubCommand("announce")
                        .WithDesc("Enable or disable safezone transition messages")
                        .WithArgs(parsers.WordRange("on|off", "on", "off"))
                        .HandleWith(OnSafezoneAnnounce)
                    .EndSubCommand()

                .Validate();
        }

        private TextCommandResult OnSafezoneStatus(TextCommandCallingArgs args)
        {
            return TextCommandResult.Success(GetStatusText());
        }

        private TextCommandResult OnSafezoneEnable(TextCommandCallingArgs args)
        {
            config.Enabled = true;
            SaveAndReapply();
            return TextCommandResult.Success("PvP safezone automation enabled.");
        }

        private TextCommandResult OnSafezoneDisable(TextCommandCallingArgs args)
        {
            config.Enabled = false;
            SaveAndReapply(clearAppliedState: true);
            return TextCommandResult.Success("PvP safezone automation disabled.");
        }

        private TextCommandResult OnSafezoneReload(TextCommandCallingArgs args)
        {
            LoadConfig();
            SaveAndReapply();
            return TextCommandResult.Success("PvP safezone config reloaded.");
        }

        private TextCommandResult OnSafezoneSave(TextCommandCallingArgs args)
        {
            SaveConfig();
            return TextCommandResult.Success("PvP safezone config saved.");
        }

        private TextCommandResult OnSafezoneInside(TextCommandCallingArgs args)
        {
            string value = (string)args[0];

            config.PvpInside = value == "on";
            SaveAndReapply();

            return TextCommandResult.Success(
                config.PvpInside
                    ? "Zone is now a PvP zone: PvP ON inside, OFF outside."
                    : "Zone is now a safezone: PvP OFF inside, ON outside."
            );
        }

        private TextCommandResult OnSafezoneMode(TextCommandCallingArgs args)
        {
            config.Mode = (string)args[0];
            SaveAndReapply();

            return TextCommandResult.Success($"PvP safezone mode set to {config.Mode}.");
        }

        private TextCommandResult OnSafezoneBox(TextCommandCallingArgs args)
        {
            config.Mode = "box";

            config.MinX = (double)args[0];
            config.MinY = (double)args[1];
            config.MinZ = (double)args[2];

            config.MaxX = (double)args[3];
            config.MaxY = (double)args[4];
            config.MaxZ = (double)args[5];

            NormalizeConfig();
            SaveAndReapply();

            return TextCommandResult.Success("PvP safezone box updated.");
        }

        private TextCommandResult OnSafezoneRadius(TextCommandCallingArgs args)
        {
            config.Mode = "radius";

            config.CenterX = (double)args[0];
            config.CenterZ = (double)args[1];
            config.Radius = (double)args[2];

            double minY = (double)args[3];
            double maxY = (double)args[4];

            if (!double.IsNaN(minY)) config.MinY = minY;
            if (!double.IsNaN(maxY)) config.MaxY = maxY;

            NormalizeConfig();
            SaveAndReapply();

            return TextCommandResult.Success("PvP safezone radius updated.");
        }

        private TextCommandResult OnSafezoneSetHere(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;

            if (player?.Entity == null)
            {
                return TextCommandResult.Error("Must be run by an in-game admin player.");
            }

            double radius = (double)args[0];

            config.Mode = "radius";
            config.CenterX = player.Entity.Pos.X;
            config.CenterZ = player.Entity.Pos.Z;

            if (radius > 0)
            {
                config.Radius = radius;
            }

            NormalizeConfig();
            SaveAndReapply();

            return TextCommandResult.Success(
                $"PvP safezone radius centered here: X={config.CenterX:0.##}, Z={config.CenterZ:0.##}, R={config.Radius:0.##}"
            );
        }

        private TextCommandResult OnSafezoneInterval(TextCommandCallingArgs args)
        {
            config.CheckIntervalMs = (int)args[0];
            SaveConfig();

            if (tickListenerId != 0)
            {
                sapi.Event.UnregisterGameTickListener(tickListenerId);
            }

            tickListenerId = sapi.Event.RegisterGameTickListener(OnServerTick, config.CheckIntervalMs);

            return TextCommandResult.Success($"PvP safezone check interval set to {config.CheckIntervalMs}ms.");
        }

        private TextCommandResult OnSafezoneAnnounce(TextCommandCallingArgs args)
        {
            string value = (string)args[0];

            config.AnnounceChanges = value == "on";
            SaveConfig();

            return TextCommandResult.Success(
                config.AnnounceChanges
                    ? "Safezone messages enabled."
                    : "Safezone messages disabled."
            );
        }


        private void SaveAndReapply(bool clearAppliedState = false)
        {
            NormalizeConfig();
            SaveConfig();

            if (clearAppliedState)
            {
                lastAppliedPvpByUid.Clear();
                return;
            }

            // Force re-evaluation under the new config.
            lastAppliedPvpByUid.Clear();
            OnServerTick(0);
        }

        private string GetStatusText()
        {
            string behavior = config.PvpInside
                ? "PvP ON inside, OFF outside"
                : "PvP OFF inside, ON outside";

            string zone = config.Mode == "radius"
                ? $"radius center=({config.CenterX:0.##}, {config.CenterZ:0.##}) r={config.Radius:0.##} y={config.MinY:0.##}..{config.MaxY:0.##}"
                : $"box min=({config.MinX:0.##}, {config.MinY:0.##}, {config.MinZ:0.##}) max=({config.MaxX:0.##}, {config.MaxY:0.##}, {config.MaxZ:0.##})";

            return
                $"PvP safezone: {(config.Enabled ? "enabled" : "disabled")}\n" +
                $"Mode: {config.Mode}\n" +
                $"Zone: {zone}\n" +
                $"Behavior: {behavior}\n" +
                $"Interval: {config.CheckIntervalMs}ms\n" +
                $"Announce: {config.AnnounceChanges}";
        }

        private static string GetHelpText()
        {
            return
                "/pvpsafezone status\n" +
                "/pvpsafezone enable|disable\n" +
                "/pvpsafezone mode box|radius\n" +
                "/pvpsafezone inside on|off\n" +
                "/pvpsafezone box <minX> <minY> <minZ> <maxX> <maxY> <maxZ>\n" +
                "/pvpsafezone radius <centerX> <centerZ> <radius> [minY] [maxY]\n" +
                "/pvpsafezone sethere [radius]\n" +
                "/pvpsafezone interval <milliseconds>\n" +
                "/pvpsafezone announce on|off\n" +
                "/pvpsafezone reload\n" +
                "/pvpsafezone save";
        }

        private void OnServerTick(float dt)
        {
            if (!config.Enabled) return;

            foreach (IPlayer rawPlayer in sapi.World.AllOnlinePlayers)
            {
                if (rawPlayer is not IServerPlayer player) continue;
                if (player.Entity == null) continue;

                bool insideZone = IsInsideZone(player);

                bool desiredPvp = config.PvpInside
                    ? insideZone
                    : !insideZone;

                if (
                    lastAppliedPvpByUid.TryGetValue(player.PlayerUID, out bool lastApplied) &&
                    lastApplied == desiredPvp
                )
                {
                    continue;
                }

                if (SetRpciPvp(player, desiredPvp))
                {
                    lastAppliedPvpByUid[player.PlayerUID] = desiredPvp;

                    if (config.AnnounceChanges)
                    {
                        SendTransitionMessage(player, insideZone, desiredPvp);
                    }
                }
                else
                {
                    sapi.Logger.Warning(
                        "[Circuits] Failed to apply RPCharacterInfo PvP state for {0}",
                        player.PlayerName
                    );
                }
            }
        }

        private bool IsInsideZone(IServerPlayer player)
        {
            var pos = player.Entity.Pos;

            if (pos.Y < config.MinY || pos.Y > config.MaxY)
            {
                return false;
            }

            if (config.Mode == "radius")
            {
                double dx = pos.X - config.CenterX;
                double dz = pos.Z - config.CenterZ;
                return dx * dx + dz * dz <= config.Radius * config.Radius;
            }

            return
                pos.X >= config.MinX && pos.X <= config.MaxX &&
                pos.Z >= config.MinZ && pos.Z <= config.MaxZ;
        }

        private void SendTransitionMessage(IServerPlayer player, bool insideZone, bool desiredPvp)
        {
            string message;

            if (config.PvpInside)
            {
                message = insideZone
                    ? config.EnterPvpzoneMessage
                    : config.LeavePvpzoneMessage;
            }
            else
            {
                message = insideZone
                    ? config.EnterSafezoneMessage
                    : config.LeaveSafezoneMessage;
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                player.SendMessage(
                    Vintagestory.API.Config.GlobalConstants.GeneralChatGroup,
                    message,
                    EnumChatType.Notification
                );
            }
        }

        private bool rpciMissingWarned;

        private bool SetRpciPvp(IServerPlayer player, bool enabled)
        {
            // Silent reflection path. No compile-time dependency.
            if (TrySetRpciPvpDirect(player, enabled))
            {
                return true;
            }

            // Optional command fallback. Still no modinfo dependency.
            // Only try it if the /rpci command exists.
            if (sapi.ChatCommands.Get("rpci") != null)
            {
                string state = enabled ? "on" : "off";
                sapi.InjectConsole($"/rpci admin forcepvp {EscapePlayerName(player.PlayerName)} {state}");
                return true;
            }

            if (!rpciMissingWarned)
            {
                rpciMissingWarned = true;
                sapi.Logger.Warning("[Ceremony] RPCharacterInfo not found. PvP safezone automation is inactive.");
            }

            return false;
        }

        private static string EscapePlayerName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "\"\"";
            return "\"" + name.Replace("\"", "\\\"") + "\"";
        }

        private bool TryResolveRpci()
        {
            if (
                rpciSystem != null &&
                rpciCharacterInfoType != null &&
                rpciSetCharacterInfoMethod != null
            )
            {
                return true;
            }

            foreach (ModSystem system in sapi.ModLoader.Systems)
            {
                Type type = system.GetType();

                if (type.FullName == "RPCharacterInfo.Core.RPCharacterInfoModSystem")
                {
                    rpciSystem = system;
                    break;
                }
            }

            if (rpciSystem == null) return false;

            Type rpciType = rpciSystem.GetType();
            Assembly rpciAssembly = rpciType.Assembly;

            rpciCharacterInfoType = rpciAssembly.GetType("RPCharacterInfo.Core.CharacterInfo");
            if (rpciCharacterInfoType == null) return false;

            rpciSetCharacterInfoMethod = rpciType.GetMethod(
                "SetCharacterInfo",
                BindingFlags.Instance | BindingFlags.NonPublic
            );

            return rpciSetCharacterInfoMethod != null;
        }

        private bool TrySetRpciPvpDirect(IServerPlayer player, bool enabled)
        {
            try
            {
                if (!TryResolveRpci()) return false;

                object characterInfo = Activator.CreateInstance(rpciCharacterInfoType);

                PropertyInfo pvpProp = rpciCharacterInfoType.GetProperty("PvP");
                PropertyInfo pvpLastEnabledProp = rpciCharacterInfoType.GetProperty("PvPLastEnabled");

                if (pvpProp == null) return false;

                pvpProp.SetValue(characterInfo, enabled);

                if (pvpLastEnabledProp != null)
                {
                    pvpLastEnabledProp.SetValue(
                        characterInfo,
                        enabled ? DateTime.UtcNow : default(DateTime)
                    );
                }

                rpciSetCharacterInfoMethod.Invoke(rpciSystem, new object[]
                {
                    sapi,
                    player,
                    characterInfo,
                    true,
                    true
                });

                return true;
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[Circuits] RPCharacterInfo reflection PvP update failed: {0}", e);
                return false;
            }
        }

        private static bool TryParseBoolWord(string value, out bool result)
        {
            result = false;

            switch (value)
            {
                case "on":
                case "true":
                case "yes":
                case "1":
                case "enable":
                case "enabled":
                    result = true;
                    return true;

                case "off":
                case "false":
                case "no":
                case "0":
                case "disable":
                case "disabled":
                    result = false;
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryPopDouble(TextCommandCallingArgs args, out double value)
        {
            string raw = args.RawArgs.PopWord();
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryPopInt(TextCommandCallingArgs args, out int value)
        {
            string raw = args.RawArgs.PopWord();
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }
}