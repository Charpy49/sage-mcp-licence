using System.ComponentModel;
using ModelContextProtocol.Server;
using Sage100Mcp.Data;

namespace Sage100Mcp.Tools;

/// <summary>Outils de stock : inventaire valorisé et articles en rupture (F_ARTSTOCK).</summary>
[McpServerToolType]
public sealed class StockTools
{
    [McpServerTool(Name = "sage_stock_inventaire")]
    [Description("Inventaire des stocks actuels : quantités et valorisation, regroupés par article (défaut), " +
                 "par famille ou par dépôt. Colonnes : quantité en stock, réservée, disponible et valorisation " +
                 "(F_ARTSTOCK). Filtres possibles par dépôt et par famille.")]
    public static async Task<string> StockInventaire(
        SageDatabaseRegistry registry,
        [Description("Regroupement : 'article' (défaut), 'famille' ou 'depot'.")] string? regroupement = null,
        [Description("Filtre dépôt (nom, recherche partielle). Vide = tous.")] string? depot = null,
        [Description("Filtre famille (code, recherche partielle). Vide = toutes.")] string? famille = null,
        [Description("Nombre maximum de lignes (défaut 50).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var n = Math.Clamp(limite ?? 50, 1, 500);
        var mode = (regroupement?.Trim().ToLowerInvariant()) switch
        {
            "famille" or "familles" => "famille",
            "depot" or "dépôt" or "depots" or "dépôts" => "depot",
            _ => "article"
        };

        var prms = new Dictionary<string, object?>();
        var filtres = new List<string>();
        if (!string.IsNullOrWhiteSpace(depot)) { filtres.Add("de.DE_Intitule LIKE @depot"); prms["@depot"] = $"%{depot.Trim()}%"; }
        if (!string.IsNullOrWhiteSpace(famille)) { filtres.Add("a.FA_CodeFamille LIKE @fam"); prms["@fam"] = $"%{famille.Trim()}%"; }
        var where = "WHERE 1 = 1" + (filtres.Count > 0 ? " AND " + string.Join(" AND ", filtres) : "");

        const string baseFrom =
            "FROM F_ARTSTOCK s " +
            "LEFT JOIN F_ARTICLE a ON a.AR_Ref = s.AR_Ref " +
            "LEFT JOIN F_DEPOT de ON de.DE_No = s.DE_No ";

        string sql, titre;
        switch (mode)
        {
            case "famille":
                titre = "par famille";
                sql = $@"SELECT TOP ({n}) ISNULL(NULLIF(CAST(a.FA_CodeFamille AS varchar(50)), ''), '(sans famille)') AS Cle,
                                COUNT(DISTINCT s.AR_Ref) AS NbArticles,
                                SUM(s.AS_QteSto) AS QteSto, SUM(s.AS_QteRes) AS QteRes, SUM(s.AS_MontSto) AS Valo
                         {baseFrom} {where}
                         GROUP BY ISNULL(NULLIF(CAST(a.FA_CodeFamille AS varchar(50)), ''), '(sans famille)')
                         HAVING SUM(s.AS_QteSto) <> 0
                         ORDER BY Valo DESC, QteSto DESC";
                break;
            case "depot":
                titre = "par dépôt";
                sql = $@"SELECT TOP ({n}) ISNULL(de.DE_Intitule, '(dépôt #' + CAST(s.DE_No AS varchar(10)) + ')') AS Cle,
                                COUNT(DISTINCT s.AR_Ref) AS NbArticles,
                                SUM(s.AS_QteSto) AS QteSto, SUM(s.AS_QteRes) AS QteRes, SUM(s.AS_MontSto) AS Valo
                         {baseFrom} {where}
                         GROUP BY ISNULL(de.DE_Intitule, '(dépôt #' + CAST(s.DE_No AS varchar(10)) + ')')
                         HAVING SUM(s.AS_QteSto) <> 0
                         ORDER BY Valo DESC, QteSto DESC";
                break;
            default:
                titre = "par article";
                sql = $@"SELECT TOP ({n}) s.AR_Ref AS Cle, MAX(a.AR_Design) AS Design, MAX(a.FA_CodeFamille) AS Famille,
                                SUM(s.AS_QteSto) AS QteSto, SUM(s.AS_QteRes) AS QteRes, SUM(s.AS_MontSto) AS Valo
                         {baseFrom} {where}
                         GROUP BY s.AR_Ref
                         HAVING SUM(s.AS_QteSto) <> 0
                         ORDER BY Valo DESC, QteSto DESC";
                break;
        }

        var rows = await registry.QueryAsync(base_sage, sql, prms, ct);
        if (rows.Count == 0) return "Aucun stock trouvé pour ces critères.";

        decimal totQte = rows.Sum(r => SageFormat.ToDecimal(r["QteSto"]));
        decimal totValo = rows.Sum(r => SageFormat.ToDecimal(r["Valo"]));
        string Qte(object? v) => SageFormat.ToDecimal(v).ToString("N0", SageFormat.Fr);
        string Dispo(IReadOnlyDictionary<string, object?> r) =>
            Qte(SageFormat.ToDecimal(r["QteSto"]) - SageFormat.ToDecimal(r["QteRes"]));

        string table = mode is "famille" or "depot"
            ? SageFormat.Table(rows,
                (mode == "famille" ? "Famille" : "Dépôt", r => SageFormat.Text(r["Cle"])),
                ("Articles", r => SageFormat.ToLong(r["NbArticles"]).ToString()),
                ("Qté stock", r => Qte(r["QteSto"])),
                ("Qté dispo.", Dispo),
                ("Valorisation", r => SageFormat.Euro(r["Valo"])))
            : SageFormat.Table(rows,
                ("Référence", r => SageFormat.Text(r["Cle"])),
                ("Désignation", r => SageFormat.Text(r["Design"])),
                ("Famille", r => SageFormat.Text(r["Famille"])),
                ("Qté stock", r => Qte(r["QteSto"])),
                ("Qté dispo.", Dispo),
                ("Valorisation", r => SageFormat.Euro(r["Valo"])));

        return $"## Inventaire des stocks {titre}\n\n" + table +
               $"\n*Qté dispo. = stock − réservé.* **Total affiché : {Qte(totQte)} unités · " +
               $"valorisation {SageFormat.Euro(totValo)}** ({rows.Count} lignes)";
    }

    [McpServerTool(Name = "sage_articles_en_rupture")]
    [Description("Articles en rupture ou bientôt en rupture : stock disponible (stock − réservé) négatif ou nul, " +
                 "ou inférieur/égal au stock minimum défini (F_ARTSTOCK.AS_QteMini). " +
                 "Un seuil peut être forcé pour repérer les articles sous un niveau donné. " +
                 "Ne considère que les articles suivis en stock.")]
    public static async Task<string> ArticlesEnRupture(
        SageDatabaseRegistry registry,
        [Description("Seuil de disponibilité à ne pas dépasser. Vide = utilise le stock mini de chaque article.")] double? seuil = null,
        [Description("Filtre dépôt (nom, recherche partielle). Vide = tous.")] string? depot = null,
        [Description("Nombre maximum d'articles (défaut 50).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var n = Math.Clamp(limite ?? 50, 1, 500);
        var prms = new Dictionary<string, object?>
        {
            ["@seuil"] = seuil.HasValue ? (decimal)seuil.Value : DBNull.Value
        };
        var depotFiltre = "";
        if (!string.IsNullOrWhiteSpace(depot)) { depotFiltre = " AND de.DE_Intitule LIKE @depot"; prms["@depot"] = $"%{depot.Trim()}%"; }

        var sql = $@"SELECT TOP ({n}) s.AR_Ref, MAX(a.AR_Design) AS Design, MAX(a.FA_CodeFamille) AS Famille,
                            SUM(s.AS_QteSto) AS QteSto, SUM(s.AS_QteRes) AS QteRes, MAX(s.AS_QteMini) AS Mini
                     FROM F_ARTSTOCK s
                     JOIN F_ARTICLE a ON a.AR_Ref = s.AR_Ref
                     LEFT JOIN F_DEPOT de ON de.DE_No = s.DE_No
                     WHERE a.AR_SuiviStock <> 0{depotFiltre}
                     GROUP BY s.AR_Ref
                     HAVING SUM(s.AS_QteSto - s.AS_QteRes)
                            <= CASE WHEN @seuil IS NOT NULL THEN @seuil ELSE MAX(s.AS_QteMini) END
                     ORDER BY (SUM(s.AS_QteSto - s.AS_QteRes)
                            - CASE WHEN @seuil IS NOT NULL THEN @seuil ELSE MAX(s.AS_QteMini) END) ASC";

        var rows = await registry.QueryAsync(base_sage, sql, prms, ct);
        if (rows.Count == 0)
            return "Aucun article en rupture ou sous le seuil. " +
                   "Vérifiez que les articles sont suivis en stock et qu'un stock mini est défini, ou précisez un seuil.";

        string Qte(object? v) => SageFormat.ToDecimal(v).ToString("N0", SageFormat.Fr);
        var table = SageFormat.Table(rows,
            ("Référence", r => SageFormat.Text(r["AR_Ref"])),
            ("Désignation", r => SageFormat.Text(r["Design"])),
            ("Famille", r => SageFormat.Text(r["Famille"])),
            ("Stock", r => Qte(r["QteSto"])),
            ("Réservé", r => Qte(r["QteRes"])),
            ("Disponible", r => Qte(SageFormat.ToDecimal(r["QteSto"]) - SageFormat.ToDecimal(r["QteRes"]))),
            ("Stock mini", r => Qte(r["Mini"])));

        var crit = seuil.HasValue
            ? $"disponible ≤ {seuil.Value.ToString("N0", SageFormat.Fr)}"
            : "disponible ≤ stock mini";
        return $"## Articles en rupture / sous le seuil ({crit})\n\n" + table +
               $"\n**{rows.Count} article(s)** à réapprovisionner.";
    }
}
