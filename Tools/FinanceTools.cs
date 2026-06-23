using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Sage100Mcp.Data;

namespace Sage100Mcp.Tools;

/// <summary>Outils financiers : TVA, trésorerie, prévisionnel d'encaissements/décaissements.</summary>
[McpServerToolType]
public sealed class FinanceTools
{
    [McpServerTool(Name = "sage_tva")]
    [Description("Synthèse de TVA sur une période : TVA collectée (comptes 4457), TVA déductible (comptes 4456) " +
                 "et TVA nette à décaisser (ou crédit de TVA). Aide à la préparation de la déclaration.")]
    public static async Task<string> Tva(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var prm = new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to };

        var detail = await registry.QueryAsync(base_sage,
            @"SELECT e.CG_Num, MAX(g.CG_Intitule) AS Intitule,
                     SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE -e.EC_Montant END) AS SoldeCredit
              FROM F_ECRITUREC e
              LEFT JOIN F_COMPTEG g ON g.CG_Num = e.CG_Num
              WHERE (e.CG_Num LIKE '4457%' OR e.CG_Num LIKE '4456%') AND e.EC_Date BETWEEN @from AND @to
              GROUP BY e.CG_Num
              HAVING SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE -e.EC_Montant END) <> 0
              ORDER BY e.CG_Num", prm, ct);

        if (detail.Count == 0) return $"Aucun mouvement de TVA {SagePeriod.Describe(from, to)}.";

        decimal collectee = 0, deductible = 0;
        foreach (var r in detail)
        {
            var num = SageFormat.Text(r["CG_Num"]);
            var soldeCredit = SageFormat.ToDecimal(r["SoldeCredit"]);
            if (num.StartsWith("4457")) collectee += soldeCredit;      // collectée = solde créditeur
            else deductible += -soldeCredit;                            // déductible = solde débiteur
        }
        var nette = collectee - deductible;

        var sb = new StringBuilder($"## Synthèse TVA {SagePeriod.Describe(from, to)}\n\n");
        sb.AppendLine("| Élément | Montant |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| TVA collectée (4457) | {SageFormat.Euro(collectee)} |");
        sb.AppendLine($"| TVA déductible (4456) | {SageFormat.Euro(deductible)} |");
        sb.AppendLine(nette >= 0
            ? $"| **TVA à décaisser** | **{SageFormat.Euro(nette)}** |"
            : $"| **Crédit de TVA** | **{SageFormat.Euro(-nette)}** |");

        sb.AppendLine("\n### Détail par compte\n");
        sb.Append(SageFormat.Table(detail,
            ("Compte", r => SageFormat.Text(r["CG_Num"])),
            ("Intitulé", r => SageFormat.Text(r["Intitule"])),
            ("Montant", r =>
            {
                var num = SageFormat.Text(r["CG_Num"]);
                var sc = SageFormat.ToDecimal(r["SoldeCredit"]);
                return SageFormat.Euro(num.StartsWith("4457") ? sc : -sc);
            })));
        sb.AppendLine("\n*Indicatif : à recouper avec votre régime de TVA et les déclarations effectives.*");
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_tresorerie")]
    [Description("Position de trésorerie à une date : solde de chaque compte de banque et de caisse (classe 51/53) " +
                 "et trésorerie totale disponible. Cumul depuis l'origine jusqu'à la date d'arrêté.")]
    public static async Task<string> Tresorerie(
        SageDatabaseRegistry registry,
        [Description("Date d'arrêté AAAA-MM-JJ (vide = aujourd'hui).")] string? date_arrete = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var asOf = SagePeriod.ParseDate(date_arrete, DateTime.Today);
        var rows = await registry.QueryAsync(base_sage,
            @"SELECT e.CG_Num, MAX(g.CG_Intitule) AS Intitule,
                     SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE -e.EC_Montant END) AS Solde
              FROM F_ECRITUREC e
              LEFT JOIN F_COMPTEG g ON g.CG_Num = e.CG_Num
              WHERE (e.CG_Num LIKE '512%' OR e.CG_Num LIKE '514%' OR e.CG_Num LIKE '53%')
                AND e.EC_Date <= @asOf
              GROUP BY e.CG_Num
              HAVING SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE -e.EC_Montant END) <> 0
              ORDER BY Solde DESC",
            new Dictionary<string, object?> { ["@asOf"] = asOf }, ct);

        if (rows.Count == 0) return $"Aucun compte de trésorerie mouvementé au {asOf:dd/MM/yyyy}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["Solde"]));
        var table = SageFormat.Table(rows,
            ("Compte", r => SageFormat.Text(r["CG_Num"])),
            ("Banque / caisse", r => SageFormat.Text(r["Intitule"])),
            ("Solde", r => SageFormat.Euro(r["Solde"])));

        return $"## Position de trésorerie au {asOf:dd/MM/yyyy}\n\n" + table +
               $"\n**Trésorerie totale : {SageFormat.Euro(total)}** ({rows.Count} comptes)";
    }

    [McpServerTool(Name = "sage_previsionnel_encaissements")]
    [Description("Prévisionnel de trésorerie par mois d'échéance : encaissements attendus (factures clients non réglées, " +
                 "comptes 411) et décaissements prévus (factures fournisseurs non réglées, comptes 401), avec solde net " +
                 "et cumul. Basé sur les dates d'échéance des écritures non lettrées.")]
    public static async Task<string> PrevisionnelEncaissements(
        SageDatabaseRegistry registry,
        [Description("Échéances à partir de AAAA-MM-JJ (vide = toutes, y compris en retard).")] string? date_debut = null,
        [Description("Échéances jusqu'à AAAA-MM-JJ (vide = pas de limite).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var filtres = new List<string>
        {
            "(e.CG_Num LIKE '411%' OR e.CG_Num LIKE '401%')",
            "(e.EC_Lettrage IS NULL OR e.EC_Lettrage = '')",
            "e.EC_Echeance IS NOT NULL"
        };
        var prm = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(date_debut)) { filtres.Add("e.EC_Echeance >= @from"); prm["@from"] = SagePeriod.ParseDate(date_debut, DateTime.Today); }
        if (!string.IsNullOrWhiteSpace(date_fin)) { filtres.Add("e.EC_Echeance <= @to"); prm["@to"] = SagePeriod.ParseDate(date_fin, DateTime.Today); }

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT YEAR(e.EC_Echeance) AS Annee, MONTH(e.EC_Echeance) AS Mois,
                      SUM(CASE WHEN e.CG_Num LIKE '411%' THEN (CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE -e.EC_Montant END) ELSE 0 END) AS Encaissements,
                      SUM(CASE WHEN e.CG_Num LIKE '401%' THEN (CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE -e.EC_Montant END) ELSE 0 END) AS Decaissements
               FROM F_ECRITUREC e
               WHERE {string.Join(" AND ", filtres)}
               GROUP BY YEAR(e.EC_Echeance), MONTH(e.EC_Echeance)
               ORDER BY Annee, Mois", prm, ct);

        if (rows.Count == 0) return "Aucune échéance non réglée trouvée pour ces critères.";

        var sb = new StringBuilder("## Prévisionnel de trésorerie par mois d'échéance\n\n");
        sb.AppendLine("| Mois | Encaissements (clients) | Décaissements (fournisseurs) | Solde net | Cumul |");
        sb.AppendLine("|---|---|---|---|---|");
        decimal totEnc = 0, totDec = 0, cumul = 0;
        foreach (var r in rows)
        {
            var enc = SageFormat.ToDecimal(r["Encaissements"]);
            var dec = SageFormat.ToDecimal(r["Decaissements"]);
            var net = enc - dec;
            cumul += net; totEnc += enc; totDec += dec;
            var mois = $"{SageFormat.MonthsFr[(int)SageFormat.ToLong(r["Mois"])]} {SageFormat.ToLong(r["Annee"])}";
            sb.AppendLine($"| {mois} | {SageFormat.Euro(enc)} | {SageFormat.Euro(dec)} | {SageFormat.Euro(net)} | {SageFormat.Euro(cumul)} |");
        }
        sb.AppendLine($"\n**Total encaissements : {SageFormat.Euro(totEnc)}** · " +
                      $"**décaissements : {SageFormat.Euro(totDec)}** · " +
                      $"**solde net : {SageFormat.Euro(totEnc - totDec)}**");
        return sb.ToString();
    }
}
