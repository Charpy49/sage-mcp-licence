using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Sage100Mcp.LicenseServer.Licensing;

public sealed class LicenseRepository
{
    private readonly string _connectionString;

    public LicenseRepository(IConfiguration configuration)
    {
        var dbPath = configuration["Licensing:DatabasePath"] ?? "licenses.db";
        if (!Path.IsPathRooted(dbPath))
        {
            dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Licenses (
                Id TEXT PRIMARY KEY,
                ClientName TEXT NOT NULL,
                KeyHash TEXT NOT NULL UNIQUE,
                KeyPrefix TEXT NOT NULL,
                AllowedToolsJson TEXT NULL,
                ExpiresAtUtc TEXT NOT NULL,
                IsRevoked INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                LastValidatedAtUtc TEXT NULL,
                LastValidatedIp TEXT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task<LicenseRecord> CreateAsync(string clientName, string licenseKey, DateTimeOffset expiresAtUtc,
        IReadOnlyList<string>? allowedTools, CancellationToken ct)
    {
        var record = new LicenseRecord
        {
            Id = Guid.NewGuid().ToString("D"),
            ClientName = clientName,
            KeyHash = LicenseKeyGenerator.Hash(licenseKey),
            KeyPrefix = LicenseKeyGenerator.Prefix(licenseKey),
            AllowedTools = allowedTools,
            ExpiresAtUtc = expiresAtUtc,
            IsRevoked = false,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO Licenses (Id, ClientName, KeyHash, KeyPrefix, AllowedToolsJson, ExpiresAtUtc, IsRevoked, CreatedAtUtc)
            VALUES ($id, $clientName, $keyHash, $keyPrefix, $allowedTools, $expiresAt, 0, $createdAt);
            """;
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$clientName", record.ClientName);
        cmd.Parameters.AddWithValue("$keyHash", record.KeyHash);
        cmd.Parameters.AddWithValue("$keyPrefix", record.KeyPrefix);
        cmd.Parameters.AddWithValue("$allowedTools", (object?)Serialize(record.AllowedTools) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expiresAt", record.ExpiresAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$createdAt", record.CreatedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        return record;
    }

    public async Task<LicenseRecord?> FindByKeyAsync(string licenseKey, CancellationToken ct)
    {
        var hash = LicenseKeyGenerator.Hash(licenseKey);
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Licenses WHERE KeyHash = $hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<LicenseRecord?> FindByIdAsync(string id, CancellationToken ct)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Licenses WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<LicenseRecord>> ListAsync(CancellationToken ct)
    {
        var results = new List<LicenseRecord>();
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Licenses ORDER BY CreatedAtUtc DESC";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(Map(reader));
        }
        return results;
    }

    public async Task RecordValidationAsync(string id, string? remoteIp, CancellationToken ct)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Licenses SET LastValidatedAtUtc = $now, LastValidatedIp = $ip WHERE Id = $id";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$ip", (object?)remoteIp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> UpdateAsync(string id, string? clientName, DateTimeOffset? expiresAtUtc,
        bool? isRevoked, IReadOnlyList<string>? allowedTools, bool clearAllowedTools, CancellationToken ct)
    {
        var existing = await FindByIdAsync(id, ct);
        if (existing is null) return false;

        IReadOnlyList<string>? effectiveAllowedTools = existing.AllowedTools;
        if (clearAllowedTools) effectiveAllowedTools = null;
        else if (allowedTools is not null) effectiveAllowedTools = allowedTools;

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE Licenses SET
                ClientName = $clientName,
                ExpiresAtUtc = $expiresAt,
                IsRevoked = $isRevoked,
                AllowedToolsJson = $allowedTools
            WHERE Id = $id
            """;
        cmd.Parameters.AddWithValue("$clientName", clientName ?? existing.ClientName);
        cmd.Parameters.AddWithValue("$expiresAt", (expiresAtUtc ?? existing.ExpiresAtUtc).ToString("O"));
        cmd.Parameters.AddWithValue("$isRevoked", (isRevoked ?? existing.IsRevoked) ? 1 : 0);
        cmd.Parameters.AddWithValue("$allowedTools", (object?)Serialize(effectiveAllowedTools) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Licenses WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    private static string? Serialize(IReadOnlyList<string>? allowedTools) =>
        allowedTools is null ? null : JsonSerializer.Serialize(allowedTools);

    private static LicenseRecord Map(SqliteDataReader reader)
    {
        var allowedToolsJson = reader["AllowedToolsJson"] as string;
        return new LicenseRecord
        {
            Id = (string)reader["Id"],
            ClientName = (string)reader["ClientName"],
            KeyHash = (string)reader["KeyHash"],
            KeyPrefix = (string)reader["KeyPrefix"],
            AllowedTools = allowedToolsJson is null
                ? null
                : JsonSerializer.Deserialize<List<string>>(allowedToolsJson),
            ExpiresAtUtc = DateTimeOffset.Parse((string)reader["ExpiresAtUtc"]),
            IsRevoked = Convert.ToInt64(reader["IsRevoked"]) != 0,
            CreatedAtUtc = DateTimeOffset.Parse((string)reader["CreatedAtUtc"]),
            LastValidatedAtUtc = reader["LastValidatedAtUtc"] as string is { } lv ? DateTimeOffset.Parse(lv) : null,
            LastValidatedIp = reader["LastValidatedIp"] as string,
        };
    }
}
