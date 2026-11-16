using Microsoft.Data.Sqlite;
using Sage100Mcp.LicenseServer.Licensing;

namespace Sage100Mcp.LicenseServer.Shop;

/// <summary>
/// Lien entre un achat Stripe et la licence qu'il a créée. Une ligne par session Checkout réussie.
/// <see cref="ProtectedLicenseKey"/> est la clé de licence chiffrée (Data Protection) : la table Licenses
/// n'en garde que le hash, mais la page de confirmation doit pouvoir la réafficher si le client la recharge.
/// </summary>
public sealed record SubscriptionRecord(
    string CheckoutSessionId,
    string StripeCustomerId,
    string StripeSubscriptionId,
    string LicenseId,
    string Plan,
    string Status,
    string? Email,
    bool IsTrial,
    string? ProtectedLicenseKey,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed class SubscriptionRepository
{
    private readonly string _connectionString;

    public SubscriptionRepository(IConfiguration configuration)
    {
        _connectionString = SqlitePaths.ResolveConnectionString(configuration);
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Subscriptions (
                CheckoutSessionId TEXT PRIMARY KEY,
                StripeCustomerId TEXT NOT NULL,
                StripeSubscriptionId TEXT NOT NULL UNIQUE,
                LicenseId TEXT NOT NULL,
                Plan TEXT NOT NULL,
                Status TEXT NOT NULL,
                Email TEXT NULL,
                IsTrial INTEGER NOT NULL DEFAULT 0,
                ProtectedLicenseKey TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Subscriptions_LicenseId ON Subscriptions (LicenseId);
            CREATE INDEX IF NOT EXISTS IX_Subscriptions_Email ON Subscriptions (Email COLLATE NOCASE);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task InsertAsync(SubscriptionRecord r, CancellationToken ct)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO Subscriptions (CheckoutSessionId, StripeCustomerId, StripeSubscriptionId, LicenseId, Plan,
                Status, Email, IsTrial, ProtectedLicenseKey, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($session, $customer, $subscription, $licenseId, $plan, $status, $email, $isTrial, $key,
                $createdAt, $updatedAt)
            """;
        cmd.Parameters.AddWithValue("$session", r.CheckoutSessionId);
        cmd.Parameters.AddWithValue("$customer", r.StripeCustomerId);
        cmd.Parameters.AddWithValue("$subscription", r.StripeSubscriptionId);
        cmd.Parameters.AddWithValue("$licenseId", r.LicenseId);
        cmd.Parameters.AddWithValue("$plan", r.Plan);
        cmd.Parameters.AddWithValue("$status", r.Status);
        cmd.Parameters.AddWithValue("$email", (object?)r.Email ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$isTrial", r.IsTrial ? 1 : 0);
        cmd.Parameters.AddWithValue("$key", (object?)r.ProtectedLicenseKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", r.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$updatedAt", r.UpdatedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<SubscriptionRecord?> FindBySessionAsync(string sessionId, CancellationToken ct) =>
        FindAsync("CheckoutSessionId = $value", sessionId, ct);

    public Task<SubscriptionRecord?> FindBySubscriptionAsync(string subscriptionId, CancellationToken ct) =>
        FindAsync("StripeSubscriptionId = $value", subscriptionId, ct);

    public Task<SubscriptionRecord?> FindByLicenseAsync(string licenseId, CancellationToken ct) =>
        FindAsync("LicenseId = $value", licenseId, ct);

    /// <summary>Un essai a-t-il déjà été accordé à cette adresse ? (un seul essai par adresse)</summary>
    public async Task<bool> HasTrialAsync(string email, CancellationToken ct)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Subscriptions WHERE IsTrial = 1 AND Email = $email COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$email", email.Trim());
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    public async Task UpdateStateAsync(string subscriptionId, string plan, string status, CancellationToken ct)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "UPDATE Subscriptions SET Plan = $plan, Status = $status, UpdatedAtUtc = $now WHERE StripeSubscriptionId = $id";
        cmd.Parameters.AddWithValue("$plan", plan);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", subscriptionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<SubscriptionRecord?> FindAsync(string where, string value, CancellationToken ct)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM Subscriptions WHERE {where} ORDER BY CreatedAtUtc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$value", value);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new SubscriptionRecord(
            (string)reader["CheckoutSessionId"],
            (string)reader["StripeCustomerId"],
            (string)reader["StripeSubscriptionId"],
            (string)reader["LicenseId"],
            (string)reader["Plan"],
            (string)reader["Status"],
            reader["Email"] as string,
            Convert.ToInt64(reader["IsTrial"]) != 0,
            reader["ProtectedLicenseKey"] as string,
            DateTimeOffset.Parse((string)reader["CreatedAtUtc"]),
            DateTimeOffset.Parse((string)reader["UpdatedAtUtc"]));
    }
}
