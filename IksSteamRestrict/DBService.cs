using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace IksSteamRestrict;

public class DBService
{
    private readonly IksSteamRestrict _plugin;
    private readonly string _connectionString;
    private readonly Dictionary<ulong, bool> _cache = new();

    public DBService(IksSteamRestrict plugin, string connectionString)
    {
        _plugin = plugin;
        _connectionString = connectionString;
        EnsureDatabaseSetup();
    }

    // Метод для создания таблицы, если её нет
    private void EnsureDatabaseSetup()
    {
        if (_plugin.Config.Debug)
        {
            _plugin.Logger.LogInformation("Database string: {0}", _connectionString);
        }

        try
        {
            using var connection = new MySqlConnection(_connectionString);
            connection.Open();

            var createTableQuery = @"
                CREATE TABLE IF NOT EXISTS iks_steam_restrict (
                    id INT AUTO_INCREMENT PRIMARY KEY,
                    steam_id BIGINT NOT NULL UNIQUE,
                    name VARCHAR(100) NOT NULL,
                    is_approved BOOLEAN NOT NULL,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );";

            using var command = new MySqlCommand(createTableQuery, connection);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _plugin.Logger.LogError($"Error ensuring database setup: {ex.Message}");
        }
    }

    // Метод для получения пользователя по SteamId
    public async Task<bool> IsPlayerApprovedAsync(ulong steamId)
    {
        try
        {
            // Проверка кэша
            if (_cache.TryGetValue(steamId, out var cachedValue))
            {
                return cachedValue;
            }

            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string query = "SELECT is_approved FROM iks_steam_restrict WHERE steam_id = @SteamId;";
            using var command = new MySqlCommand(query, connection);
            command.Parameters.AddWithValue("@SteamId", steamId);

            var result = await command.ExecuteScalarAsync();

            // Сохранение в кэш
            var isApproved = result != null && Convert.ToBoolean(result);
            _cache[steamId] = isApproved;
            return isApproved;
        }
        catch (Exception ex)
        {
            _plugin.Logger.LogError($"Error checking player approval for SteamId {steamId}: {ex.Message}");
            return false; // Возвращаем false при ошибке
        }
    }

    // Метод для добавления пользователя, если он одобрен
    public async Task AddPlayerApprovedAsync(ulong steamId, string name)
    {
        try
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                INSERT INTO iks_steam_restrict (steam_id, name, is_approved)
                VALUES (@SteamId, @Name, @IsApproved)
                ON DUPLICATE KEY UPDATE
                    name = VALUES(name),
                    is_approved = VALUES(is_approved);";

            using var command = new MySqlCommand(query, connection);
            command.Parameters.AddWithValue("@SteamId", steamId);
            command.Parameters.AddWithValue("@Name", name);
            command.Parameters.AddWithValue("@IsApproved", true);

            await command.ExecuteNonQueryAsync();

            // Обновление кэша
            _cache[steamId] = true;
        }
        catch (Exception ex)
        {
            _plugin.Logger.LogError($"Error adding or updating player {name} with SteamId {steamId}: {ex.Message}");
        }
    }
}

// Модель данных пользователя
public class PlayerEntity
{
    public int Id { get; set; }
    public ulong SteamId { get; set; }
    public string Name { get; set; } = null!;
    public bool IsApproved { get; set; }
}
