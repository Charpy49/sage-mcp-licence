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

    [McpServerTool(Name = "sage_devis_clients_en_cours")]
    [Description("Devis clients en cours (documents de vente DO_Type = 0 au statut « en cours », donc ni acceptés, " +
                 "ni refusés, ni transformés en commande/facture), regroupés par client. " +
                 "Pipeline commercial à convertir : montant potentiel, nombre de devis et ancienneté pour les relances.")]
    public static async Task<string> DevisClientsEnCours(
        SageDatabaseRegistry registry,
        [Description("Nombre de clients affichés (défaut 30).")] int? limite = null,
        [Description("Date d'émission minimale AAAA-MM-JJ (vide = tous les devis en cours, sans limite d'ancienneté).")] string? date_debut = null,
        [Description("Date d'émission maximale AAAA-MM-JJ (vide = pas de limite).")] string? date_fin = null,
        [Description("Ancienneté en mois au-delà de laquelle un devis est signalé comme à relancer (défaut 3).")] int? mois_relance = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var n = Math.Clamp(limite ?? 30, 1, 300);
        var mois = Math.Clamp(mois_relance ?? 3, 1, 120);
        DateTime? from = string.IsNullOrWhiteSpace(date_debut) ? null : SagePeriod.ParseDate(date_debut, DateTime.Today);
        DateTime? to = string.IsNullOrWhiteSpace(date_fin)
            ? null
            : SagePeriod.ParseDate(date_fin, DateTime.Today).AddDays(1).AddTicks(-1);
        var seuil = DateTime.Today.AddMonths(-mois);

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) d.DO_Tiers, MAX(c.CT_Intitule) AS Intitule,
                      COUNT(*) AS NbDevis, SUM(d.DO_TotalHT) AS MontantHT,
                      MIN(d.DO_Date) AS PlusAncien, MAX(d.DO_Date) AS PlusRecent,
                      SUM(CASE WHEN d.DO_Date < @seuil THEN 1 ELSE 0 END) AS NbARelancer,
                      SUM(SUM(d.DO_TotalHT)) OVER () AS GrandTotal, SUM(COUNT(*)) OVER () AS GrandNb,
                      SUM(SUM(CASE WHEN d.DO_Date < @seuil THEN d.DO_TotalHT ELSE 0 END)) OVER () AS GrandTotalARelancer,
                      SUM(SUM(CASE WHEN d.DO_Date < @seuil THEN 1 ELSE 0 END)) OVER () AS GrandNbARelancer
               FROM F_DOCENTETE d
               LEFT JOIN F_COMPTET c ON c.CT_Num = d.DO_Tiers
               WHERE d.DO_Domaine = 0 AND d.DO_Type = 0 AND d.DO_Statut = 0 AND d.DO_Cloture = 0
                 AND (@from IS NULL OR d.DO_Date >= @from)
                 AND (@to IS NULL OR d.DO_Date <= @to)
               GROUP BY d.DO_Tiers
               ORDER BY MontantHT DESC",
            new Dictionary<string, object?> { ["@seuil"] = seuil, ["@from"] = from, ["@to"] = to }, ct);

        var periode = from is null && to is null
            ? ""
            : from is null ? $" émis jusqu'au {to:dd/MM/yyyy}"
            : to is null ? $" émis depuis le {from:dd/MM/yyyy}"
            : $" émis {SagePeriod.Describe(from.Value, to.Value)}";

        if (rows.Count == 0) return $"Aucun devis client en cours{periode}.";

        decimal total = SageFormat.ToDecimal(rows[0]["GrandTotal"]);
        long totalNb = SageFormat.ToLong(rows[0]["GrandNb"]);
        decimal totalRelance = SageFormat.ToDecimal(rows[0]["GrandTotalARelancer"]);
        long totalNbRelance = SageFormat.ToLong(rows[0]["GrandNbARelancer"]);

        var table = SageFormat.Table(rows,
            ("Client", r => SageFormat.Text(r["DO_Tiers"])),
            ("Intitulé", r => SageFormat.Text(r["Intitule"])),
            ("Nb devis", r => SageFormat.ToLong(r["NbDevis"]).ToString()),
            ("Montant HT", r => SageFormat.Euro(r["MontantHT"])),
            ("Plus ancien", r => SageFormat.Date(r["PlusAncien"])),
            ("Plus récent", r => SageFormat.Date(r["PlusRecent"])),
            ($"Dont > {mois} mois", r => SageFormat.ToLong(r["NbARelancer"]).ToString()));

        return $"## Devis clients en cours{periode}\n\n" + table +
               $"\n**Total potentiel : {SageFormat.Euro(total)}** sur {totalNb} devis ({rows.Count} clients affichés)" +
               $"\n**À relancer (plus de {mois} mois) : {totalNbRelance} devis pour {SageFormat.Euro(totalRelance)}**";
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

    [McpServerTool(Name = "sage_commandes_clients_detail")]
    [Description("Détail ligne à ligne des commandes clients (F_DOCENTETE + F_DOCLIGNE) : numéro de pièce, date de " +
                 "commande, date de livraison prévue, client, statut, article, quantité et montant HT. " +
                 "Complète sage_commandes_clients_en_cours, qui ne renvoie qu'un total par client. " +
                 "Filtrable par client, par pièce, par période de commande et par date de livraison prévue.")]
    public static async Task<string> CommandesClientsDetail(
        SageDatabaseRegistry registry,
        [Description("Numéro de compte client (ex. 'GC001232'). Vide = tous les clients.")] string? client = null,
        [Description("Numéro de pièce de la commande. Vide = toutes les commandes.")] string? piece = null,
        [Description("Commandes passées à partir du AAAA-MM-JJ (vide = pas de limite).")] string? date_debut = null,
        [Description("Commandes passées jusqu'au AAAA-MM-JJ (vide = pas de limite).")] string? date_fin = null,
        [Description("Livraison prévue au plus tard le AAAA-MM-JJ (vide = pas de limite).")] string? livraison_avant = null,
        [Description("true pour inclure les commandes soldées/clôturées (défaut false : uniquement l'en-cours).")] bool? inclure_soldees = null,
        [Description("false pour une ligne par commande au lieu d'une ligne par article (défaut true).")] bool? detail_lignes = null,
        [Description("Nombre maximum de commandes retenues (défaut 50, maximum 500). En mode détaillé, toutes les lignes de ces commandes sont affichées.")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var n = Math.Clamp(limite ?? 50, 1, 500);
        var parLigne = detail_lignes ?? true;

        var filtres = new List<string> { "d.DO_Domaine = 0", "d.DO_Type = 1" };
        if (inclure_soldees != true) filtres.Add("d.DO_Cloture = 0");
        var prm = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(client)) { filtres.Add("d.DO_Tiers = @client"); prm["@client"] = client.Trim(); }
        if (!string.IsNullOrWhiteSpace(piece)) { filtres.Add("d.DO_Piece = @piece"); prm["@piece"] = piece.Trim(); }
        if (!string.IsNullOrWhiteSpace(date_debut)) { filtres.Add("d.DO_Date >= @from"); prm["@from"] = SagePeriod.ParseDate(date_debut, DateTime.Today); }
        if (!string.IsNullOrWhiteSpace(date_fin)) { filtres.Add("d.DO_Date <= @to"); prm["@to"] = SagePeriod.ParseDate(date_fin, DateTime.Today).AddDays(1).AddTicks(-1); }
        if (!string.IsNullOrWhiteSpace(livraison_avant))
        {
            // Une date de livraison non renseignée reste à la date plancher Sage : on l'exclut du filtre.
            filtres.Add("d.DO_DateLivr > '19000101' AND d.DO_DateLivr <= @livr");
            prm["@livr"] = SagePeriod.ParseDate(livraison_avant, DateTime.Today).AddDays(1).AddTicks(-1);
        }
        var where = string.Join(" AND ", filtres);

        // Les commandes sans date de livraison renseignée sont rejetées en fin de liste plutôt qu'en tête.
        const string ordre = "CASE WHEN d.DO_DateLivr IS NULL OR d.DO_DateLivr < '19000101' THEN 1 ELSE 0 END, " +
                             "d.DO_DateLivr, d.DO_Piece";

        var commandes = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) d.DO_Piece, d.DO_Date, d.DO_Ref, d.DO_Tiers, c.CT_Intitule,
                      d.DO_Statut, d.DO_Cloture, d.DO_DateLivr, d.DO_TotalHT
               FROM F_DOCENTETE d
               LEFT JOIN F_COMPTET c ON c.CT_Num = d.DO_Tiers
               WHERE {where}
               ORDER BY {ordre}", prm, ct);

        if (commandes.Count == 0) return "Aucune commande client ne correspond à ces critères.";

        var totaux = await registry.QueryAsync(base_sage,
            $@"SELECT COUNT(*) AS NbCmd, SUM(d.DO_TotalHT) AS TotalHT
               FROM F_DOCENTETE d
               WHERE {where}", prm, ct);
        var nbTotal = SageFormat.ToLong(totaux[0]["NbCmd"]);
        var montantTotal = SageFormat.ToDecimal(totaux[0]["TotalHT"]);

        // Les index Sage sur les pièces portent sur cbDO_Piece, colonne calculée *non persistée* :
        // SQL Server sait s'en servir pour un « DO_Piece = … » mais pas pour joindre F_DOCENTETE à
        // F_DOCLIGNE (1,4 M lignes), où le plan dégénère en balayage complet. D'où la lecture des
        // lignes en second temps, par liste de pièces.
        var pieces = commandes.Select(r => SageFormat.Text(r["DO_Piece"]))
                              .Where(p => p.Length > 0).Distinct().ToList();
        var prmLignes = new Dictionary<string, object?>();
        for (var i = 0; i < pieces.Count; i++) prmLignes[$"@p{i}"] = pieces[i];
        var listePieces = string.Join(", ", prmLignes.Keys);

        var lignes = pieces.Count == 0
            ? Array.Empty<IReadOnlyDictionary<string, object?>>()
            : await registry.QueryAsync(base_sage,
                parLigne
                    ? $@"SELECT l.DO_Piece, l.DL_Ligne, l.AR_Ref, l.DL_Design, l.DL_Qte,
                                l.DL_MontantHT, l.DO_DateLivr
                         FROM F_DOCLIGNE l
                         WHERE l.DO_Domaine = 0 AND l.DO_Type = 1 AND l.DO_Piece IN ({listePieces})
                         ORDER BY l.DO_Piece, l.DL_Ligne"
                    : $@"SELECT l.DO_Piece, COUNT(*) AS NbLignes
                         FROM F_DOCLIGNE l
                         WHERE l.DO_Domaine = 0 AND l.DO_Type = 1 AND l.DO_Piece IN ({listePieces})
                         GROUP BY l.DO_Piece",
                prmLignes, ct);

        var lignesParPiece = lignes.GroupBy(r => SageFormat.Text(r["DO_Piece"]))
                                   .ToDictionary(g => g.Key, g => g.ToList());

        var affichees = new List<IReadOnlyDictionary<string, object?>>();
        decimal totalAffiche = 0;
        foreach (var cmd in commandes)
        {
            var numeroPiece = SageFormat.Text(cmd["DO_Piece"]);
            lignesParPiece.TryGetValue(numeroPiece, out var sesLignes);

            if (!parLigne)
            {
                var ligneCmd = new Dictionary<string, object?>(cmd, StringComparer.OrdinalIgnoreCase)
                {
                    ["_Livraison"] = cmd["DO_DateLivr"],
                    ["_Montant"] = cmd["DO_TotalHT"],
                    ["_NbLignes"] = sesLignes is null ? 0L : SageFormat.ToLong(sesLignes[0]["NbLignes"])
                };
                totalAffiche += SageFormat.ToDecimal(cmd["DO_TotalHT"]);
                affichees.Add(ligneCmd);
                continue;
            }

            if (sesLignes is null) continue;
            foreach (var l in sesLignes)
            {
                // La date de livraison de la ligne prime : elle peut différer de l'entête (livraison partielle).
                var livraison = l["DO_DateLivr"] is DateTime dl && dl.Year >= 1900 ? l["DO_DateLivr"] : cmd["DO_DateLivr"];
                affichees.Add(new Dictionary<string, object?>(cmd, StringComparer.OrdinalIgnoreCase)
                {
                    ["_Livraison"] = livraison,
                    ["_Montant"] = l["DL_MontantHT"],
                    ["AR_Ref"] = l["AR_Ref"],
                    ["DL_Design"] = l["DL_Design"],
                    ["DL_Qte"] = l["DL_Qte"]
                });
                totalAffiche += SageFormat.ToDecimal(l["DL_MontantHT"]);
            }
        }

        if (affichees.Count == 0) return "Les commandes trouvées ne comportent aucune ligne d'article.";

        var colonnes = new List<(string, Func<IReadOnlyDictionary<string, object?>, string>)>
        {
            ("Pièce", r => SageFormat.Text(r["DO_Piece"])),
            ("Commande", r => SageFormat.DateOpt(r["DO_Date"])),
            ("Livraison", r => SageFormat.DateOpt(r["_Livraison"])),
            ("Client", r => SageFormat.Text(r["DO_Tiers"])),
            ("Intitulé", r => SageFormat.Text(r["CT_Intitule"])),
            ("Statut", r => StatutCommande(r["DO_Statut"], r["DO_Cloture"]))
        };
        if (parLigne)
        {
            colonnes.Add(("Article", r => SageFormat.Text(r["AR_Ref"])));
            colonnes.Add(("Désignation", r => SageFormat.Text(r["DL_Design"])));
            colonnes.Add(("Qté", r => SageFormat.ToDecimal(r["DL_Qte"]).ToString("N2", SageFormat.Fr)));
        }
        else
        {
            colonnes.Add(("Réf.", r => SageFormat.Text(r["DO_Ref"])));
            colonnes.Add(("Lignes", r => SageFormat.ToLong(r["_NbLignes"]).ToString()));
        }
        colonnes.Add(("Montant HT", r => SageFormat.Euro(r["_Montant"])));

        var titre = parLigne ? "Détail des lignes de commandes clients" : "Commandes clients (une ligne par commande)";
        var sb = new StringBuilder($"## {titre}\n\n");
        sb.Append(SageFormat.Table(affichees, colonnes.ToArray()));
        sb.AppendLine($"\n**{SageFormat.Euro(totalAffiche)}** sur {commandes.Count} commande(s)" +
                      (parLigne ? $" ({affichees.Count} ligne(s) d'article)" : "") + ".");
        if (commandes.Count < nbTotal)
            sb.AppendLine($"\n*{commandes.Count} commande(s) affichée(s) sur {nbTotal} correspondant aux critères " +
                          $"({SageFormat.Euro(montantTotal)} au total) : augmentez `limite` ou affinez les filtres.*");
        return sb.ToString();
    }

    /// <summary>Libellé du statut d'une commande de vente (énumération Sage DO_Statut) et de sa clôture.</summary>
    private static string StatutCommande(object? statut, object? cloture)
    {
        var libelle = SageFormat.ToLong(statut) switch
        {
            0 => "Saisi",
            1 => "Confirmé",
            2 => "Accepté",
            3 => "À traiter",
            _ => $"Statut {SageFormat.ToLong(statut)}"
        };
        return SageFormat.ToLong(cloture) != 0 ? $"{libelle} (soldée)" : libelle;
    }
}
