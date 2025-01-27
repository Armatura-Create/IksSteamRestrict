using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace IksSteamRestrict;

public class SteamUserInfo
{
    public DateTime SteamAccountAge { get; set; }
    public int SteamLevel { get; set; }
    public int CS2Playtime { get; set; }
    public bool IsPrivate { get; set; }
    public bool IsGameDetailsPrivate { get; set; }
    public bool IsTradeBanned { get; set; }
    public bool IsVACBanned { get; set; }
    public bool IsGameBanned { get; set; }
}

public class SteamService
{
    private readonly HttpClient _httpClient;
    private readonly string _steamWebAPIKey;
    private readonly SRConfig _config;
    private readonly ILogger _logger;
    public SteamUserInfo UserInfo { get; }

    public SteamService(IksSteamRestrict plugin, SteamUserInfo userInfo)
    {
        _httpClient = plugin.Client;
        _config = plugin.Config;
        _logger = plugin.Logger;
        _steamWebAPIKey = _config.SteamWebAPI;
        UserInfo = userInfo;
    }

    public async Task FetchSteamUserInfo(string steamId)
    {
        if (_config.Debug)
        {
            _logger.LogInformation("Start fetching Steam user info");
        }

        var playtimeTask = FetchCs2PlaytimeAsync(steamId);
        var steamLevel = FetchSteamLevelAsync(steamId);
        var profileTask = FetchProfilePrivacyAsync(steamId);
        var tradeBanTask = FetchTradeBanStatusAsync(steamId);
        // var gameBanTask = FetchGameBanStatusAsync(steamId);

        await playtimeTask;
        await steamLevel;
        await profileTask;
        await tradeBanTask;
        // await gameBanTask;

        if (_config.Debug)
        {
            _logger.LogInformation("Steam user info fetched successfully");
        }
    }

    private async Task FetchCs2PlaytimeAsync(string steamId)
    {
        var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={_steamWebAPIKey}&steamid={steamId}&format=json";
        if (_config.Debug)
        {
            _logger.LogInformation($"Fetching CS2Playtime: {url}");
        }

        await FetchAndParseAsync(url, json =>
        {
            var cs2Playtime = ParseCs2Playtime(json);
            UserInfo.CS2Playtime = cs2Playtime == -1 ? -1 : cs2Playtime / 60;
        });
    }

    private async Task FetchSteamLevelAsync(string steamId)
    {
        var url = $"https://api.steampowered.com/IPlayerService/GetSteamLevel/v1/?key={_steamWebAPIKey}&steamid={steamId}";
        if (_config.Debug)
        {
            _logger.LogInformation($"Fetching SteamLevel: {url}");
        }

        await FetchAndParseAsync(url, json => { UserInfo.SteamLevel = ParseSteamLevel(json); });
    }

    private async Task FetchProfilePrivacyAsync(string steamId)
    {
        var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={_steamWebAPIKey}&steamids={steamId}";
        if (_config.Debug)
        {
            _logger.LogInformation($"Fetching Profile Privacy: {url}");
        }

        await FetchAndParseAsync(url, json => ParseSteamUserInfo(json, UserInfo));
    }

    private async Task FetchTradeBanStatusAsync(string steamId)
    {
        var url = $"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={_steamWebAPIKey}&steamids={steamId}";
        if (_config.Debug)
        {
            _logger.LogInformation($"Fetching Trade Ban Status: {url}");
        }

        await FetchAndParseAsync(url, json =>
        {
            ParseTradeBanStatus(json, UserInfo);
            ParseVACBanStatus(json, UserInfo);
        });
    }

    private async Task FetchGameBanStatusAsync(string steamId)
    {
        var url = $"https://api.steampowered.com/ISteamUser/GetUserGameBan/v1/?key={_steamWebAPIKey}&steamids={steamId}";
        if (_config.Debug)
        {
            _logger.LogInformation($"Fetching Game Ban Status: {url}");
        }

        await FetchAndParseAsync(url, json => ParseGameBanStatus(json, UserInfo));
    }

    private async Task FetchAndParseAsync(string url, Action<string> parseAction)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                parseAction(json);
            }
            else
            {
                _logger.LogError($"API error: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching or parsing data from {url}: {ex.Message}");
        }
    }

    private int ParseCs2Playtime(string json)
    {
        JObject data = JObject.Parse(json);
        JToken? game = data["response"]?["games"]?.FirstOrDefault(x => x["appid"]?.Value<int>() == 730);
        return game?["playtime_forever"]?.Value<int>() ?? -1;
    }

    private int ParseSteamLevel(string json)
    {
        var data = JObject.Parse(json);
        return (int)(data["response"]?["player_level"] ?? -1);
    }

    private void ParseSteamUserInfo(string json, SteamUserInfo userInfo)
    {
        var data = JObject.Parse(json);
        var player = data["response"]?["players"]?.FirstOrDefault();
        if (player == null) return;
        userInfo.IsPrivate = player["communityvisibilitystate"]?.ToObject<int?>() != 3;
        userInfo.IsGameDetailsPrivate = player["gameextrainfo"] == null;
        var timeCreated = player["timecreated"]?.ToObject<int?>();
        userInfo.SteamAccountAge = timeCreated.HasValue
            ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(timeCreated.Value)
            : DateTime.Now;
    }

    private void ParseTradeBanStatus(string json, SteamUserInfo userInfo)
    {
        var data = JObject.Parse(json);
        var playerBan = data["players"]?.FirstOrDefault();
        userInfo.IsTradeBanned = playerBan != null && (bool)(playerBan["CommunityBanned"] ?? false);
    }

    private void ParseGameBanStatus(string json, SteamUserInfo userInfo)
    {
        var data = JObject.Parse(json);
        var userGameBan = data["players"]?.FirstOrDefault();
        userInfo.IsGameBanned = userGameBan != null && (bool)(userGameBan["IsGameBanned"] ?? false);
    }

    private void ParseVACBanStatus(string json, SteamUserInfo userInfo)
    {
        var data = JObject.Parse(json);
        var userGameBan = data["players"]?.FirstOrDefault();
        userInfo.IsVACBanned = userGameBan != null && (bool)(userGameBan["VACBanned"] ?? false);
    }
}