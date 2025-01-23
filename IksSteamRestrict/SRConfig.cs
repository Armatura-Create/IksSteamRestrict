using System.Text.Json.Serialization;
using IksAdminApi;

namespace IksSteamRestrict;

public class SRConfig : PluginCFG<SRConfig>, IPluginCFG
{
    [JsonPropertyName("Debug")]
    public bool Debug { get; set; } = true;

    [JsonPropertyName("SteamWebAPI")]
    public string SteamWebAPI { get; set; } = "";
    

    [JsonPropertyName("MinimumHour")]
    public int MinimumHour { get; set; } = -1;

    
    [JsonPropertyName("MinimumLevel")]
    public int MinimumLevel { get; set; } = -1;
    
    [JsonPropertyName("DaysBanByLevel")]
    public int DaysBanByLevel { get; set; } = -1;


    [JsonPropertyName("MinimumSteamAccountAgeInDays")]
    public int MinimumSteamAccountAgeInDays { get; set; } = -1;
    

    [JsonPropertyName("BlockPrivateProfile")]
    public bool BlockPrivateProfile { get; set; } = false;

    [JsonPropertyName("BlockTradeBanned")]
    public bool BlockTradeBanned { get; set; } = false;

    [JsonPropertyName("BlockVACBanned")]
    public bool BlockVACBanned { get; set; } = false;

    [JsonPropertyName("BlockGameBanned")]
    public bool BlockGameBanned { get; set; } = false;
    
    [JsonPropertyName("BanRestrict")]
    public bool BanRestrict { get; set; } = false;
    
    [JsonPropertyName("WarningTime")]
    public int WarningTime { get; set; } = 5;
    
    
}