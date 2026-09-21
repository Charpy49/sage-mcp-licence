using Microsoft.Data.Sqlite;

namespace Sage100Mcp.LicenseServer.Licensing;

/// <summary>Résolution de la base SQLite locale, partagée par les dépôts.</summary>
internal static class SqlitePaths
{
    public static string ResolveConnectionString(IConfiguration configuration)
    {
        var dbPath = configuration["Licensing:DatabasePath"] ?? "licenses.db";
        if (!Path.IsPathRooted(dbPath))
        {
            dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    /// <summary>Ajoute une colonne si elle n'existe pas déjà (migration des bases existantes).</summary>
    public static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name";
        check.Parameters.AddWithValue("$name", column);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }
}
