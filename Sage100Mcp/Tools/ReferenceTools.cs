using System.ComponentModel;
using System.Text;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using Sage100Mcp.Data;

namespace Sage100Mcp.Tools;

/// <summary>Outils de référence : bases, journaux, plan comptable, tiers.</summary>
[McpServerToolType]
public sealed class ReferenceTools
{
    [McpServerTool(Name = "sage_lister_bases")]
    [Description("Liste les bases Sage 100 configurées et teste la connexion à chacune. " +
                 "À utiliser en premier pour connaître le nom des bases disponibles (clients/sociétés).")]
    public static async Task<string> ListerBases(SageDatabaseRegistry registry, CancellationToken ct)
    {
        var sb = new StringBuilder("## Bases Sage 100 configurées\n\n");
        foreach (var db in registry.All)
        {
            string statut;
            try
            {
                var rows = await registry.QueryAsync(db.Name,
                    "SELECT TOP 1 1 AS ok FROM F_COMPTEG", null, ct);
                statut = rows.Count > 0 ? "✅ connectée" : "⚠️ connectée (vide)";
            }
            catch (Exception ex)
            {
                statut = $"❌ erreur : {ex.Message}";
            }
            var def = db.Default ? " *(par défaut)*" : "";
            sb.AppendLine($"- **{db.Name}**{def} — {db.Description} — {statut}");
        }
        sb.AppendLine("\n*Lecture seule. Précisez le nom de la base dans les autres outils, ou laissez vide pour la base par défaut.*");
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_lister_journaux")]
    [Description("Liste les journaux comptables (achats, ventes, banque, OD, etc.) d'une base Sage.")]
    public static async Task<string> ListerJournaux(
        SageDatabaseRegistry registry,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var rows = await registry.QueryAsync(base_sage,
            "SELECT JO_Num, JO_Intitule, JO_Type FROM F_JOURNAUX ORDER BY JO_Num", null, ct);
        if (rows.Count == 0) return "Aucun journal trouvé.";

        string TypeJournal(object? v) => SageFormat.ToLong(v) switch
        {
            0 => "Achats", 1 => "Ventes", 2 => "Trésorerie", 3 => "Général",
            4 => "Situation", 5 => "Analytique", _ => "Autre"
        };

        return "## Journaux comptables\n\n" + SageFormat.Table(rows,
            ("Code", r => SageFormat.Text(r["JO_Num"])),
            ("Intitulé", r => SageFormat.Text(r["JO_Intitule"])),
            ("Type", r => TypeJournal(r["JO_Type"])));
    }

    [McpServerTool(Name = "sage_rechercher_comptes")]
    [Description("Recherche dans le plan comptable général (F_COMPTEG) par numéro ou intitulé. " +
                 "Exemples : '707' (ventes de marchandises), 'salaires', '411' (clients).")]
    public static async Task<string> RechercherComptes(
        SageDatabaseRegistry registry,
        [Description("Texte recherché dans le numéro ou l'intitulé du compte.")] string recherche,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        [Description("Nombre maximum de résultats (défaut 50).")] int? limite = null,
        CancellationToken ct = default)
    {
        var top = Math.Clamp(limite ?? 50, 1, 500);
        var rows = await registry.QueryAsync(base_sage,
            $"SELECT TOP ({top}) CG_Num, CG_Intitule FROM F_COMPTEG " +
            "WHERE CG_Num LIKE @q OR CG_Intitule LIKE @q ORDER BY CG_Num",
            new Dictionary<string, object?> { ["@q"] = $"%{recherche}%" }, ct);
        if (rows.Count == 0) return $"Aucun compte ne correspond à « {recherche} ».";

        return $"## Comptes correspondant à « {recherche} » ({rows.Count})\n\n" + SageFormat.Table(rows,
            ("Compte", r => SageFormat.Text(r["CG_Num"])),
            ("Intitulé", r => SageFormat.Text(r["CG_Intitule"])));
    }

    [McpServerTool(Name = "sage_rechercher_tiers")]
    [Description("Recherche un tiers (client, fournisseur, salarié) par numéro ou raison sociale dans F_COMPTET.")]
    public static async Task<string> RechercherTiers(
        SageDatabaseRegistry registry,
        [Description("Texte recherché (numéro, intitulé, ville).")] string recherche,
        [Description("Type de tiers : 'client', 'fournisseur', 'salarie' ou vide pour tous.")] string? type = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        [Description("Nombre maximum de résultats (défaut 50).")] int? limite = null,
        CancellationToken ct = default)
    {
        var top = Math.Clamp(limite ?? 50, 1, 500);
        var filtreType = (type?.Trim().ToLowerInvariant()) switch
        {
            "client" or "clients" => " AND CT_Type = 0",
            "fournisseur" or "fournisseurs" => " AND CT_Type = 1",
            "salarie" or "salarié" or "salaries" => " AND CT_Type = 2",
            _ => ""
        };
        var rows = await registry.QueryAsync(base_sage,
            $"SELECT TOP ({top}) CT_Num, CT_Intitule, CT_Type, CT_Ville, CT_Telephone, CT_EMail " +
            "FROM F_COMPTET WHERE (CT_Num LIKE @q OR CT_Intitule LIKE @q OR CT_Ville LIKE @q)" +
            filtreType + " ORDER BY CT_Intitule",
            new Dictionary<string, object?> { ["@q"] = $"%{recherche}%" }, ct);
        if (rows.Count == 0) return $"Aucun tiers ne correspond à « {recherche} ».";

        string TypeTiers(object? v) => SageFormat.ToLong(v) switch
        {
            0 => "Client", 1 => "Fournisseur", 2 => "Salarié", _ => "Autre"
        };

        return $"## Tiers correspondant à « {recherche} » ({rows.Count})\n\n" + SageFormat.Table(rows,
            ("Numéro", r => SageFormat.Text(r["CT_Num"])),
            ("Intitulé", r => SageFormat.Text(r["CT_Intitule"])),
            ("Type", r => TypeTiers(r["CT_Type"])),
            ("Ville", r => SageFormat.Text(r["CT_Ville"])),
            ("Téléphone", r => SageFormat.Text(r["CT_Telephone"])),
            ("E-mail", r => SageFormat.Text(r["CT_EMail"])));
    }
}
