using System.Text.Json;

namespace IksSteamRestrict;

public class PlayerBypassConfig
{
    public class BypassConfig
    {
        private readonly List<ulong> _playersBypassed = new();

        // Проверка, обходится ли игрок
        public bool GetPlayerBypass(ulong steamID)
        {
            return _playersBypassed.Contains(steamID);
        }

        // Добавление игрока в обход
        public void AddPlayerConfig(ulong steamID)
        {
            if (!_playersBypassed.Contains(steamID))
            {
                _playersBypassed.Add(steamID);
            }
        }

        // Получение всех игроков, которых нужно обойти
        public List<ulong> GetAllBypassedPlayers()
        {
            return _playersBypassed;
        }
    }

    public class BypassConfigService
    {
        private readonly string _configFilePath;

        public BypassConfigService(string configFilePath)
        {
            _configFilePath = configFilePath;

            // Создание файла, если его нет
            if (!File.Exists(_configFilePath))
            {
                File.WriteAllText(_configFilePath, "[]");
            }
        }

        // Загрузка конфигурации из файла
        public BypassConfig LoadConfig()
        {
            var bypassConfig = new BypassConfig();

            try
            {
                var json = File.ReadAllText(_configFilePath);
                var playerConfigs = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();

                foreach (var id in playerConfigs)
                {
                    if (ulong.TryParse(id, out var steamID))
                    {
                        bypassConfig.AddPlayerConfig(steamID);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading bypass config: {ex.Message}");
            }

            return bypassConfig;
        }

        // Сохранение конфигурации в файл
        public void SaveConfig(BypassConfig bypassConfig)
        {
            try
            {
                var players = bypassConfig.GetAllBypassedPlayers();
                var json = JsonSerializer.Serialize(players.Select(id => id.ToString()).ToList());
                File.WriteAllText(_configFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving bypass config: {ex.Message}");
            }
        }
    }
}
