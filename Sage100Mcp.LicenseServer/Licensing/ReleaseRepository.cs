using Microsoft.Data.Sqlite;

namespace Sage100Mcp.LicenseServer.Licensing;

/// <summary>Dépôt des versions publiées (table <c>Releases</c> de la même base SQLite que les licences).</summary>
public sealed class ReleaseRepository
{
    public const string DefaultChannel = "stable";

    private readonly string _connectionString;

    public ReleaseRepository(IConfiguration configuration)
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
            CREATE TABLE IF NOT EXISTS Releases (
                Version TEXT PRIMARY KEY,
                Channel TEXT NOT NULL DEFAULT 'stable',
                DownloadUrl TEXT NOT NULL,
                Sha256 TEXT NOT NULL,
                Signature TEXT NULL,
                Notes TEXT NULL,
                IsMinimum INTEGER NOT NULL DEFAULT 0,
                IsYanked INTEGER NOT NULL DEFAULT 0,
                PublishedAtUtc TEXT NOT NULL
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

    public async Task<ReleaseRecord> CreateAsync(string version, string? channel, string downloadUrl, string sha256,
        string? signature, string? notes, bool isMinimum, CancellationToken ct)
    {
        var record = new ReleaseRecord
        {
            Version = version,
            Channel = string.IsNullOrWhiteSpace(channel) ? DefaultChannel : channel.Trim(),
            DownloadUrl = downloadUrl,
            Sha256 = sha256.Trim(),
            Signature = signature,
            Notes = notes,
            IsMinimum = isMinimum,
            IsYanked = false,
            PublishedAtUtc = DateTimeOffset.UtcNow,
        };

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO Releases (Version, Channel, DownloadUrl, Sha256, Signature, Notes, IsMinimum, IsYanked, PublishedAtUtc)
            VALUES ($version, $channel, $url, $sha, $signature, $notes, $isMinimum, 0, $publishedAt);
            """;
        cmd.Parameters.AddWithValue("$version", record.Version);
        cmd.Parameters.AddWithValue("$channel", record.Channel);
        cmd.Parameters.AddWithValue("$url", record.DownloadUrl);
        cmd.Parameters.AddWithValue("$sha", record.Sha256);
        cmd.Parameters.AddWithValue("$signature", (object?)record.Signature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$notes", (object?)record.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$isMinimum", record.IsMinimum ? 1 : 0);
        cmd.Parameters.AddWithValue("$publishedAt", record.PublishedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        return record;
    }

    public async Task<IReadOnlyList<ReleaseRecord>> ListAsync(CancellationToken ct)
    {
        var results = new List<ReleaseRecord>();
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Releases";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(Map(reader));
        }
        // Tri par version décroissante : impossible en SQL sur une chaîne "1.10.0" vs "1.9.0".
        return results.OrderByDescending(UpdateResolver.ParseVersion).ToList();
    }

    public async Task<ReleaseRecord?> FindAsync(string version, CancellationToken ct)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Releases WHERE Version = $version";
        cmd.Parameters.AddWithValue("$version", version);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<bool> SetFlagsAsync(string version, bool? isYanked, bool? isMinimum, CancellationToken ct)
    {
        var existing = await FindAsync(version, ct);
        if (existing is null) return false;

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Releases SET IsYanked = $isYanked, IsMinimum = $isMinimum WHERE Version = $version";
        cmd.Parameters.AddWithValue("$isYanked", (isYanked ?? existing.IsYanked) ? 1 : 0);
        cmd.Parameters.AddWithValue("$isMinimum", (isMinimum ?? existing.IsMinimum) ? 1 : 0);
        cmd.Parameters.AddWithValue("$version", version);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteAsync(string version, CancellationToken ct)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Releases WHERE Version = $version";
        cmd.Parameters.AddWithValue("$version", version);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static ReleaseRecord Map(SqliteDataReader reader) => new()
    {
        Version = (string)reader["Version"],
        Channel = (string)reader["Channel"],
        DownloadUrl = (string)reader["DownloadUrl"],
        Sha256 = (string)reader["Sha256"],
        Signature = reader["Signature"] as string,
        Notes = reader["Notes"] as string,
        IsMinimum = Convert.ToInt64(reader["IsMinimum"]) != 0,
        IsYanked = Convert.ToInt64(reader["IsYanked"]) != 0,
        PublishedAtUtc = DateTimeOffset.Parse((string)reader["PublishedAtUtc"]),
    };
}
