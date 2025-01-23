using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Timers;
using IksAdminApi;
using Microsoft.Extensions.Logging;
using static System.DateTime;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace IksSteamRestrict;

[MinimumApiVersion(300)]
public class IksSteamRestrict : AdminModule
{
    public override string ModuleName => "IksSteamRestrict";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Armatura";

    private BypassConfig? _bypassConfig;
    public SRConfig Config { get; set; } = new();
    public required HttpClient Client;

    private bool _gBSteamApiActivated;
    private readonly Timer?[] _gHTimer = new Timer?[65];
    private readonly int[] _gIWarnTime = new int[65];

    public override void Load(bool hotReload)
    {
        if (!hotReload) return;
        _gBSteamApiActivated = true;

        foreach (var player in Utilities.GetPlayers().Where(m =>
                     m is { Connected: PlayerConnectedState.PlayerConnected, IsHLTV: false, IsBot: false } && m.SteamID.ToString().Length == 17))
        {
            OnPlayerConnectFull(player);
        }
    }

    public override void Ready()
    {
        string bypassConfigFilePath = "bypass_config.json";
        var bypassConfigService = new BypassConfigService(Path.Combine(ModuleDirectory, bypassConfigFilePath));
        _bypassConfig = bypassConfigService.LoadConfig();

        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(() => { _gBSteamApiActivated = true; });
        RegisterListener<Listeners.OnClientConnect>((slot, _, _) => { _gHTimer[slot]?.Kill(); });
        RegisterListener<Listeners.OnClientDisconnect>(slot => { _gHTimer[slot]?.Kill(); });
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull, HookMode.Post);

