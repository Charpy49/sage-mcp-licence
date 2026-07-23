using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Sage100Mcp.Data;

namespace Sage100Mcp.Tools;

/// <summary>Outils d'analyse pour le dirigeant : ABC, comparatifs, acquisition, panier, ratios.</summary>
[McpServerToolType]
public sealed class AnalyseTools
{
    [McpServerTool(Name = "sage_analyse_abc_clients")]
    [Description("Analyse ABC (loi de Pareto 20/80) du chiffre d'affaires clients sur une période : segmente les clients " +
                 "en A (jusqu'à 80 % du CA), B (80–95 %) et C (95–100 %). Mesure la concentration et le risque de dépendance.")]
    public static async Task<string> AnalyseAbcClients(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var rows = await registry.QueryAsync(base_sage,
            @"SELECT d.DO_Tiers, MAX(c.CT_Intitule) AS Intitule, SUM(d.DO_TotalHT) AS CA
              FROM F_DOCENTETE d
              LEFT JOIN F_COMPTET c ON c.CT_Num = d.DO_Tiers
              WHERE d.DO_Domaine = 0 AND d.DO_Type IN (6, 7) AND d.DO_Date BETWEEN @from AND @to
              GROUP BY d.DO_Tiers
              HAVING SUM(d.DO_TotalHT) > 0
              ORDER BY CA DESC",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune vente {SagePeriod.Describe(from, to)}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["CA"]));
        int nbA = 0, nbB = 0, nbC = 0;
        decimal caA = 0, caB = 0, caC = 0, cumul = 0;
        var topA = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in rows)
        {
            var ca = SageFormat.ToDecimal(r["CA"]);
            var pctAvant = total == 0 ? 0 : cumul / total;
            cumul += ca;
            if (pctAvant < 0.80m) { nbA++; caA += ca; if (topA.Count < 5) topA.Add(r); }
            else if (pctAvant < 0.95m) { nbB++; caB += ca; }
            else { nbC++; caC += ca; }
        }

        string Pct(decimal v) => total == 0 ? "0 %" : (v / total).ToString("P1", SageFormat.Fr);
        string PctN(int nb) => rows.Count == 0 ? "0 %" : ((decimal)nb / rows.Count).ToString("P1", SageFormat.Fr);

        var sb = new StringBuilder($"## Analyse ABC clients {SagePeriod.Describe(from, to)}\n\n");
        sb.AppendLine("| Segment | Clients | % clients | CA HT | % CA |");
        sb.AppendLine("|---|---|---|---|---|");
        sb.AppendLine($"| **A** (0–80 % du CA) | {nbA} | {PctN(nbA)} | {SageFormat.Euro(caA)} | {Pct(caA)} |");
        sb.AppendLine($"| **B** (80–95 %) | {nbB} | {PctN(nbB)} | {SageFormat.Euro(caB)} | {Pct(caB)} |");
        sb.AppendLine($"| **C** (95–100 %) | {nbC} | {PctN(nbC)} | {SageFormat.Euro(caC)} | {Pct(caC)} |");
        sb.AppendLine($"| **Total** | {rows.Count} | 100 % | {SageFormat.Euro(total)} | 100 % |");
        sb.AppendLine($"\n*{nbA} clients (segment A) représentent {Pct(caA)} du chiffre d'affaires.*\n");
        sb.AppendLine("**Principaux clients (segment A)**\n");
        sb.Append(SageFormat.Table(topA,
            ("Client", r => SageFormat.Text(r["DO_Tiers"])),
            ("Intitulé", r => SageFormat.Text(r["Intitule"])),
            ("CA HT", r => SageFormat.Euro(r["CA"])),
            ("% CA", r => Pct(SageFormat.ToDecimal(r["CA"])))));
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_comparatif_ventes")]
    [Description("Comparaison des ventes facturées entre deux années, mois par mois, avec variation en %. " +
                 "Pour analyser la progression (année en cours vs année précédente).")]
    public static async Task<string> ComparatifVentes(
        SageDatabaseRegistry registry,
        [Description("Année de référence (la plus ancienne). Vide = année précédente.")] int? annee1 = null,
        [Description("Année à comparer (la plus récente). Vide = année en cours.")] int? annee2 = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var a2 = annee2 ?? DateTime.Today.Year;
        var a1 = annee1 ?? a2 - 1;

        var rows = await registry.QueryAsync(base_sage,
            @"SELECT MONTH(DO_Date) AS Mois,
                     SUM(CASE WHEN YEAR(DO_Date) = @a1 THEN DO_TotalHT ELSE 0 END) AS CA1,
                     SUM(CASE WHEN YEAR(DO_Date) = @a2 THEN DO_TotalHT ELSE 0 END) AS CA2
              FROM F_DOCENTETE
              WHERE DO_Domaine = 0 AND DO_Type IN (6, 7) AND YEAR(DO_Date) IN (@a1, @a2)
              GROUP BY MONTH(DO_Date)
              ORDER BY Mois",
            new Dictionary<string, object?> { ["@a1"] = a1, ["@a2"] = a2 }, ct);

        if (rows.Count == 0) return $"Aucune vente en {a1} ni en {a2}.";

        string Var(decimal v1, decimal v2) => v1 == 0 ? (v2 == 0 ? "—" : "nouveau")
            : ((v2 - v1) / Math.Abs(v1)).ToString("+0.0 %;-0.0 %", SageFormat.Fr);

        var sb = new StringBuilder($"## Comparatif des ventes {a1} vs {a2}\n\n");
        sb.AppendLine($"| Mois | {a1} | {a2} | Variation |");
        sb.AppendLine("|---|---|---|---|");
        decimal t1 = 0, t2 = 0;
        foreach (var r in rows)
        {
            var v1 = SageFormat.ToDecimal(r["CA1"]);
            var v2 = SageFormat.ToDecimal(r["CA2"]);
            t1 += v1; t2 += v2;
            sb.AppendLine($"| {SageFormat.MonthsFr[(int)SageFormat.ToLong(r["Mois"])]} | {SageFormat.Euro(v1)} | {SageFormat.Euro(v2)} | {Var(v1, v2)} |");
        }
        sb.AppendLine($"| **Total** | **{SageFormat.Euro(t1)}** | **{SageFormat.Euro(t2)}** | **{Var(t1, t2)}** |");
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_nouveaux_clients")]
    [Description("Clients nouvellement acquis sur une période : ceux dont la toute première facture tombe dans l'intervalle. " +
                 "Indicateur de conquête commerciale. Montre le CA généré depuis l'acquisition.")]
    public static async Task<string> NouveauxClients(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nombre de clients affichés (défaut 50).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var n = Math.Clamp(limite ?? 50, 1, 300);
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) c.CT_Num, c.CT_Intitule, c.CT_Ville, MIN(d.DO_Date) AS Premiere,
                      SUM(CASE WHEN d.DO_Date BETWEEN @from AND @to THEN d.DO_TotalHT ELSE 0 END) AS CA_Periode
               FROM F_DOCENTETE d
               JOIN F_COMPTET c ON c.CT_Num = d.DO_Tiers
               WHERE d.DO_Domaine = 0 AND d.DO_Type IN (6, 7)
               GROUP BY c.CT_Num, c.CT_Intitule, c.CT_Ville
               HAVING MIN(d.DO_Date) BETWEEN @from AND @to
               ORDER BY CA_Periode DESC",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucun nouveau client {SagePeriod.Describe(from, to)}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["CA_Periode"]));
        var table = SageFormat.Table(rows,
            ("Client", r => SageFormat.Text(r["CT_Num"])),
            ("Intitulé", r => SageFormat.Text(r["CT_Intitule"])),
            ("Ville", r => SageFormat.Text(r["CT_Ville"])),
            ("1ʳᵉ facture", r => SageFormat.Date(r["Premiere"])),
            ("CA généré", r => SageFormat.Euro(r["CA_Periode"])));

        return $"## Nouveaux clients {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**{rows.Count} nouveau(x) client(s)** — CA généré : {SageFormat.Euro(total)}";
    }

    [McpServerTool(Name = "sage_panier_moyen")]
    [Description("Panier moyen (ticket moyen) par mois : chiffre d'affaires facturé divisé par le nombre de factures. " +
                 "Suivi de la valeur moyenne des ventes dans le temps.")]
    public static async Task<string> PanierMoyen(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var rows = await registry.QueryAsync(base_sage,
            @"SELECT YEAR(DO_Date) AS Annee, MONTH(DO_Date) AS Mois, SUM(DO_TotalHT) AS CA, COUNT(*) AS Nb
              FROM F_DOCENTETE
              WHERE DO_Domaine = 0 AND DO_Type IN (6, 7) AND DO_Date BETWEEN @from AND @to
              GROUP BY YEAR(DO_Date), MONTH(DO_Date)
              ORDER BY Annee, Mois",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune facture {SagePeriod.Describe(from, to)}.";

        decimal totCa = rows.Sum(r => SageFormat.ToDecimal(r["CA"]));
        long totNb = rows.Sum(r => SageFormat.ToLong(r["Nb"]));
        var table = SageFormat.Table(rows,
            ("Mois", r => $"{SageFormat.MonthsFr[(int)SageFormat.ToLong(r["Mois"])]} {SageFormat.ToLong(r["Annee"])}"),
            ("Factures", r => SageFormat.ToLong(r["Nb"]).ToString()),
            ("CA HT", r => SageFormat.Euro(r["CA"])),
            ("Panier moyen", r =>
            {
                var nb = SageFormat.ToLong(r["Nb"]);
                return nb == 0 ? "—" : SageFormat.Euro(SageFormat.ToDecimal(r["CA"]) / nb);
            }));

        var panierGlobal = totNb == 0 ? 0 : totCa / totNb;
        return $"## Panier moyen par mois {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Panier moyen global : {SageFormat.Euro(panierGlobal)}** ({totNb} factures · CA {SageFormat.Euro(totCa)})";
    }

    [McpServerTool(Name = "sage_ratios_pilotage")]
    [Description("Ratios clés de pilotage sur une période : taux de marge (résultat/produits), poids des charges, " +
                 "délai moyen de paiement clients (DSO) et fournisseurs (DPO), à partir de la comptabilité. " +
                 "Vue synthétique de la performance et du besoin en fonds de roulement.")]
    public static async Task<string> RatiosPilotage(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var gestion = await registry.QueryAsync(base_sage,
            @"SELECT
                SUM(CASE WHEN LEFT(CG_Num,1)='7' THEN (CASE WHEN EC_Sens=1 THEN EC_Montant ELSE -EC_Montant END) ELSE 0 END) AS Produits,
                SUM(CASE WHEN LEFT(CG_Num,1)='6' THEN (CASE WHEN EC_Sens=0 THEN EC_Montant ELSE -EC_Montant END) ELSE 0 END) AS Charges,
                SUM(CASE WHEN CG_Num LIKE '60%' THEN (CASE WHEN EC_Sens=0 THEN EC_Montant ELSE -EC_Montant END) ELSE 0 END) AS Achats
              FROM F_ECRITUREC WHERE JM_Date BETWEEN @from AND @to",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);
        var encours = await registry.QueryAsync(base_sage,
            @"SELECT
                SUM(CASE WHEN CG_Num LIKE '411%' THEN (CASE WHEN EC_Sens=0 THEN EC_Montant ELSE -EC_Montant END) ELSE 0 END) AS Clients,
                SUM(CASE WHEN CG_Num LIKE '401%' THEN (CASE WHEN EC_Sens=1 THEN EC_Montant ELSE -EC_Montant END) ELSE 0 END) AS Fournisseurs
              FROM F_ECRITUREC
              WHERE (CG_Num LIKE '411%' OR CG_Num LIKE '401%') AND (EC_Lettrage IS NULL OR EC_Lettrage='') AND JM_Date BETWEEN @from AND @to",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        var produits = SageFormat.ToDecimal(gestion[0]["Produits"]);
        var charges = SageFormat.ToDecimal(gestion[0]["Charges"]);
        var achats = SageFormat.ToDecimal(gestion[0]["Achats"]);
        var resultat = produits - charges;
        var clients = SageFormat.ToDecimal(encours[0]["Clients"]);
        var fournisseurs = SageFormat.ToDecimal(encours[0]["Fournisseurs"]);
        var jours = Math.Max(1, (to.Date - from.Date).Days + 1);

        string Pct(decimal num, decimal den) => den == 0 ? "n/a" : (num / den).ToString("P1", SageFormat.Fr);
        string Jours(decimal num, decimal den) => den == 0 ? "n/a" : $"{(num / den * jours):N0} j".Replace(" ", " ");

        var sb = new StringBuilder($"## Ratios de pilotage {SagePeriod.Describe(from, to)}\n\n");
        sb.AppendLine("| Indicateur | Valeur |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Produits (classe 7) | {SageFormat.Euro(produits)} |");
        sb.AppendLine($"| Charges (classe 6) | {SageFormat.Euro(charges)} |");
        sb.AppendLine($"| Résultat | {SageFormat.Euro(resultat)} |");
        sb.AppendLine($"| Taux de marge (résultat/produits) | {Pct(resultat, produits)} |");
        sb.AppendLine($"| Poids des charges (charges/produits) | {Pct(charges, produits)} |");
        sb.AppendLine($"| Encours clients | {SageFormat.Euro(clients)} |");
        sb.AppendLine($"| DSO – délai moyen paiement clients | {Jours(clients, produits)} |");
        sb.AppendLine($"| Dettes fournisseurs | {SageFormat.Euro(fournisseurs)} |");
        sb.AppendLine($"| DPO – délai moyen paiement fournisseurs | {Jours(fournisseurs, achats)} |");
        sb.AppendLine("\n*DSO/DPO calculés en HT sur la période (approximation ; la norme utilise le TTC).*");
        return sb.ToString();
    }
}
