using System.Data.Common;
using System.Text;

namespace Sage100Mcp.Installer;

/// <summary>
/// Une base Sage 100 déclarée dans <c>Sage:Databases</c> du appsettings.json.
/// </summary>
internal sealed class SageDatabase
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Server { get; set; } = "(local)";
    public string Database { get; set; } = "";
    public bool Default { get; set; }

    /// <summary>
    /// Chaîne d'origine d'une base déjà configurée : ses options (authentification SQL, délais…)
    /// sont conservées, seuls serveur et base sont remplacés.
    /// </summary>
    public string? OriginalConnectionString { get; init; }

    private string? _originalServer;
    private string? _originalDatabase;

    public string BuildConnectionString()
    {
        // Base inchangée : chaîne reprise à l'identique (DbConnectionStringBuilder réécrirait
        // toutes les clés en minuscules).
        if (!string.IsNullOrWhiteSpace(OriginalConnectionString)
            && string.Equals(Server.Trim(), _originalServer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Database.Trim(), _originalDatabase, StringComparison.OrdinalIgnoreCase))
        {
            return OriginalConnectionString;
        }

        var builder = new DbConnectionStringBuilder();
        if (!string.IsNullOrWhiteSpace(OriginalConnectionString))
        {
            builder.ConnectionString = OriginalConnectionString;
            // Plusieurs synonymes existent : on les retire tous avant de réécrire.
            foreach (var key in new[] { "Server", "Data Source", "Address", "Addr", "Network Address",
                                        "Database", "Initial Catalog" })
            {
                builder.Remove(key);
            }
        }
        else
        {
            builder["Trusted_Connection"] = "True";
            builder["TrustServerCertificate"] = "True";
            builder["Encrypt"] = "False";
            builder["Application Name"] = "Sage100-MCP";
        }

        var rebuilt = new DbConnectionStringBuilder
        {
            ["Server"] = Server.Trim(),
            ["Database"] = Database.Trim()
        };
        foreach (string key in builder.Keys) rebuilt[key] = builder[key];
        return rebuilt.ConnectionString;
    }

    /// <summary>Reconstitue une base à partir d'une entrée existante de appsettings.json.</summary>
    public static SageDatabase FromConnectionString(string name, string description, string connectionString, bool isDefault)
    {
        var builder = new DbConnectionStringBuilder();
        try { builder.ConnectionString = connectionString; }
        catch (ArgumentException) { /* chaîne illisible : champs vides, l'utilisateur corrigera */ }

        var server = First(builder, "Server", "Data Source", "Address", "Addr", "Network Address") ?? "(local)";
        var database = First(builder, "Database", "Initial Catalog") ?? "";
        return new SageDatabase
        {
            Name = name,
            Description = description,
            Server = server,
            Database = database,
            Default = isDefault,
            OriginalConnectionString = connectionString,
            _originalServer = server,
            _originalDatabase = database
        };
    }

    private static string? First(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (builder.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text) return text;
        }
        return null;
    }
}

/// <summary>
/// Lecture des fichiers de base Sage 100 SQL (<c>.gcm</c> gestion commerciale, <c>.mae</c> comptabilité…).
/// <para>
/// Sous Sage 100 SQL, ces fichiers ne contiennent plus les données : ce sont de courts fichiers INI
/// qui désignent l'instance SQL Server. La base SQL porte le nom du fichier.
/// <code>
/// [CBASE]
/// ServeurSQL=(local)
/// Createur=COLU
/// Type=CIAL
/// </code>
/// </para>
/// </summary>
internal static class SageFileReader
{
    public const string DialogFilter =
        "Bases Sage 100 (*.gcm;*.mae)|*.gcm;*.mae|Gestion commerciale (*.gcm)|*.gcm|Comptabilité (*.mae)|*.mae|Tous les fichiers (*.*)|*.*";

    public static SageDatabase Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Fichiers écrits par Sage en ANSI occidental.
        foreach (var raw in File.ReadAllLines(path, Encoding.Latin1))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('[') || line.StartsWith(';')) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        if (!values.TryGetValue("ServeurSQL", out var server))
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} n'est pas un fichier de base Sage 100 SQL (clé ServeurSQL absente).");
        }

        var fileName = Path.GetFileNameWithoutExtension(path);
        var database = values.TryGetValue("Base", out var explicitBase) && explicitBase.Length > 0
            ? explicitBase
            : fileName;

        return new SageDatabase
        {
            Name = fileName,
            Description = Describe(values.GetValueOrDefault("Type"), Path.GetExtension(path)),
            Server = string.IsNullOrWhiteSpace(server) ? "(local)" : server,
            Database = database
        };
    }

    public static string Describe(string? type, string extension) =>
        (type?.ToUpperInvariant(), extension.ToLowerInvariant()) switch
        {
            ("CIAL", _) or (_, ".gcm") => "Gestion commerciale",
            ("CPTA", _) or (_, ".mae") => "Comptabilité",
            ("IMMO", _) or (_, ".imo") => "Immobilisations",
            _ => "Base Sage 100"
        };
}
