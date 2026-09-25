using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Sage100Mcp.LicenseServer.Licensing;

public sealed class LicenseRepository
{
    private readonly string _connectionString;

    public LicenseRepository(IConfiguration configuration)
    {
        _connectionString = SqlitePaths.ResolveConnectionString(configuration);
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

        // Migration des bases déjà déployées : colonnes de politique de mise à jour et d'inventaire.
        SqlitePaths.EnsureColumn(connection, "Licenses", "UpdateChannel", "TEXT NOT NULL DEFAULT 'stable'");
        SqlitePaths.EnsureColumn(connection, "Licenses", "PinnedVersion", "TEXT NULL");
        SqlitePaths.EnsureColumn(connection, "Licenses", "LastInstalledVersion", "TEXT NULL");
        SqlitePaths.EnsureColumn(connection, "Licenses", "LastTransport", "TEXT NULL");

        // Activation par poste. Les licences existantes passent à 1 poste : le premier qui se
        // présente avec un client 1.3.0 ou plus récent prend la place.
        SqlitePaths.EnsureColumn(connection, "Licenses", "MaxMachines", "INTEGER NOT NULL DEFAULT 1");

        using var activations = connection.CreateCommand();
        activations.CommandText =
            """
            CREATE TABLE IF NOT EXISTS LicenseActivations (
                LicenseId TEXT NOT NULL,
                MachineId TEXT NOT NULL,
                MachineName TEXT NULL,
                ActivatedAtUtc TEXT NOT NULL,
                LastSeenAtUtc TEXT NOT NULL,
                PRIMARY KEY (LicenseId, MachineId)
            );
            """;
        activations.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task<LicenseRecord> CreateAsync(string clientName, string licenseKey, DateTimeOffset expiresAtUtc,
        IReadOnlyList<string>? allowedTools, int maxMachines, CancellationToken ct)
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
            MaxMachines = maxMachines,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO Licenses (Id, ClientName, KeyHash, KeyPrefix, AllowedToolsJson, ExpiresAtUtc, IsRevoked, MaxMachines, CreatedAtUtc)
            VALUES ($id, $clientName, $keyHash, $keyPrefix, $allowedTools, $expiresAt, 0, $maxMachines, $createdAt);
            """;
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$clientName", record.ClientName);
        cmd.Parameters.AddWithValue("$keyHash", record.KeyHash);
        cmd.Parameters.AddWithValue("$keyPrefix", record.KeyPrefix);
        cmd.Parameters.AddWithValue("$allowedTools", (object?)Serialize(record.AllowedTools) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expiresAt", record.ExpiresAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$maxMachines", record.MaxMachines);
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

    /// <summary>
    /// Trace la validation et met à jour l'inventaire. <paramref name="installedVersion"/> et
    /// <paramref name="transport"/> sont conservés tels quels quand le client ne les fournit pas
    /// (clients antérieurs à la gestion des mises à jour).
    /// </summary>
    public async Task RecordValidationAsync(string id, string? remoteIp, string? installedVersion, string? transport,
        CancellationToken ct)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE Licenses SET
                LastValidatedAtUtc = $now,
                LastValidatedIp = $ip,
                LastInstalledVersion = COALESCE($version, LastInstalledVersion),
                LastTransport = COALESCE($transport, LastTransport)
            WHERE Id = $id
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$ip", (object?)remoteIp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$version", (object?)installedVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$transport", (object?)transport ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Définit le canal et l'épinglage de version d'une licence.</summary>
    public async Task<bool> SetUpdatePolicyAsync(string id, string? updateChannel, string? pinnedVersion,
        bool clearPinnedVersion, CancellationToken ct)
    {
        var existing = await FindByIdAsync(id, ct);
        if (existing is null) return false;

        var effectivePin = clearPinnedVersion ? null : (pinnedVersion ?? existing.PinnedVersion);

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Licenses SET UpdateChannel = $channel, PinnedVersion = $pin WHERE Id = $id";
        cmd.Parameters.AddWithValue("$channel",
            string.IsNullOrWhiteSpace(updateChannel) ? existing.UpdateChannel : updateChannel.Trim());
        cmd.Parameters.AddWithValue("$pin", (object?)effectivePin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> UpdateAsync(string id, string? clientName, DateTimeOffset? expiresAtUtc,
        bool? isRevoked, IReadOnlyList<string>? allowedTools, bool clearAllowedTools, int? maxMachines,
        CancellationToken ct)
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
                AllowedToolsJson = $allowedTools,
                MaxMachines = $maxMachines
            WHERE Id = $id
            """;
        cmd.Parameters.AddWithValue("$clientName", clientName ?? existing.ClientName);
        cmd.Parameters.AddWithValue("$expiresAt", (expiresAtUtc ?? existing.ExpiresAtUtc).ToString("O"));
        cmd.Parameters.AddWithValue("$isRevoked", (isRevoked ?? existing.IsRevoked) ? 1 : 0);
        cmd.Parameters.AddWithValue("$allowedTools", (object?)Serialize(effectiveAllowedTools) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$maxMachines", maxMachines ?? existing.MaxMachines);
        cmd.Parameters.AddWithValue("$id", id);
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM LicenseActivations WHERE LicenseId = $id; DELETE FROM Licenses WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    // --- Activation par poste ---

    /// <summary>
    /// Active la licence sur un poste, ou rafraîchit une activation existante. Seuls les
    /// <see cref="LicenseRecord.MaxMachines"/> postes activés les premiers sont acceptés : abaisser le
    /// quota écarte donc les derniers arrivés, sans effacer la trace de leur activation.
    /// La transaction (BEGIN IMMEDIATE) sérialise les activations concurrentes : deux postes neufs
    /// qui se présentent en même temps ne peuvent pas prendre tous deux la dernière place.
    /// </summary>
    /// <returns>Le poste est-il accepté, et quels postes occupent les places autorisées.</returns>
    public async Task<(bool Accepted, IReadOnlyList<ActivationRecord> Active)> ActivateAsync(
        LicenseRecord license, string machineId, string? machineName, CancellationToken ct)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var all = await ListActivationsAsync(connection, license, ct);
        var active = all.Where(a => a.IsActive).ToList();

        using var cmd = connection.CreateCommand();
        cmd.Parameters.AddWithValue("$licenseId", license.Id);
        cmd.Parameters.AddWithValue("$machineId", machineId);
        cmd.Parameters.AddWithValue("$machineName", (object?)machineName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        bool accepted;
        if (all.FirstOrDefault(a => a.MachineId == machineId) is { } known)
        {
            accepted = known.IsActive;
            cmd.CommandText =
                """
                UPDATE LicenseActivations SET LastSeenAtUtc = $now, MachineName = COALESCE($machineName, MachineName)
                WHERE LicenseId = $licenseId AND MachineId = $machineId
                """;
        }
        else if (active.Count < license.MaxMachines)
        {
            accepted = true;
            cmd.CommandText =
                """
                INSERT INTO LicenseActivations (LicenseId, MachineId, MachineName, ActivatedAtUtc, LastSeenAtUtc)
                VALUES ($licenseId, $machineId, $machineName, $now, $now)
                """;
        }
        else
        {
            // Poste refusé : il n'est pas enregistré, sinon chaque tentative laisserait une trace à nettoyer.
            return (false, active);
        }

        await cmd.ExecuteNonQueryAsync(ct);
        transaction.Commit();
        return (accepted, active);
    }

    public async Task<IReadOnlyList<ActivationRecord>> ListActivationsAsync(LicenseRecord license, CancellationToken ct)
    {
        using var connection = Open();
        return await ListActivationsAsync(connection, license, ct);
    }

    /// <summary>Postes de la licence, du plus ancien au plus récent ; seuls les MaxMachines premiers sont actifs.</summary>
    private static async Task<IReadOnlyList<ActivationRecord>> ListActivationsAsync(SqliteConnection connection,
        LicenseRecord license, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT * FROM LicenseActivations WHERE LicenseId = $licenseId ORDER BY ActivatedAtUtc, MachineId";
        cmd.Parameters.AddWithValue("$licenseId", license.Id);
        using var reader = await cmd.ExecuteReaderAsync(ct);

        var results = new List<ActivationRecord>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ActivationRecord(
                (string)reader["MachineId"],
                reader["MachineName"] as string,
                DateTimeOffset.Parse((string)reader["ActivatedAtUtc"]),
                DateTimeOffset.Parse((string)reader["LastSeenAtUtc"]),
                IsActive: results.Count < license.MaxMachines));
        }
        return results;
    }

