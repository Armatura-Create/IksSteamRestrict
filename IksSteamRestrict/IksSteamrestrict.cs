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

    private PlayerBypassConfig.BypassConfig? _bypassConfig;
    public SRConfig Config { get; set; } = new();
    public required HttpClient Client;
    private DBService _dbService;

    private bool _gBSteamApiActivated;
    private readonly Timer?[] _gHTimer = new Timer?[65];
    private readonly int[] _gIWarnTime = new int[65];

    public override void Load(bool hotReload)
    {
        if (!hotReload) return;
        _gBSteamApiActivated = true;

        foreach (var player in Utilities.GetPlayers().Where(p =>
                     p is { Connected: PlayerConnectedState.PlayerConnected, IsHLTV: false, IsBot: false } && 
                     p.SteamID.ToString().Length == 17))
        {
            OnPlayerConnectFull(player);
        }
    }

    public override void Ready()
    {
        var bypassConfigFilePath = "bypass_config.json";
        var bypassConfigService = new PlayerBypassConfig.BypassConfigService(Path.Combine(ModuleDirectory, bypassConfigFilePath));
        _bypassConfig = bypassConfigService.LoadConfig();

        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(() => { _gBSteamApiActivated = true; });
        RegisterListener<Listeners.OnClientConnect>((slot, _, _) => { _gHTimer[slot]?.Kill(); });
        RegisterListener<Listeners.OnClientDisconnect>(slot => { _gHTimer[slot]?.Kill(); });
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull, HookMode.Post);

        _dbService = new DBService(this, $"Server={Api.Config.Host};Database={Api.Config.Database};User={Api.Config.User};Password={Api.Config.Password};");

        Config = Api.Config.ReadOrCreate(AdminUtils.CoreInstance.ModuleDirectory + "/configs/module_steam_restrict.json", new SRConfig());
        Client = new HttpClient();
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
        {
            OnPlayerConnectFull(player);
        }
        return HookResult.Continue;
    }

    private void OnPlayerConnectFull(CCSPlayerController player)
    {
        if (string.IsNullOrEmpty(Config.SteamWebAPI) || player.IsBot || player.IsHLTV)
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

        if (!_gBSteamApiActivated) return;

        var authorizedSteamID = player.AuthorizedSteamID.SteamId64;
        Server.NextWorldUpdate(() => CheckUserViolations(authorizedSteamID));
    }

    private void CheckUserViolations(ulong authorizedSteamID)
    {
        var userInfo = new SteamUserInfo();
        var steamService = new SteamService(this, userInfo);

        Task.Run(async () =>
        {
            await steamService.FetchSteamUserInfo(authorizedSteamID.ToString());
            userInfo = steamService.UserInfo;

            Server.NextWorldUpdate(() =>
            {
                var player = Utilities.GetPlayerFromSteamId(authorizedSteamID);
                if (player?.IsValid != true) return;

                if (Config.Debug)
                {
                    LogPlayerInfo(player, userInfo);
                }

                Task.Run(async () =>
                {
                    var result = await IsRestrictionViolatedAsync(player, userInfo);
                    Server.NextWorldUpdate(() => HandleViolation(player, result, userInfo));
                });
            });
        });
    }

    private void LogPlayerInfo(CCSPlayerController player, SteamUserInfo userInfo)
    {
        Logger.LogInformation($"{player.PlayerName} info:");
        Logger.LogInformation($"CS2Playtime: {userInfo.CS2Playtime}");
        Logger.LogInformation($"SteamLevel: {userInfo.SteamLevel}");
        Logger.LogInformation($"Account Creation Date: {userInfo.SteamAccountAge:dd-MM-yyyy} ({(int)(Now - userInfo.SteamAccountAge).TotalDays} days ago)");
        Logger.LogInformation($"HasPrivateProfile: {userInfo.IsPrivate}");
        Logger.LogInformation($"IsGameBanned: {userInfo.IsGameBanned}");
    }

    private void HandleViolation(CCSPlayerController player, TypeViolated result, SteamUserInfo userInfo)
    {
        if (result == TypeViolated.APPROVED) return;

        if (Config.BanRestrict)
        {
            BanPlayer(player, result, userInfo);
        }
        else
        {
            Logger.LogInformation($"Kicking {player.PlayerName} for {GetReasonPrivate(result)}");
            Api.Kick(Api.ConsoleAdmin, player, GetReasonPrivate(result));
        }
    }

    private void BanPlayer(CCSPlayerController player, TypeViolated result, SteamUserInfo userInfo)
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
                    Server.NextFrame(() => player.Print(GetReasonPrivate(result)));
                }
                else
                {
                    var playerBan = new PlayerBan(player.SteamID.ToString(), player.IpAddress, player.PlayerName, GetReason(result), GetDuration(result, userInfo))
                    {
                        AdminId = Api.ConsoleAdmin.Id,
                        CreatedAt = AdminUtils.CurrentTimestamp(),
                        UpdatedAt = AdminUtils.CurrentTimestamp()
                    };

                    Logger.LogInformation($"Banning {player.PlayerName} for {GetReasonPrivate(result)}");
                    Api.AddBan(playerBan, false);

                    _gHTimer[playerSlot]?.Kill();
                    _gHTimer[playerSlot] = null;
                }
            }
            else
            {
                _gHTimer[playerSlot]?.Kill();
                _gHTimer[playerSlot] = null;
            }
        }, TimerFlags.REPEAT);
    }

    private async Task<TypeViolated> IsRestrictionViolatedAsync(CCSPlayerController player, SteamUserInfo userInfo)
    {
        var steamId64 = player.AuthorizedSteamID?.SteamId64 ?? 0;

        if ((await Api.GetAdminsBySteamId(steamId64.ToString())).Count > 0) return TypeViolated.APPROVED;

        if (await _dbService.IsPlayerApprovedAsync(steamId64)) return TypeViolated.APPROVED;

        var bypassConfig = _bypassConfig ?? new PlayerBypassConfig.BypassConfig();
        if (bypassConfig.GetPlayerBypass(steamId64)) return TypeViolated.APPROVED;

        if (Config.MinimumHour != -1 && userInfo.CS2Playtime < Config.MinimumHour) return TypeViolated.MIN_HOURS;
        if (Config.MinimumLevel != -1 && userInfo.SteamLevel < Config.MinimumLevel) return TypeViolated.STEAM_LEVEL;
        if (Config.MinimumSteamAccountAgeInDays != -1 && (Now - userInfo.SteamAccountAge).TotalDays < Config.MinimumSteamAccountAgeInDays) return TypeViolated.MIN_ACCOUNT_AGE;
        if (Config.BlockPrivateProfile && (userInfo.IsPrivate || userInfo.IsGameDetailsPrivate)) return TypeViolated.PRIVATE_PROFILE;
        if (Config.BlockTradeBanned && userInfo.IsTradeBanned) return TypeViolated.TRADE_BANNED;
        if (Config.BlockGameBanned && userInfo.IsGameBanned) return TypeViolated.GAME_BANNED;
        if (Config.BlockVACBanned && userInfo.IsVACBanned) return TypeViolated.VAC_BANNED;

        await _dbService.AddPlayerApprovedAsync(steamId64, player.PlayerName);
        return TypeViolated.APPROVED;
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
        return Localizer["Reason." + type, value == 0 ? "" : value.ToString()];
    }

    private string GetReason(TypeViolated type) => $"{type} [SR]";

    private int GetDuration(TypeViolated type, SteamUserInfo userInfo)
    {
        return type switch
        {
            TypeViolated.STEAM_LEVEL => Config.DaysBanByLevel * 24 * 60 * 60,
            TypeViolated.MIN_HOURS => (Config.MinimumHour - userInfo.CS2Playtime) * 60 * 60,
            TypeViolated.MIN_ACCOUNT_AGE => (Config.MinimumSteamAccountAgeInDays - (int)(Now - userInfo.SteamAccountAge).TotalDays) * 24 * 60 * 60,
            _ => 0
        };
    }

    private enum TypeViolated
    {
        APPROVED,
        STEAM_LEVEL,
        MIN_HOURS,
        MIN_ACCOUNT_AGE,
        PRIVATE_PROFILE,
        TRADE_BANNED,
        GAME_BANNED,
        VAC_BANNED
    }
}