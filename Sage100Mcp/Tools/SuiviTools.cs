using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Sage100Mcp.Data;

namespace Sage100Mcp.Tools;

/// <summary>Outils de suivi : balance âgée fournisseurs, retards clients, détail facture, journal, stocks dormants.</summary>
[McpServerToolType]
public sealed class SuiviTools
{
    [McpServerTool(Name = "sage_balance_agee_fournisseurs")]
    [Description("Balance âgée fournisseurs : répartition des dettes fournisseurs non réglées par tranche d'échéance " +
                 "(non échu, 1-30 j, 31-60 j, 61-90 j, > 90 j). Comptes 401, écritures non lettrées.")]
    public static async Task<string> BalanceAgeeFournisseurs(
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
                    WHEN e.EC_Echeance IS NULL OR e.EC_Echeance < '19000101' THEN '5 - Sans échéance'
                    WHEN e.EC_Echeance >= @asOf THEN '0 - Non échu'
                    WHEN DATEDIFF(day, e.EC_Echeance, @asOf) <= 30 THEN '1 - 1 à 30 j'
                    WHEN DATEDIFF(day, e.EC_Echeance, @asOf) <= 60 THEN '2 - 31 à 60 j'
                    WHEN DATEDIFF(day, e.EC_Echeance, @asOf) <= 90 THEN '3 - 61 à 90 j'
                    ELSE '4 - plus de 90 j'
                  END AS Tranche,
                  CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE -e.EC_Montant END AS Montant
                FROM F_ECRITUREC e
                WHERE e.CG_Num LIKE '401%' AND (e.EC_Lettrage IS NULL OR e.EC_Lettrage = '') AND e.JM_Date <= @asOf
              ) t
              GROUP BY Tranche ORDER BY Tranche",
            new Dictionary<string, object?> { ["@asOf"] = asOf }, ct);

        if (rows.Count == 0) return $"Aucune dette fournisseur au {asOf:dd/MM/yyyy}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["Total"]));
        decimal enRetard = rows.Where(r => !SageFormat.Text(r["Tranche"]).Contains("Non échu")
                                        && !SageFormat.Text(r["Tranche"]).Contains("Sans échéance"))
                               .Sum(r => SageFormat.ToDecimal(r["Total"]));
        var table = SageFormat.Table(rows,
            ("Tranche", r => SageFormat.Text(r["Tranche"]).Substring(4)),
            ("Montant", r => SageFormat.Euro(r["Total"])),
            ("% du total", r => total == 0 ? "0 %" : (SageFormat.ToDecimal(r["Total"]) / total).ToString("P1", SageFormat.Fr)));

        return $"## Balance âgée fournisseurs au {asOf:dd/MM/yyyy}\n\n" + table +
               $"\n**Dettes totales : {SageFormat.Euro(total)}** — dont échues : {SageFormat.Euro(enRetard)} " +
               $"({(total == 0 ? "0 %" : (enRetard / total).ToString("P1", SageFormat.Fr))})";
    }

    [McpServerTool(Name = "sage_retards_clients")]
    [Description("Clients en retard de paiement : factures échues et non réglées (comptes 411, non lettrées, " +
                 "échéance dépassée), par client, avec montant en retard, échéance la plus ancienne et jours de retard maxi. " +
                 "Priorise le recouvrement.")]
    public static async Task<string> RetardsClients(
        SageDatabaseRegistry registry,
        [Description("Date d'arrêté AAAA-MM-JJ (vide = aujourd'hui).")] string? date_arrete = null,
        [Description("Nombre de clients affichés (défaut 30).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var asOf = SagePeriod.ParseDate(date_arrete, DateTime.Today);
        var n = Math.Clamp(limite ?? 30, 1, 300);
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) e.CT_Num, MAX(c.CT_Intitule) AS Intitule,
                      SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE -e.EC_Montant END) AS Retard,
                      MIN(e.EC_Echeance) AS PlusAncienne,
                      MAX(DATEDIFF(day, e.EC_Echeance, @asOf)) AS JoursMax
               FROM F_ECRITUREC e
               LEFT JOIN F_COMPTET c ON c.CT_Num = e.CT_Num
               WHERE e.CG_Num LIKE '411%' AND (e.EC_Lettrage IS NULL OR e.EC_Lettrage = '')
                 AND e.EC_Echeance IS NOT NULL AND e.EC_Echeance > '19000101' AND e.EC_Echeance < @asOf
                 AND e.CT_Num IS NOT NULL AND e.CT_Num <> ''
               GROUP BY e.CT_Num
               HAVING SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE -e.EC_Montant END) > 0.005
               ORDER BY Retard DESC",
            new Dictionary<string, object?> { ["@asOf"] = asOf }, ct);

        if (rows.Count == 0) return $"Aucun retard de paiement client au {asOf:dd/MM/yyyy}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["Retard"]));
        var table = SageFormat.Table(rows,
            ("Client", r => SageFormat.Text(r["CT_Num"])),
            ("Intitulé", r => SageFormat.Text(r["Intitule"])),
            ("Montant en retard", r => SageFormat.Euro(r["Retard"])),
            ("Échéance la + ancienne", r => SageFormat.Date(r["PlusAncienne"])),
            ("Jours de retard", r => $"{SageFormat.ToLong(r["JoursMax"])} j"));

        return $"## Clients en retard de paiement au {asOf:dd/MM/yyyy}\n\n" + table +
               $"\n**Total en retard : {SageFormat.Euro(total)}** ({rows.Count} clients)";
    }

    [McpServerTool(Name = "sage_detail_facture")]
    [Description("Détail d'une facture de vente : entête (client, dates, totaux) et lignes (articles, quantités, " +
                 "prix, montants). Recherche par numéro de pièce.")]
    public static async Task<string> DetailFacture(
        SageDatabaseRegistry registry,
        [Description("Numéro de pièce de la facture (ex. '729340').")] string piece,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var prm = new Dictionary<string, object?> { ["@p"] = piece.Trim() };
        var entetes = await registry.QueryAsync(base_sage,
            @"SELECT TOP 1 d.DO_Piece, d.DO_Type, d.DO_Date, d.DO_Ref, d.DO_Tiers, MAX(c.CT_Intitule) AS Intitule,
                     d.DO_TotalHT, d.DO_TotalTTC, d.DO_NetAPayer
              FROM F_DOCENTETE d LEFT JOIN F_COMPTET c ON c.CT_Num = d.DO_Tiers
              WHERE d.DO_Domaine = 0 AND d.DO_Type IN (6, 7) AND d.DO_Piece = @p
              GROUP BY d.DO_Piece, d.DO_Type, d.DO_Date, d.DO_Ref, d.DO_Tiers, d.DO_TotalHT, d.DO_TotalTTC, d.DO_NetAPayer
              ORDER BY d.DO_Type DESC", prm, ct);
        if (entetes.Count == 0) return $"Facture « {piece} » introuvable (ventes).";
        var h = entetes[0];

        var lignes = await registry.QueryAsync(base_sage,
            @"SELECT DL_Ligne, AR_Ref, DL_Design, DL_Qte, DL_PrixUnitaire, DL_MontantHT
              FROM F_DOCLIGNE
              WHERE DO_Domaine = 0 AND DO_Piece = @p AND DO_Type = @type
              ORDER BY DL_Ligne",
            new Dictionary<string, object?> { ["@p"] = piece.Trim(), ["@type"] = SageFormat.ToLong(h["DO_Type"]) }, ct);

        var sb = new StringBuilder($"## Facture {SageFormat.Text(h["DO_Piece"])}\n\n");
        sb.AppendLine($"- **Client** : {SageFormat.Text(h["DO_Tiers"])} — {SageFormat.Text(h["Intitule"])}");
        sb.AppendLine($"- **Date** : {SageFormat.Date(h["DO_Date"])}");
        if (!string.IsNullOrWhiteSpace(SageFormat.Text(h["DO_Ref"]))) sb.AppendLine($"- **Référence** : {SageFormat.Text(h["DO_Ref"])}");
        sb.AppendLine($"- **Total HT** : {SageFormat.Euro(h["DO_TotalHT"])} · **TTC** : {SageFormat.Euro(h["DO_TotalTTC"])} · **Net à payer** : {SageFormat.Euro(h["DO_NetAPayer"])}");
        sb.AppendLine();
        if (lignes.Count > 0)
        {
            sb.Append(SageFormat.Table(lignes,
                ("Réf.", r => SageFormat.Text(r["AR_Ref"])),
                ("Désignation", r => SageFormat.Text(r["DL_Design"])),
                ("Qté", r => SageFormat.ToDecimal(r["DL_Qte"]).ToString("N2", SageFormat.Fr)),
                ("PU HT", r => SageFormat.Euro(r["DL_PrixUnitaire"])),
                ("Montant HT", r => SageFormat.Euro(r["DL_MontantHT"]))));
            sb.AppendLine($"\n*{lignes.Count} ligne(s).*");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_journal_comptable")]
    [Description("Consultation d'un journal comptable : écritures d'un journal donné (achats, ventes, banque, OD…) " +
                 "sur une période, avec totaux débit/crédit. Utilise le code journal (voir sage_lister_journaux).")]
    public static async Task<string> JournalComptable(
        SageDatabaseRegistry registry,
        [Description("Code du journal (ex. 'VT', 'AC', 'BQ', 'OD').")] string journal,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nombre maximum de lignes (défaut 200).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var top = Math.Clamp(limite ?? 200, 1, 2000);
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({top}) e.JM_Date, e.EC_Piece, e.CG_Num, e.CT_Num, e.EC_Intitule, e.EC_Sens, e.EC_Montant
               FROM F_ECRITUREC e
               WHERE e.JO_Num = @jo AND e.JM_Date BETWEEN @from AND @to
               ORDER BY e.JM_Date, e.EC_No",
            new Dictionary<string, object?> { ["@jo"] = journal.Trim(), ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune écriture dans le journal {journal} {SagePeriod.Describe(from, to)}.";

        decimal totD = 0, totC = 0;
        foreach (var r in rows)
        {
            if (SageFormat.ToLong(r["EC_Sens"]) == 0) totD += SageFormat.ToDecimal(r["EC_Montant"]);
            else totC += SageFormat.ToDecimal(r["EC_Montant"]);
        }
        var table = SageFormat.Table(rows,
            ("Date", r => SageFormat.Date(r["JM_Date"])),
            ("Pièce", r => SageFormat.Text(r["EC_Piece"])),
            ("Compte", r => SageFormat.Text(r["CG_Num"])),
            ("Tiers", r => SageFormat.Text(r["CT_Num"])),
            ("Libellé", r => SageFormat.Text(r["EC_Intitule"])),
            ("Débit", r => SageFormat.ToLong(r["EC_Sens"]) == 0 ? SageFormat.Euro(r["EC_Montant"]) : ""),
            ("Crédit", r => SageFormat.ToLong(r["EC_Sens"]) == 1 ? SageFormat.Euro(r["EC_Montant"]) : ""));

        return $"## Journal {journal} {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total débit : {SageFormat.Euro(totD)} · Total crédit : {SageFormat.Euro(totC)}** ({rows.Count} lignes" +
               (rows.Count == top ? ", limite atteinte" : "") + ")";
    }

    [McpServerTool(Name = "sage_articles_dormants")]
    [Description("Articles en stock (quantité positive) n'ayant fait l'objet d'aucune vente depuis un certain nombre de mois " +
                 "(défaut 12) : stock immobilisé / surstock à écouler. Trié par valorisation puis quantité.")]
    public static async Task<string> ArticlesDormants(
        SageDatabaseRegistry registry,
        [Description("Nombre de mois sans vente (défaut 12).")] int? mois_sans_vente = null,
        [Description("Date de référence AAAA-MM-JJ (vide = aujourd'hui).")] string? date_reference = null,
        [Description("Nombre d'articles affichés (défaut 50).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var reference = SagePeriod.ParseDate(date_reference, DateTime.Today);
        var mois = Math.Clamp(mois_sans_vente ?? 12, 1, 240);
        var cutoff = reference.AddMonths(-mois);
        var n = Math.Clamp(limite ?? 50, 1, 300);

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) s.AR_Ref, MAX(a.AR_Design) AS Design, MAX(a.FA_CodeFamille) AS Famille,
                      SUM(s.AS_QteSto) AS Qte, SUM(s.AS_MontSto) AS Valo, MAX(v.DerniereVente) AS DerniereVente
               FROM F_ARTSTOCK s
               LEFT JOIN F_ARTICLE a ON a.AR_Ref = s.AR_Ref
               LEFT JOIN (SELECT AR_Ref, MAX(DO_Date) AS DerniereVente FROM F_DOCLIGNE
                          WHERE DO_Domaine = 0 AND DO_Type IN (6, 7) GROUP BY AR_Ref) v ON v.AR_Ref = s.AR_Ref
               GROUP BY s.AR_Ref
               HAVING SUM(s.AS_QteSto) > 0 AND (MAX(v.DerniereVente) < @cutoff OR MAX(v.DerniereVente) IS NULL)
               ORDER BY Valo DESC, Qte DESC",
            new Dictionary<string, object?> { ["@cutoff"] = cutoff }, ct);

        if (rows.Count == 0) return $"Aucun article dormant (sans vente depuis {mois} mois) au {reference:dd/MM/yyyy}.";

        decimal totQte = rows.Sum(r => SageFormat.ToDecimal(r["Qte"]));
        decimal totValo = rows.Sum(r => SageFormat.ToDecimal(r["Valo"]));
        var table = SageFormat.Table(rows,
            ("Référence", r => SageFormat.Text(r["AR_Ref"])),
            ("Désignation", r => SageFormat.Text(r["Design"])),
            ("Famille", r => SageFormat.Text(r["Famille"])),
            ("Qté stock", r => SageFormat.ToDecimal(r["Qte"]).ToString("N0", SageFormat.Fr)),
            ("Valorisation", r => SageFormat.Euro(r["Valo"])),
            ("Dernière vente", r => r["DerniereVente"] is null ? "jamais" : SageFormat.Date(r["DerniereVente"])));

        return $"## Articles dormants (aucune vente depuis {mois} mois, au {reference:dd/MM/yyyy})\n\n" + table +
               $"\n**{rows.Count} article(s)** · {totQte.ToString("N0", SageFormat.Fr)} unités immobilisées · " +
               $"valorisation {SageFormat.Euro(totValo)}";
    }
}