    /// <summary>
    /// Libère un poste (changement de PC, réinstallation de Windows), ou tous les postes de la licence
    /// si <paramref name="machineId"/> est null. Renvoie le nombre d'activations supprimées.
    /// </summary>
    public async Task<int> DeactivateAsync(string licenseId, string? machineId, CancellationToken ct)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = machineId is null
            ? "DELETE FROM LicenseActivations WHERE LicenseId = $licenseId"
            : "DELETE FROM LicenseActivations WHERE LicenseId = $licenseId AND MachineId = $machineId";
        cmd.Parameters.AddWithValue("$licenseId", licenseId);
        if (machineId is not null) cmd.Parameters.AddWithValue("$machineId", machineId);
        return await cmd.ExecuteNonQueryAsync(ct);
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
            MaxMachines = Convert.ToInt32(reader["MaxMachines"]),
            CreatedAtUtc = DateTimeOffset.Parse((string)reader["CreatedAtUtc"]),
            LastValidatedAtUtc = reader["LastValidatedAtUtc"] as string is { } lv ? DateTimeOffset.Parse(lv) : null,
            LastValidatedIp = reader["LastValidatedIp"] as string,
            UpdateChannel = reader["UpdateChannel"] as string ?? ReleaseRepository.DefaultChannel,
            PinnedVersion = reader["PinnedVersion"] as string,
            LastInstalledVersion = reader["LastInstalledVersion"] as string,
            LastTransport = reader["LastTransport"] as string,
        };
    }
}
