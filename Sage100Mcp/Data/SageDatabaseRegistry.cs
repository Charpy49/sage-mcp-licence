using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Sage100Mcp.Configuration;

namespace Sage100Mcp.Data;

/// <summary>
/// Annuaire des bases Sage 100 configurées. Résout une base par nom (ou la base par défaut)
/// et fournit l'exécution de requêtes <b>strictement en lecture seule</b>.
/// </summary>
public sealed class SageDatabaseRegistry
{
    private readonly IReadOnlyList<SageDatabaseOptions> _databases;

    public SageDatabaseRegistry(IOptions<SageOptions> options)
    {
        _databases = options.Value.Databases ?? new List<SageDatabaseOptions>();
        if (_databases.Count == 0)
            throw new InvalidOperationException(
                "Aucune base Sage configurée. Renseignez la section \"Sage:Databases\" de appsettings.json.");
    }

    public IReadOnlyList<SageDatabaseOptions> All => _databases;

    /// <summary>Résout une base par son nom ; si <paramref name="name"/> est vide, renvoie la base par défaut.</summary>
    public SageDatabaseOptions Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return _databases.FirstOrDefault(d => d.Default) ?? _databases[0];

        var match = _databases.FirstOrDefault(
            d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var available = string.Join(", ", _databases.Select(d => d.Name));
            throw new ArgumentException($"Base Sage inconnue : '{name}'. Bases disponibles : {available}.");
        }
        return match;
    }

    /// <summary>
    /// Exécute une requête SELECT et matérialise les lignes. La connexion est forcée en
    /// lecture seule (isolation READ UNCOMMITTED, pas de verrous sur la base de production)
    /// et toute commande non-SELECT est rejetée.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string? databaseName,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        GuardReadOnly(sql);
        var db = Resolve(databaseName);

        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Lecture non bloquante : on n'interfère pas avec les utilisateurs Sage en production.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
                command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                row[reader.GetName(i)] = value;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Garde-fou : seules les requêtes SELECT / WITH (CTE) sont autorisées.</summary>
    private static void GuardReadOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("Requête vide.");

        var trimmed = sql.TrimStart();
        var firstWord = new string(trimmed.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        if (firstWord is not ("SELECT" or "WITH"))
            throw new InvalidOperationException(
                "Opération refusée : le serveur Sage MCP est en lecture seule (SELECT uniquement).");

        // Interdit les mots-clés de modification, même via injection dans un SELECT.
        string[] forbidden =
        {
            " INSERT ", " UPDATE ", " DELETE ", " MERGE ", " DROP ", " ALTER ", " CREATE ",
            " TRUNCATE ", " EXEC ", " EXECUTE ", " GRANT ", " REVOKE ", " INTO "
        };
        var upper = $" {sql.ToUpperInvariant().Replace("\r", " ").Replace("\n", " ").Replace("\t", " ")} ";
        foreach (var word in forbidden)
        {
            if (upper.Contains(word))
                throw new InvalidOperationException(
                    $"Opération refusée (lecture seule) : mot-clé interdit détecté ({word.Trim()}).");
        }
    }
}
