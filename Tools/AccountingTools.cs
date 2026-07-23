using System.ComponentModel;
using ModelContextProtocol.Server;
using Sage100Mcp.Data;

namespace Sage100Mcp.Tools;

/// <summary>Outils pour le comptable : balance générale, grand livre, grand livre tiers.</summary>
[McpServerToolType]
public sealed class AccountingTools
{
    [McpServerTool(Name = "sage_balance_generale")]
    [Description("Balance comptable générale par compte sur une période : total débit, total crédit et solde. " +
                 "Permet de filtrer sur une plage de comptes (ex. de '60000000' à '69999999' pour les charges). " +
                 "Source : écritures F_ECRITUREC.")]
    public static async Task<string> BalanceGenerale(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Compte de début (ex. '6' ou '60000000'). Vide = tous.")] string? compte_debut = null,
        [Description("Compte de fin (ex. '6zzzzzzz'). Vide = tous.")] string? compte_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var accStart = string.IsNullOrWhiteSpace(compte_debut) ? "0" : compte_debut.Trim();
        var accEnd = string.IsNullOrWhiteSpace(compte_fin) ? "zzzzzzzzzzzzzzzz" : compte_fin.Trim();

        var rows = await registry.QueryAsync(base_sage,
            @"SELECT e.CG_Num,
                     MAX(g.CG_Intitule) AS CG_Intitule,
                     SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END) AS Debit,
                     SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END) AS Credit
              FROM F_ECRITUREC e
              LEFT JOIN F_COMPTEG g ON g.CG_Num = e.CG_Num
              WHERE e.JM_Date BETWEEN @from AND @to
                AND e.CG_Num BETWEEN @accStart AND @accEnd
              GROUP BY e.CG_Num
              HAVING SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END) <> 0
                  OR SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END) <> 0
              ORDER BY e.CG_Num",
            new Dictionary<string, object?>
            {
                ["@from"] = from, ["@to"] = to, ["@accStart"] = accStart, ["@accEnd"] = accEnd
            }, ct);

        if (rows.Count == 0) return $"Aucune écriture {SagePeriod.Describe(from, to)} sur cette plage de comptes.";

        decimal totDebit = 0, totCredit = 0;
        foreach (var r in rows) { totDebit += SageFormat.ToDecimal(r["Debit"]); totCredit += SageFormat.ToDecimal(r["Credit"]); }

        var table = SageFormat.Table(rows,
            ("Compte", r => SageFormat.Text(r["CG_Num"])),
            ("Intitulé", r => SageFormat.Text(r["CG_Intitule"])),
            ("Débit", r => SageFormat.Euro(r["Debit"])),
            ("Crédit", r => SageFormat.Euro(r["Credit"])),
            ("Solde", r => SageFormat.Euro(SageFormat.ToDecimal(r["Debit"]) - SageFormat.ToDecimal(r["Credit"]))));

        return $"## Balance générale {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Totaux** — Débit : {SageFormat.Euro(totDebit)} · Crédit : {SageFormat.Euro(totCredit)} · " +
               $"Solde : {SageFormat.Euro(totDebit - totCredit)} ({rows.Count} comptes)";
    }

    [McpServerTool(Name = "sage_grand_livre_compte")]
    [Description("Grand livre d'un compte général : détail chronologique des écritures (date, journal, pièce, libellé, débit/crédit) " +
                 "avec solde progressif. Source : F_ECRITUREC.")]
    public static async Task<string> GrandLivreCompte(
        SageDatabaseRegistry registry,
        [Description("Numéro de compte général exact ou préfixe (ex. '512' pour la banque).")] string compte,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        [Description("Nombre maximum de lignes (défaut 200).")] int? limite = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var top = Math.Clamp(limite ?? 200, 1, 2000);

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({top}) e.JM_Date, e.JO_Num, e.EC_Piece, e.EC_Intitule, e.CT_Num, e.EC_Sens, e.EC_Montant
               FROM F_ECRITUREC e
               WHERE e.CG_Num LIKE @compte AND e.JM_Date BETWEEN @from AND @to
               ORDER BY e.JM_Date, e.EC_No",
            new Dictionary<string, object?>
            {
                ["@compte"] = $"{compte.Trim()}%", ["@from"] = from, ["@to"] = to
            }, ct);

        if (rows.Count == 0) return $"Aucune écriture sur le compte {compte} {SagePeriod.Describe(from, to)}.";

        decimal solde = 0;
        var enriched = rows.Select(r =>
        {
            var debit = SageFormat.ToLong(r["EC_Sens"]) == 0 ? SageFormat.ToDecimal(r["EC_Montant"]) : 0m;
            var credit = SageFormat.ToLong(r["EC_Sens"]) == 1 ? SageFormat.ToDecimal(r["EC_Montant"]) : 0m;
            solde += debit - credit;
            var d = new Dictionary<string, object?>(r) { ["_Debit"] = debit, ["_Credit"] = credit, ["_Solde"] = solde };
            return (IReadOnlyDictionary<string, object?>)d;
        }).ToList();

        var table = SageFormat.Table(enriched,
            ("Date", r => SageFormat.Date(r["JM_Date"])),
            ("Jnl", r => SageFormat.Text(r["JO_Num"])),
            ("Pièce", r => SageFormat.Text(r["EC_Piece"])),
            ("Libellé", r => SageFormat.Text(r["EC_Intitule"])),
            ("Tiers", r => SageFormat.Text(r["CT_Num"])),
            ("Débit", r => SageFormat.Euro(r["_Debit"])),
            ("Crédit", r => SageFormat.Euro(r["_Credit"])),
            ("Solde", r => SageFormat.Euro(r["_Solde"])));

        return $"## Grand livre compte {compte} {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Solde de période : {SageFormat.Euro(solde)}** ({rows.Count} lignes" +
               (rows.Count == top ? ", limite atteinte — affinez la période" : "") + ")";
    }

    [McpServerTool(Name = "sage_grand_livre_tiers")]
    [Description("Grand livre d'un tiers (client ou fournisseur) : détail des écritures et solde. " +
                 "Indique combien le client doit (solde débiteur) ou ce qu'on doit au fournisseur (solde créditeur).")]
    public static async Task<string> GrandLivreTiers(
        SageDatabaseRegistry registry,
        [Description("Numéro de compte tiers (ex. 'C001', 'FORD').")] string tiers,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        [Description("Nombre maximum de lignes (défaut 200).")] int? limite = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var top = Math.Clamp(limite ?? 200, 1, 2000);

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({top}) e.JM_Date, e.JO_Num, e.EC_Piece, e.EC_Intitule, e.EC_Echeance,
                      e.EC_Lettrage, e.EC_Sens, e.EC_Montant
               FROM F_ECRITUREC e
               WHERE e.CT_Num = @tiers AND e.JM_Date BETWEEN @from AND @to
               ORDER BY e.JM_Date, e.EC_No",
            new Dictionary<string, object?>
            {
                ["@tiers"] = tiers.Trim(), ["@from"] = from, ["@to"] = to
            }, ct);

        if (rows.Count == 0) return $"Aucune écriture pour le tiers {tiers} {SagePeriod.Describe(from, to)}.";

        decimal solde = 0;
        var enriched = rows.Select(r =>
        {
            var debit = SageFormat.ToLong(r["EC_Sens"]) == 0 ? SageFormat.ToDecimal(r["EC_Montant"]) : 0m;
            var credit = SageFormat.ToLong(r["EC_Sens"]) == 1 ? SageFormat.ToDecimal(r["EC_Montant"]) : 0m;
            solde += debit - credit;
            var d = new Dictionary<string, object?>(r) { ["_Debit"] = debit, ["_Credit"] = credit, ["_Solde"] = solde };
            return (IReadOnlyDictionary<string, object?>)d;
        }).ToList();

        var table = SageFormat.Table(enriched,
            ("Date", r => SageFormat.Date(r["JM_Date"])),
            ("Jnl", r => SageFormat.Text(r["JO_Num"])),
            ("Pièce", r => SageFormat.Text(r["EC_Piece"])),
            ("Libellé", r => SageFormat.Text(r["EC_Intitule"])),
            ("Échéance", r => SageFormat.Date(r["EC_Echeance"])),
            ("Lettré", r => string.IsNullOrWhiteSpace(SageFormat.Text(r["EC_Lettrage"])) ? "" : "✓"),
            ("Débit", r => SageFormat.Euro(r["_Debit"])),
            ("Crédit", r => SageFormat.Euro(r["_Credit"])),
            ("Solde", r => SageFormat.Euro(r["_Solde"])));

        var sens = solde >= 0 ? "débiteur (le tiers doit)" : "créditeur (dû au tiers)";
        return $"## Grand livre tiers {tiers} {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Solde : {SageFormat.Euro(solde)}** — {sens} ({rows.Count} lignes" +
               (rows.Count == top ? ", limite atteinte" : "") + ")";
    }
}
