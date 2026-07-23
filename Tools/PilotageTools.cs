using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Sage100Mcp.Data;

namespace Sage100Mcp.Tools;

/// <summary>Outils de pilotage avancé (dirigeant / commercial) : tendances, achats, commandes, clients.</summary>
[McpServerToolType]
public sealed class PilotageTools
{
    [McpServerTool(Name = "sage_evolution_ca_annuelle")]
    [Description("Évolution du chiffre d'affaires par année (comptes de classe 70) avec variation en % d'une année " +
                 "sur l'autre. Idéal pour suivre la croissance pluriannuelle.")]
    public static async Task<string> EvolutionCaAnnuelle(
        SageDatabaseRegistry registry,
        [Description("Première année (vide = il y a 4 ans).")] int? annee_debut = null,
        [Description("Dernière année (vide = année en cours).")] int? annee_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var a2 = annee_fin ?? DateTime.Today.Year;
        var a1 = annee_debut ?? a2 - 4;
        if (a1 > a2) (a1, a2) = (a2, a1);

        var rows = await registry.QueryAsync(base_sage,
            @"SELECT YEAR(JM_Date) AS An,
                     SUM(CASE WHEN EC_Sens = 1 THEN EC_Montant ELSE -EC_Montant END) AS CA
              FROM F_ECRITUREC
              WHERE CG_Num LIKE '70%' AND YEAR(JM_Date) BETWEEN @a1 AND @a2
              GROUP BY YEAR(JM_Date)
              ORDER BY An",
            new Dictionary<string, object?> { ["@a1"] = a1, ["@a2"] = a2 }, ct);

        if (rows.Count == 0) return $"Aucun chiffre d'affaires entre {a1} et {a2}.";

        var sb = new StringBuilder($"## Évolution du chiffre d'affaires {a1}–{a2}\n\n");
        sb.AppendLine("| Année | CA HT | Variation |");
        sb.AppendLine("|---|---|---|");
        decimal? prev = null;
        foreach (var r in rows)
        {
            var ca = SageFormat.ToDecimal(r["CA"]);
            string var = prev is null or 0
                ? "—"
                : ((ca - prev.Value) / Math.Abs(prev.Value)).ToString("+0.0 %;-0.0 %", SageFormat.Fr);
            sb.AppendLine($"| {SageFormat.ToLong(r["An"])} | {SageFormat.Euro(ca)} | {var} |");
            prev = ca;
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_ca_par_famille")]
    [Description("Chiffre d'affaires des ventes par famille d'articles (lignes de factures F_DOCLIGNE + F_ARTICLE) " +
                 "sur une période. Montants HT, quantités et part en %.")]
    public static async Task<string> CaParFamille(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nombre de familles (défaut 30).")] int? top = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var n = Math.Clamp(top ?? 30, 1, 200);
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) ISNULL(NULLIF(CAST(a.FA_CodeFamille AS varchar(50)), ''), '(sans famille)') AS Famille,
                      SUM(l.DL_MontantHT) AS CA_HT, SUM(l.DL_Qte) AS Qte,
                      COUNT(DISTINCT l.AR_Ref) AS NbArticles,
                      SUM(SUM(l.DL_MontantHT)) OVER () AS GrandTotal
               FROM F_DOCLIGNE l
               LEFT JOIN F_ARTICLE a ON a.AR_Ref = l.AR_Ref
               WHERE l.DO_Domaine = 0 AND l.DO_Type IN (6, 7) AND l.DO_Date BETWEEN @from AND @to
               GROUP BY ISNULL(NULLIF(CAST(a.FA_CodeFamille AS varchar(50)), ''), '(sans famille)')
               ORDER BY CA_HT DESC",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune vente {SagePeriod.Describe(from, to)}.";

        decimal total = SageFormat.ToDecimal(rows[0]["GrandTotal"]);
        var table = SageFormat.Table(rows,
            ("Famille", r => SageFormat.Text(r["Famille"])),
            ("Articles", r => SageFormat.ToLong(r["NbArticles"]).ToString()),
            ("Qté vendue", r => SageFormat.ToDecimal(r["Qte"]).ToString("N0", SageFormat.Fr)),
            ("CA HT", r => SageFormat.Euro(r["CA_HT"])),
            ("% du total", r => total == 0 ? "0 %"
                : (SageFormat.ToDecimal(r["CA_HT"]) / total).ToString("P1", SageFormat.Fr)));

        return $"## CA par famille d'articles {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total CA HT : {SageFormat.Euro(total)}** ({rows.Count} familles)";
    }

    [McpServerTool(Name = "sage_top_fournisseurs")]
    [Description("Top fournisseurs par montant facturé sur une période (mouvements comptables des comptes 401). " +
                 "Pour identifier la dépendance fournisseurs.")]
    public static async Task<string> TopFournisseurs(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nombre de fournisseurs (défaut 20).")] int? top = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var n = Math.Clamp(top ?? 20, 1, 200);
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) e.CT_Num, MAX(c.CT_Intitule) AS Intitule,
                      SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE -e.EC_Montant END) AS Achats,
                      COUNT(*) AS NbEcr,
                      SUM(SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE -e.EC_Montant END)) OVER () AS GrandTotal
               FROM F_ECRITUREC e
               LEFT JOIN F_COMPTET c ON c.CT_Num = e.CT_Num
               WHERE e.CG_Num LIKE '401%' AND e.JM_Date BETWEEN @from AND @to
                 AND e.CT_Num IS NOT NULL AND e.CT_Num <> ''
               GROUP BY e.CT_Num
               ORDER BY Achats DESC",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucun achat fournisseur {SagePeriod.Describe(from, to)}.";

        decimal total = SageFormat.ToDecimal(rows[0]["GrandTotal"]);
        var table = SageFormat.Table(rows,
            ("Fournisseur", r => SageFormat.Text(r["CT_Num"])),
            ("Intitulé", r => SageFormat.Text(r["Intitule"])),
            ("Achats HT", r => SageFormat.Euro(r["Achats"])),
            ("% du total", r => total == 0 ? "0 %"
                : (SageFormat.ToDecimal(r["Achats"]) / total).ToString("P1", SageFormat.Fr)));

        return $"## Top {rows.Count} fournisseurs {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total achats (comptes 401) : {SageFormat.Euro(total)}**";
    }

    [McpServerTool(Name = "sage_achats_par_mois")]
    [Description("Achats par mois (comptes de charges classe 60) sur une période. Suivi de l'évolution des achats.")]
    public static async Task<string> AchatsParMois(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var rows = await registry.QueryAsync(base_sage,
            @"SELECT YEAR(JM_Date) AS Annee, MONTH(JM_Date) AS Mois,
                     SUM(CASE WHEN EC_Sens = 0 THEN EC_Montant ELSE -EC_Montant END) AS Achats
              FROM F_ECRITUREC
              WHERE CG_Num LIKE '60%' AND JM_Date BETWEEN @from AND @to
              GROUP BY YEAR(JM_Date), MONTH(JM_Date)
              ORDER BY Annee, Mois",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucun achat (classe 60) {SagePeriod.Describe(from, to)}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["Achats"]));
        var table = SageFormat.Table(rows,
            ("Mois", r => $"{SageFormat.MonthsFr[(int)SageFormat.ToLong(r["Mois"])]} {SageFormat.ToLong(r["Annee"])}"),
            ("Achats HT", r => SageFormat.Euro(r["Achats"])));

        return $"## Achats par mois {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total achats : {SageFormat.Euro(total)}** — moyenne mensuelle : {SageFormat.Euro(total / Math.Max(1, rows.Count))}";
    }

    [McpServerTool(Name = "sage_commandes_clients_en_cours")]
    [Description("Carnet de commandes clients en cours (bons de commande de vente non clôturés, restant à livrer/facturer), " +
                 "regroupés par client. Indicateur d'activité à venir.")]
    public static async Task<string> CommandesClientsEnCours(
        SageDatabaseRegistry registry,
        [Description("Nombre de clients affichés (défaut 30).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var n = Math.Clamp(limite ?? 30, 1, 300);
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) d.DO_Tiers, MAX(c.CT_Intitule) AS Intitule,
                      COUNT(*) AS NbCmd, SUM(d.DO_TotalHT) AS MontantHT, MIN(d.DO_Date) AS PlusAncienne,
                      SUM(SUM(d.DO_TotalHT)) OVER () AS GrandTotal, SUM(COUNT(*)) OVER () AS GrandNb
               FROM F_DOCENTETE d
               LEFT JOIN F_COMPTET c ON c.CT_Num = d.DO_Tiers
               WHERE d.DO_Domaine = 0 AND d.DO_Type = 1 AND d.DO_Cloture = 0
               GROUP BY d.DO_Tiers
               ORDER BY MontantHT DESC",
            null, ct);

        if (rows.Count == 0) return "Aucune commande client en cours.";

        decimal total = SageFormat.ToDecimal(rows[0]["GrandTotal"]);
        long totalNb = SageFormat.ToLong(rows[0]["GrandNb"]);
        var table = SageFormat.Table(rows,
            ("Client", r => SageFormat.Text(r["DO_Tiers"])),
            ("Intitulé", r => SageFormat.Text(r["Intitule"])),
            ("Nb cmd", r => SageFormat.ToLong(r["NbCmd"]).ToString()),
            ("Montant HT", r => SageFormat.Euro(r["MontantHT"])),
            ("Plus ancienne", r => SageFormat.Date(r["PlusAncienne"])));

        return $"## Carnet de commandes clients en cours\n\n" + table +
               $"\n**Total carnet : {SageFormat.Euro(total)}** sur {totalNb} commandes ({rows.Count} clients affichés)";
    }

    [McpServerTool(Name = "sage_clients_inactifs")]
    [Description("Clients actifs n'ayant plus été facturés depuis un certain nombre de mois (défaut 6), " +
                 "triés par chiffre d'affaires historique décroissant — pour cibler les relances commerciales.")]
    public static async Task<string> ClientsInactifs(
        SageDatabaseRegistry registry,
        [Description("Nombre de mois d'inactivité (défaut 6).")] int? mois_inactivite = null,
        [Description("Date de référence AAAA-MM-JJ (vide = aujourd'hui).")] string? date_reference = null,
        [Description("Nombre de clients affichés (défaut 30).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var reference = SagePeriod.ParseDate(date_reference, DateTime.Today);
        var mois = Math.Clamp(mois_inactivite ?? 6, 1, 120);
        var cutoff = reference.AddMonths(-mois);
        var n = Math.Clamp(limite ?? 30, 1, 300);

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) c.CT_Num, c.CT_Intitule, c.CT_Ville,
                      MAX(d.DO_Date) AS Derniere, SUM(d.DO_TotalHT) AS CA_Total, COUNT(d.DO_Piece) AS NbFact
               FROM F_COMPTET c
               LEFT JOIN F_DOCENTETE d ON d.DO_Tiers = c.CT_Num AND d.DO_Domaine = 0 AND d.DO_Type IN (6, 7)
               WHERE c.CT_Type = 0 AND c.CT_Sommeil = 0
               GROUP BY c.CT_Num, c.CT_Intitule, c.CT_Ville
               HAVING MAX(d.DO_Date) < @cutoff OR MAX(d.DO_Date) IS NULL
               ORDER BY SUM(d.DO_TotalHT) DESC",
            new Dictionary<string, object?> { ["@cutoff"] = cutoff }, ct);

        if (rows.Count == 0) return $"Aucun client inactif depuis plus de {mois} mois (référence {reference:dd/MM/yyyy}).";

        var table = SageFormat.Table(rows,
            ("Client", r => SageFormat.Text(r["CT_Num"])),
            ("Intitulé", r => SageFormat.Text(r["CT_Intitule"])),
            ("Ville", r => SageFormat.Text(r["CT_Ville"])),
            ("Dernière facture", r => r["Derniere"] is null ? "jamais" : SageFormat.Date(r["Derniere"])),
            ("CA historique", r => SageFormat.Euro(r["CA_Total"])));

        return $"## Clients inactifs depuis plus de {mois} mois (au {reference:dd/MM/yyyy})\n\n" + table +
               $"\n**{rows.Count} client(s)** à relancer (triés par CA historique).";
    }

    [McpServerTool(Name = "sage_fiche_client")]
    [Description("Fiche synthétique 360° d'un client : coordonnées, CA facturé sur une période, encours non réglé, " +
                 "et les dernières factures. Vue complète pour préparer un rendez-vous ou un point client.")]
    public static async Task<string> FicheClient(
        SageDatabaseRegistry registry,
        [Description("Numéro de compte tiers du client (ex. 'C001').")] string tiers,
        [Description("Date de début AAAA-MM-JJ pour le CA (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ pour le CA (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var prm = new Dictionary<string, object?> { ["@t"] = tiers.Trim() };

        var head = await registry.QueryAsync(base_sage,
            @"SELECT CT_Num, CT_Intitule, CT_Adresse, CT_CodePostal, CT_Ville, CT_Pays,
                     CT_Telephone, CT_EMail, CT_Encours, CT_Sommeil
              FROM F_COMPTET WHERE CT_Num = @t", prm, ct);
        if (head.Count == 0) return $"Client « {tiers} » introuvable.";
        var h = head[0];

        var caRows = await registry.QueryAsync(base_sage,
            @"SELECT SUM(DO_TotalHT) AS CA, COUNT(*) AS NbFact
              FROM F_DOCENTETE
              WHERE DO_Domaine = 0 AND DO_Type IN (6, 7) AND DO_Tiers = @t AND DO_Date BETWEEN @from AND @to",
            new Dictionary<string, object?> { ["@t"] = tiers.Trim(), ["@from"] = from, ["@to"] = to }, ct);

        var encRows = await registry.QueryAsync(base_sage,
            @"SELECT SUM(CASE WHEN EC_Sens = 0 THEN EC_Montant ELSE -EC_Montant END) AS Encours
              FROM F_ECRITUREC
              WHERE CT_Num = @t AND CG_Num LIKE '411%' AND (EC_Lettrage IS NULL OR EC_Lettrage = '') AND JM_Date BETWEEN @from AND @to", new Dictionary<string, object?> { ["@t"] = tiers.Trim(), ["@from"] = from, ["@to"] = to }, ct);

        var dernieres = await registry.QueryAsync(base_sage,
            @"SELECT TOP 5 DO_Piece, DO_Date, DO_Ref, DO_TotalHT, DO_NetAPayer
              FROM F_DOCENTETE
              WHERE DO_Domaine = 0 AND DO_Type IN (6, 7) AND DO_Tiers = @t
              ORDER BY DO_Date DESC", prm, ct);

        var sb = new StringBuilder($"## Fiche client {SageFormat.Text(h["CT_Num"])} — {SageFormat.Text(h["CT_Intitule"])}\n\n");
        if (SageFormat.ToLong(h["CT_Sommeil"]) != 0) sb.AppendLine("> ⚠️ Client en sommeil\n");
        sb.AppendLine("**Coordonnées**");
        sb.AppendLine($"- {SageFormat.Text(h["CT_Adresse"])}, {SageFormat.Text(h["CT_CodePostal"])} {SageFormat.Text(h["CT_Ville"])} {SageFormat.Text(h["CT_Pays"])}".Trim());
        sb.AppendLine($"- Tél : {SageFormat.Text(h["CT_Telephone"])} · E-mail : {SageFormat.Text(h["CT_EMail"])}");
        sb.AppendLine();
        sb.AppendLine("**Indicateurs**");
        sb.AppendLine($"- CA facturé {SagePeriod.Describe(from, to)} : **{SageFormat.Euro(caRows[0]["CA"])}** ({SageFormat.ToLong(caRows[0]["NbFact"])} factures)");
        sb.AppendLine($"- Encours non réglé (comptes 411) : **{SageFormat.Euro(encRows[0]["Encours"])}**");
        sb.AppendLine($"- Plafond d'encours autorisé : {SageFormat.Euro(h["CT_Encours"])}");
        sb.AppendLine();
        if (dernieres.Count > 0)
        {
            sb.AppendLine("**Dernières factures**\n");
            sb.Append(SageFormat.Table(dernieres,
                ("Pièce", r => SageFormat.Text(r["DO_Piece"])),
                ("Date", r => SageFormat.Date(r["DO_Date"])),
                ("Référence", r => SageFormat.Text(r["DO_Ref"])),
                ("Total HT", r => SageFormat.Euro(r["DO_TotalHT"])),
                ("Net à payer", r => SageFormat.Euro(r["DO_NetAPayer"]))));
        }
        return sb.ToString();
    }
}