        Config = Api.Config.ReadOrCreate(AdminUtils.CoreInstance.ModuleDirectory + "/configs/module_steam_restrict.json", new SRConfig());
        Client = new HttpClient();
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null)
            return HookResult.Continue;

        OnPlayerConnectFull(player);
        return HookResult.Continue;
    }

    private void OnPlayerConnectFull(CCSPlayerController player)
    {
        if (string.IsNullOrEmpty(Config.SteamWebAPI))
            return;

        if (player.IsBot || player.IsHLTV)
            return;

        if (player.AuthorizedSteamID == null)
        {
            _gHTimer[player.Slot] = AddTimer(1.0f, () =>
            {
                if (player.AuthorizedSteamID == null) return;
                _gHTimer[player.Slot]?.Kill();
                OnPlayerConnectFull(player);
            }, TimerFlags.REPEAT);
            return;
        }

        if (!_gBSteamApiActivated)
            return;

        var authorizedSteamID = player.AuthorizedSteamID.SteamId64;
        Task.Run(async () => { Server.NextWorldUpdate(() => { CheckUserViolations(authorizedSteamID); }); });
    }

    private void CheckUserViolations(ulong authorizedSteamID)
    {
        var userInfo = new SteamUserInfo();

        var steamService = new SteamService(this, userInfo);

        Task.Run(async () =>
        {
            await steamService.FetchSteamUserInfo(authorizedSteamID.ToString());

            userInfo = steamService.UserInfo;

            await Server.NextWorldUpdateAsync(() =>
            {
                var player = Utilities.GetPlayerFromSteamId(authorizedSteamID);

                if (player?.IsValid != true) return;
                if (Config.Debug)
                {
                    Logger.LogInformation($"{player.PlayerName} info:");
                    Logger.LogInformation($"CS2Playtime: {userInfo.CS2Playtime}");
                    Logger.LogInformation($"SteamLevel: {userInfo.SteamLevel}");
                    Logger.LogInformation(
                        (Now - userInfo.SteamAccountAge).TotalSeconds > 30
                            ? $"Steam Account Creation Date: {userInfo.SteamAccountAge:dd-MM-yyyy} ({(int)(Now - userInfo.SteamAccountAge).TotalDays} days ago)"
                            : $"Steam Account Creation Date: N/A");
                    //Logger.LogInformation($"HasPrime: {userInfo.HasPrime}"); Removed due to people bought prime after CS2 cannot be detected sadly (or atleast not yet)
                    Logger.LogInformation($"HasPrivateProfile: {userInfo.IsPrivate}");
                    Logger.LogInformation($"HasPrivateGameDetails: {userInfo.IsGameDetailsPrivate}");
                    Logger.LogInformation($"IsTradeBanned: {userInfo.IsTradeBanned}");
                    Logger.LogInformation($"IsGameBanned: {userInfo.IsGameBanned}");
                }

                var result = IsRestrictionViolated(player, userInfo);
                if (Config.Debug)
                {
                    Logger.LogInformation($"Player {player.PlayerName} violated restriction: {result}");
                }

                if (result == TypeViolated.APPROVED) return;
                
                if (Config.BanRestrict)
                {
                    var playerSlot = player.Slot;
                    _gIWarnTime[playerSlot] = Config.WarningTime;

                    _gHTimer[playerSlot] = AddTimer(1.0f, () =>
                    {
                        if (player?.IsValid == true)
                        {
                            _gIWarnTime[playerSlot]--;

                            if (_gIWarnTime[playerSlot] > 0)
                            {
                                Server.NextFrame(() => { player.Print(GetReasonPrivate(result)); });
                            }

                            if (_gIWarnTime[playerSlot] > 0) return;
                            var playerBan = new PlayerBan(
                                player.SteamID.ToString(),
                                player.IpAddress,
                                player.PlayerName,
                                GetReason(result),
                                GetDuration(result, userInfo)
                            );
                            playerBan.AdminId = Api.ConsoleAdmin.Id;
                            playerBan.CreatedAt = AdminUtils.CurrentTimestamp();
                            playerBan.UpdatedAt = AdminUtils.CurrentTimestamp();
                            if (Config.Debug)
                            {
                                Logger.LogInformation($"Banning {player.PlayerName} for {GetReasonPrivate(result)}");
                            }
                            Api.AddBan(playerBan, false);

                            _gHTimer[playerSlot]?.Kill();
                            _gHTimer[playerSlot] = null;
                        }
                        else
                        {
                            _gHTimer[playerSlot]?.Kill();
                            _gHTimer[playerSlot] = null;
                        }
                    }, TimerFlags.REPEAT);
                }
                else
                {
                    if (Config.Debug)
                    {
                        Logger.LogInformation($"Kicking {player.PlayerName} for {GetReasonPrivate(result)}");
                    }
                    Api.Kick(Api.ConsoleAdmin, player, GetReasonPrivate(result));
                }
            });
        });
    }

    private TypeViolated IsRestrictionViolated(CCSPlayerController player, SteamUserInfo userInfo)
    {
        var steamId64 = player.AuthorizedSteamID?.SteamId64 ?? 0;

        if (Api.GetAdminsBySteamId(steamId64.ToString()).Result.Count > 0)
        {
            return TypeViolated.APPROVED;
        }

        //TODO Добавить команду для rcon что бы можо было добавлять через консоль или через админку
        var bypassConfig = _bypassConfig ?? new BypassConfig();
        var playerBypassConfig = bypassConfig.GetPlayerConfig(steamId64);

        if (!(playerBypassConfig?.BypassMinimumHours ?? false) && Config.MinimumHour != -1 && userInfo.CS2Playtime > -1 &&
            userInfo.CS2Playtime < Config.MinimumHour)
            return TypeViolated.MIN_HOURS;

        if (!(playerBypassConfig?.BypassMinimumLevel ?? false) && Config.MinimumLevel != -1 && userInfo.SteamLevel > -1 &&
            userInfo.SteamLevel < Config.MinimumLevel)
            return TypeViolated.STEAM_LEVEL;

        if (!(playerBypassConfig?.BypassMinimumSteamAccountAge ?? false) && Config.MinimumSteamAccountAgeInDays != -1 &&
            (Now - userInfo.SteamAccountAge).TotalDays < Config.MinimumSteamAccountAgeInDays)
            return TypeViolated.MIN_ACCOUNT_AGE;

        if (Config.BlockPrivateProfile && !(playerBypassConfig?.BypassPrivateProfile ?? false) && (userInfo.IsPrivate || userInfo.IsGameDetailsPrivate))
            return TypeViolated.PRIVATE_PROFILE;

        if (Config.BlockTradeBanned && !(playerBypassConfig?.BypassTradeBanned ?? false) && userInfo.IsTradeBanned)
            return TypeViolated.TRADE_BANNED;

        if (Config.BlockGameBanned && !(playerBypassConfig?.BypassGameBanned ?? false) && userInfo.IsGameBanned)
            return TypeViolated.GAME_BANNED;

        if (Config.BlockVACBanned && !(playerBypassConfig?.BypassVACBanned ?? false) && userInfo.IsVACBanned)
            return TypeViolated.VAC_BANNED;

        return TypeViolated.APPROVED;
    }

    enum TypeViolated
    {
        APPROVED,
        STEAM_LEVEL,
        MIN_HOURS,
        MIN_ACCOUNT_AGE,
        PRIVATE_PROFILE,
        TRADE_BANNED,
        GAME_BANNED,
        VAC_BANNED,
    }

    private string GetReason(TypeViolated type)
    {
        return type + " [SR]";
    }

    private string GetReasonPrivate(TypeViolated type)
    {
        var value = type switch
        {
            TypeViolated.STEAM_LEVEL => Config.MinimumLevel,
            TypeViolated.MIN_HOURS => Config.MinimumHour,
            TypeViolated.MIN_ACCOUNT_AGE => Config.MinimumSteamAccountAgeInDays,
            _ => 0
        };
        return Localizer["Reason." + type, value.ToString() == "0" ? "" : value.ToString()];
    }

    private int GetDuration(TypeViolated type, SteamUserInfo userinfo)
    {
        var result = type switch
        {
            TypeViolated.STEAM_LEVEL => Config.DaysBanByLevel * 24 * 60 * 60,
            TypeViolated.MIN_HOURS => (Config.MinimumHour - userinfo.CS2Playtime) * 60 * 60,
            TypeViolated.MIN_ACCOUNT_AGE => (Config.MinimumSteamAccountAgeInDays - (int)(Now - userinfo.SteamAccountAge).TotalDays) * 24 * 60 * 60,
            TypeViolated.PRIVATE_PROFILE => 60,
            _ => 0
        };

        return result;
    }
}