using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using IksAdminApi;
using Microsoft.Extensions.Logging;

namespace IksSteamRestrict;

[MinimumApiVersion(300)]
public class IksSteamRestrict : AdminModule
{
    public override string ModuleName => "SteamRestrict";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Armatura";

    private BypassConfig? _bypassConfig;
    public SRConfig Config { get; set; } = new();
    public readonly HttpClient Client = new HttpClient();
    
    private bool g_bSteamAPIActivated = false;
    private CounterStrikeSharp.API.Modules.Timers.Timer?[] g_hTimer = new CounterStrikeSharp.API.Modules.Timers.Timer?[65];
    private int[] g_iWarnTime = new int[65];

    public override void Load(bool hotReload)
    {
        if (!hotReload) return;
        g_bSteamAPIActivated = true;

        foreach (var player in Utilities.GetPlayers().Where(m => m is { Connected: PlayerConnectedState.PlayerConnected, IsHLTV: false, IsBot: false } && m.SteamID.ToString().Length == 17))
        {
            OnPlayerConnectFull(player);
        }
    }

    public override void Ready()
    {
        string bypassConfigFilePath = "bypass_config.json";
        var bypassConfigService = new BypassConfigService(Path.Combine(ModuleDirectory, bypassConfigFilePath));
        _bypassConfig = bypassConfigService.LoadConfig();
        Config = Api.Config.ReadOrCreate("configs/steam_restrict", new SRConfig());
        
        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(() => { g_bSteamAPIActivated = true; });
        RegisterListener<Listeners.OnClientConnect>((int slot, string name, string ipAddress) => { g_hTimer[slot]?.Kill(); });
        RegisterListener<Listeners.OnClientDisconnect>((int slot) => { g_hTimer[slot]?.Kill(); });
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull, HookMode.Post);
    }
    
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;
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
            g_hTimer[player.Slot] = AddTimer(1.0f, () =>
            {
                if (player.AuthorizedSteamID != null)
                {
                    g_hTimer[player.Slot]?.Kill();
                    OnPlayerConnectFull(player);
                }
            }, TimerFlags.REPEAT);
            return;
        }

        if (!g_bSteamAPIActivated)
            return;

        ulong authorizedSteamID = player.AuthorizedSteamID.SteamId64;
        nint handle = player.Handle;

        Task.Run(async () =>
        {
            Server.NextWorldUpdate(() =>
            {
                CheckUserViolations(handle, authorizedSteamID);
            });
        });
    }
    
    private void CheckUserViolations(nint handle, ulong authorizedSteamID)
    {
        SteamUserInfo userInfo = new SteamUserInfo();

        SteamService steamService = new SteamService(this, userInfo);

        Task.Run(async () =>
        {
            await steamService.FetchSteamUserInfo(authorizedSteamID.ToString());

            SteamUserInfo? userInfo = steamService.UserInfo;

            Server.NextWorldUpdate(() =>
            {
                CCSPlayerController? player = Utilities.GetPlayerFromSteamId(authorizedSteamID);

                if (player?.IsValid == true && userInfo != null)
                {
                    if (Config.Debug)
                    {
                        Logger.LogInformation($"{player.PlayerName} info:");
                        Logger.LogInformation($"CS2Playtime: {userInfo.CS2Playtime}");
                        Logger.LogInformation($"SteamLevel: {userInfo.SteamLevel}");
                        if ((DateTime.Now - userInfo.SteamAccountAge).TotalSeconds > 30)
                            Logger.LogInformation($"Steam Account Creation Date: {userInfo.SteamAccountAge:dd-MM-yyyy} ({(int)(DateTime.Now - userInfo.SteamAccountAge).TotalDays} days ago)");
                        else
                            Logger.LogInformation($"Steam Account Creation Date: N/A");
                        //Logger.LogInformation($"HasPrime: {userInfo.HasPrime}"); Removed due to people bought prime after CS2 cannot be detected sadly (or atleast not yet)
                        Logger.LogInformation($"HasPrivateProfile: {userInfo.IsPrivate}");
                        Logger.LogInformation($"HasPrivateGameDetails: {userInfo.IsGameDetailsPrivate}");
                        Logger.LogInformation($"IsTradeBanned: {userInfo.IsTradeBanned}");
                        Logger.LogInformation($"IsGameBanned: {userInfo.IsGameBanned}");
                    }

                    var result = IsRestrictionViolated(player, userInfo);

                    if (result != TypeViolated.APPROVED)
                    {
                        
                            int playerSlot = player.Slot;
                            g_iWarnTime[playerSlot] = Config.PrivateProfileWarningTime;
                            int printInterval = Config.PrivateProfileWarningPrintSeconds;
                            int remainingPrintTime = printInterval;

                            g_hTimer[playerSlot] = AddTimer(1.0f, () =>
                            {
                                if (player?.IsValid == true)
                                {
                                    g_iWarnTime[playerSlot]--;
                                    remainingPrintTime--;

                                    if (remainingPrintTime <= 0)
                                    {
                                        Server.NextFrame(() => {
                                            player.Print(Api.Localizer["Tag." + result.ToString(), ],$" {ChatColors.Silver}[ {ChatColors.Lime}SteamRestrict {ChatColors.Silver}] {ChatColors.LightRed}Your Steam profile or Game details are private. You will be kicked in {g_iWarnTime[playerSlot]} seconds.");
                                        });
                                        remainingPrintTime = printInterval;
                                    }

                                    if (g_iWarnTime[playerSlot] <= 0)
                                    {
                                        PlayerBan playerBan = new PlayerBan();
                                        Api.AddBan(playerBan, false);
                                        g_hTimer[playerSlot]?.Kill();
                                        g_hTimer[playerSlot] = null;
                                    }
                                }
                                else
                                {
                                    g_hTimer[playerSlot]?.Kill();
                                    g_hTimer[playerSlot] = null;
                                }
                            }, TimerFlags.REPEAT);
                    }
                }
            });
        });
    }
    
    private TypeViolated IsRestrictionViolated(CCSPlayerController player, SteamUserInfo userInfo)
    {
        var steamId64 = player.AuthorizedSteamID?.SteamId64 ?? 0;

        if (Api.GetAdminsBySteamId(steamId64.ToString(), false).Result.Count > 0)
        {
            return TypeViolated.APPROVED;
        }

        //TODO Добавить команду для rcon что бы можо было добавлять через консоль или через админку
        BypassConfig bypassConfig = _bypassConfig ?? new BypassConfig();
        PlayerBypassConfig? playerBypassConfig = bypassConfig.GetPlayerConfig(steamId64);

        if (!(playerBypassConfig?.BypassMinimumHours ?? false) && Config.MinimumHour != -1 && userInfo.CS2Playtime < Config.MinimumHour)
            return TypeViolated.MIN_HOURS;

        if (!(playerBypassConfig?.BypassMinimumLevel ?? false) && Config.MinimumLevel != -1 && userInfo.SteamLevel < Config.MinimumLevel)
            return TypeViolated.STEAM_LEVEL;

        if (!(playerBypassConfig?.BypassMinimumSteamAccountAge ?? false) && Config.MinimumSteamAccountAgeInDays != -1 && (DateTime.Now - userInfo.SteamAccountAge).TotalDays < Config.MinimumSteamAccountAgeInDays)
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
}