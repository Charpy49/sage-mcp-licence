namespace Sage100Mcp.Configuration;

/// <summary>Racine de configuration (section "Sage" de appsettings.json).</summary>
public sealed class SageOptions
{
    public const string SectionName = "Sage";

    public List<SageDatabaseOptions> Databases { get; set; } = new();
}

/// <summary>Configuration d'une base Sage 100 (SQL Server) accessible en lecture seule.</summary>
public sealed class SageDatabaseOptions
{
    /// <summary>Nom logique utilisé dans les outils (ex. "Estelle_INT").</summary>
    public string Name { get; set; } = "";

    /// <summary>Description lisible de la société / base.</summary>
    public string? Description { get; set; }

    /// <summary>Chaîne de connexion SQL Server.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Base utilisée par défaut quand l'appelant ne précise pas de base.</summary>
    public bool Default { get; set; }
}
