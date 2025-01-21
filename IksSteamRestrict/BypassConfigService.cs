using System.Text.Json;

namespace IksSteamRestrict;

public class PlayerBypassConfig
	{
		public bool BypassMinimumHours { get; set; } = false;
		public bool BypassMinimumLevel { get; set; } = false;
		public bool BypassMinimumSteamAccountAge { get; set; } = false;
		public bool BypassPrivateProfile { get; set; } = false;
		public bool BypassTradeBanned { get; set; } = false;
		public bool BypassVACBanned { get; set; } = false;
		public bool BypassGameBanned { get; set; } = false;
	}

	public class BypassConfig
	{
		private Dictionary<ulong, PlayerBypassConfig> _playerConfigs = new Dictionary<ulong, PlayerBypassConfig>();

		public PlayerBypassConfig? GetPlayerConfig(ulong steamID)
		{
			if (_playerConfigs.TryGetValue(steamID, out var playerConfig))
				return playerConfig;

			return null;
		}

		public void AddPlayerConfig(ulong steamID, PlayerBypassConfig playerConfig)
		{
			_playerConfigs[steamID] = playerConfig;
		}

		public Dictionary<ulong, PlayerBypassConfig> GetAllPlayerConfigs()
		{
			return _playerConfigs;
		}
	}

	public class BypassConfigService
	{
		private readonly string _configFilePath;

		public BypassConfigService(string configFilePath)
		{
			_configFilePath = configFilePath;
		}

		public BypassConfig LoadConfig()
		{
			if (File.Exists(_configFilePath))
			{
				string json = File.ReadAllText(_configFilePath);
				var playerConfigs = JsonSerializer.Deserialize<Dictionary<ulong, PlayerBypassConfig>>(json)!;
				var bypassConfig = new BypassConfig();

				foreach (var kvp in playerConfigs)
				{
					bypassConfig.AddPlayerConfig(kvp.Key, kvp.Value);
				}

				return bypassConfig;
			}
			else
			{
				var defaultConfig = new BypassConfig();

				defaultConfig.AddPlayerConfig(76561198345583467, new PlayerBypassConfig
				{
					BypassMinimumHours = false,
					BypassMinimumLevel = true,
					BypassMinimumSteamAccountAge = false,
					BypassPrivateProfile = true,
					BypassTradeBanned = false,
					BypassVACBanned = true,
					BypassGameBanned = true
				});

				defaultConfig.AddPlayerConfig(76561198132924835, new PlayerBypassConfig
				{
					BypassMinimumHours = true,
					BypassMinimumLevel = false,
					BypassMinimumSteamAccountAge = true,
					BypassPrivateProfile = false,
					BypassTradeBanned = true,
					BypassVACBanned = false,
					BypassGameBanned = false
				});

				string json = JsonSerializer.Serialize(defaultConfig.GetAllPlayerConfigs(), new JsonSerializerOptions { WriteIndented = true });
				File.WriteAllText(_configFilePath, json);

				return defaultConfig;
			}
		}
	}