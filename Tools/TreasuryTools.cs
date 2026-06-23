using System.ComponentModel;
using ModelContextProtocol.Server;
using Sage100Mcp.Data;

namespace Sage100Mcp.Tools;

/// <summary>Outils de trésorerie / créances : encours clients, dettes fournisseurs, balance âgée.</summary>
[McpServerToolType]
public sealed class TreasuryTools
{
    [McpServerTool(Name = "sage_encours_clients")]
    [Description("Encours clients : montants non lettrés (factures non réglées) par client, sur les comptes 411. " +
                 "Pour le dirigeant et le comptable : qui doit de l'argent et combien. Source : F_ECRITUREC non lettré.")]
    public static async Task<string> EncoursClients(
        SageDatabaseRegistry registry,
        [Description("Date d'arrêté AAAA-MM-JJ (vide = aujourd'hui).")] string? date_arrete = null,
        [Description("Nombre de clients affichés (défaut 30).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var asOf = SagePeriod.ParseDate(date_arrete, DateTime.Today);
        var top = Math.Clamp(limite ?? 30, 1, 500);
        return await OpenItemsAsync(registry, base_sage, "411", asOf, top, debiteur: true, ct,
            titre: "Encours clients (factures non réglées)");
    }

    [McpServerTool(Name = "sage_dettes_fournisseurs")]
    [Description("Dettes fournisseurs : montants non lettrés (factures fournisseurs à payer) par fournisseur, comptes 401. " +
                 "Source : F_ECRITUREC non lettré.")]
    public static async Task<string> DettesFournisseurs(
        SageDatabaseRegistry registry,
        [Description("Date d'arrêté AAAA-MM-JJ (vide = aujourd'hui).")] string? date_arrete = null,
        [Description("Nombre de fournisseurs affichés (défaut 30).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var asOf = SagePeriod.ParseDate(date_arrete, DateTime.Today);
        var top = Math.Clamp(limite ?? 30, 1, 500);
        return await OpenItemsAsync(registry, base_sage, "401", asOf, top, debiteur: false, ct,
            titre: "Dettes fournisseurs (à payer)");
    }

    private static async Task<string> OpenItemsAsync(
        SageDatabaseRegistry registry, string? baseSage, string racineCompte,
        DateTime asOf, int top, bool debiteur, CancellationToken ct, string titre)
    {
        // Solde signé : pour les clients on veut le débiteur (>0), pour les fournisseurs le créditeur.
        var rows = await registry.QueryAsync(baseSage,
            $@"SELECT TOP ({top}) e.CT_Num,
                      MAX(c.CT_Intitule) AS CT_Intitule,
                      SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE -e.EC_Montant END) AS Solde,
                      COUNT(*) AS NbLignes
               FROM F_ECRITUREC e
               LEFT JOIN F_COMPTET c ON c.CT_Num = e.CT_Num
               WHERE e.CG_Num LIKE @racine
                 AND (e.EC_Lettrage IS NULL OR e.EC_Lettrage = '')
                 AND e.EC_Date <= @asOf
                 AND e.CT_Num IS NOT NULL AND e.CT_Num <> ''
               GROUP BY e.CT_Num
               HAVING ABS(SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE -e.EC_Montant END)) > 0.005
               ORDER BY " + (debiteur ? "Solde DESC" : "Solde ASC"),
            new Dictionary<string, object?> { ["@racine"] = $"{racineCompte}%", ["@asOf"] = asOf }, ct);

        if (rows.Count == 0) return $"Aucun encours {titre.ToLowerInvariant()} au {asOf:dd/MM/yyyy}.";

        decimal total = rows.Sum(r => Math.Abs(SageFormat.ToDecimal(r["Solde"])));

        var table = SageFormat.Table(rows,
            ("Tiers", r => SageFormat.Text(r["CT_Num"])),
            ("Intitulé", r => SageFormat.Text(r["CT_Intitule"])),
            ("Lignes", r => SageFormat.ToLong(r["NbLignes"]).ToString()),
            ("Montant", r => SageFormat.Euro(Math.Abs(SageFormat.ToDecimal(r["Solde"])))));

        return $"## {titre} au {asOf:dd/MM/yyyy}\n\n" + table +
               $"\n**Total affiché : {SageFormat.Euro(total)}** (top {rows.Count})";
    }

    [McpServerTool(Name = "sage_balance_agee_clients")]
    [Description("Balance âgée clients : répartition de l'encours client non réglé par tranche de retard " +
                 "(non échu, 1-30 j, 31-60 j, 61-90 j, > 90 j) en fonction des dates d'échéance. " +
                 "Indicateur clé de risque pour le dirigeant. Source : F_ECRITUREC non lettré, comptes 411.")]
    public static async Task<string> BalanceAgeeClients(
        SageDatabaseRegistry registry,
        [Description("Date d'arrêté AAAA-MM-JJ (vide = aujourd'hui).")] string? date_arrete = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var asOf = SagePeriod.ParseDate(date_arrete, DateTime.Today);

        var rows = await registry.QueryAsync(base_sage,
            @"SELECT Tranche, SUM(Montant) AS Total, COUNT(*) AS NbLignes
              FROM (
                SELECT
                  CASE
                    WHEN e.EC_Echeance IS NULL THEN '5 - Sans échéance'
                    WHEN e.EC_Echeance >= @asOf THEN '0 - Non échu'
                    WHEN DATEDIFF(day, e.EC_Echeance, @asOf) <= 30 THEN '1 - 1 à 30 j'
                    WHEN DATEDIFF(day, e.EC_Echeance, @asOf) <= 60 THEN '2 - 31 à 60 j'
                    WHEN DATEDIFF(day, e.EC_Echeance, @asOf) <= 90 THEN '3 - 61 à 90 j'
                    ELSE '4 - plus de 90 j'
                  END AS Tranche,
                  CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE -e.EC_Montant END AS Montant
                FROM F_ECRITUREC e
                WHERE e.CG_Num LIKE '411%'
                  AND (e.EC_Lettrage IS NULL OR e.EC_Lettrage = '')
                  AND e.EC_Date <= @asOf
              ) t
              GROUP BY Tranche
              ORDER BY Tranche",
            new Dictionary<string, object?> { ["@asOf"] = asOf }, ct);

        if (rows.Count == 0) return $"Aucun encours client au {asOf:dd/MM/yyyy}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["Total"]));
        decimal enRetard = rows.Where(r => !SageFormat.Text(r["Tranche"]).Contains("Non échu")
                                        && !SageFormat.Text(r["Tranche"]).Contains("Sans échéance"))
                               .Sum(r => SageFormat.ToDecimal(r["Total"]));

        var table = SageFormat.Table(rows,
            ("Tranche", r => SageFormat.Text(r["Tranche"]).Substring(4)),
            ("Montant", r => SageFormat.Euro(r["Total"])),
            ("% du total", r => total == 0 ? "0 %"
                : (SageFormat.ToDecimal(r["Total"]) / total).ToString("P1", SageFormat.Fr)));

        return $"## Balance âgée clients au {asOf:dd/MM/yyyy}\n\n" + table +
               $"\n**Encours total : {SageFormat.Euro(total)}** — dont en retard : {SageFormat.Euro(enRetard)} " +
               $"({(total == 0 ? "0 %" : (enRetard / total).ToString("P1", SageFormat.Fr))})";
    }
}
