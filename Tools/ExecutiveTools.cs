using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Sage100Mcp.Data;

namespace Sage100Mcp.Tools;

/// <summary>Outils de pilotage pour le dirigeant : CA, résultat, top clients/articles, tableau de bord.</summary>
[McpServerToolType]
public sealed class ExecutiveTools
{
    [McpServerTool(Name = "sage_chiffre_affaires")]
    [Description("Chiffre d'affaires (comptes de classe 7, ventes) sur une période, avec ventilation mensuelle. " +
                 "Le CA est calculé en comptabilité (crédit - débit des comptes 70). Idéal pour suivre l'évolution.")]
    public static async Task<string> ChiffreAffaires(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Racine de comptes de produits (défaut '70' = ventes ; '7' = tous les produits).")] string? racine_compte = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var racine = string.IsNullOrWhiteSpace(racine_compte) ? "70" : racine_compte.Trim();

        var rows = await registry.QueryAsync(base_sage,
            @"SELECT YEAR(EC_Date) AS Annee, MONTH(EC_Date) AS Mois,
                     SUM(CASE WHEN EC_Sens = 1 THEN EC_Montant ELSE -EC_Montant END) AS CA
              FROM F_ECRITUREC
              WHERE CG_Num LIKE @racine AND EC_Date BETWEEN @from AND @to
              GROUP BY YEAR(EC_Date), MONTH(EC_Date)
              ORDER BY Annee, Mois",
            new Dictionary<string, object?> { ["@racine"] = $"{racine}%", ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucun chiffre d'affaires {SagePeriod.Describe(from, to)}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["CA"]));
        var table = SageFormat.Table(rows,
            ("Mois", r => $"{SageFormat.MonthsFr[(int)SageFormat.ToLong(r["Mois"])]} {SageFormat.ToLong(r["Annee"])}"),
            ("Chiffre d'affaires HT", r => SageFormat.Euro(r["CA"])));

        var nbMois = rows.Count;
        return $"## Chiffre d'affaires {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total CA : {SageFormat.Euro(total)}** — moyenne mensuelle : {SageFormat.Euro(total / Math.Max(1, nbMois))} ({nbMois} mois)";
    }

    [McpServerTool(Name = "sage_compte_resultat")]
    [Description("Compte de résultat simplifié sur une période : total produits (classe 7), total charges (classe 6), " +
                 "résultat (bénéfice ou perte). Vue synthétique pour le dirigeant.")]
    public static async Task<string> CompteResultat(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);

        var rows = await registry.QueryAsync(base_sage,
            @"SELECT LEFT(CG_Num, 2) AS Poste,
                     SUM(CASE WHEN EC_Sens = 1 THEN EC_Montant ELSE -EC_Montant END) AS SoldeCredit
              FROM F_ECRITUREC
              WHERE LEFT(CG_Num, 1) IN ('6','7') AND EC_Date BETWEEN @from AND @to
              GROUP BY LEFT(CG_Num, 2)
              ORDER BY Poste",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune écriture de gestion (classes 6 et 7) {SagePeriod.Describe(from, to)}.";

        decimal produits = 0, charges = 0;
        var produitRows = new List<IReadOnlyDictionary<string, object?>>();
        var chargeRows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in rows)
        {
            var poste = SageFormat.Text(r["Poste"]);
            var soldeCredit = SageFormat.ToDecimal(r["SoldeCredit"]);
            if (poste.StartsWith('7'))
            {
                produits += soldeCredit;
                produitRows.Add(new Dictionary<string, object?> { ["Poste"] = poste, ["Montant"] = soldeCredit });
            }
            else
            {
                var charge = -soldeCredit; // charge = débit - crédit
                charges += charge;
                chargeRows.Add(new Dictionary<string, object?> { ["Poste"] = poste, ["Montant"] = charge });
            }
        }

        var resultat = produits - charges;
        var sb = new StringBuilder($"## Compte de résultat simplifié {SagePeriod.Describe(from, to)}\n\n");
        sb.AppendLine($"| Indicateur | Montant |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| **Produits (classe 7)** | {SageFormat.Euro(produits)} |");
        sb.AppendLine($"| **Charges (classe 6)** | {SageFormat.Euro(charges)} |");
        sb.AppendLine($"| **Résultat** | {SageFormat.Euro(resultat)} ({(resultat >= 0 ? "bénéfice" : "perte")}) |");
        if (produits != 0)
            sb.AppendLine($"| Taux de marge | {(resultat / produits).ToString("P1", SageFormat.Fr)} |");

        sb.AppendLine("\n### Détail des produits par poste (classe 70-79)\n");
        sb.Append(SageFormat.Table(produitRows,
            ("Poste", r => SageFormat.Text(r["Poste"])),
            ("Montant", r => SageFormat.Euro(r["Montant"]))));
        sb.AppendLine("\n### Détail des charges par poste (classe 60-69)\n");
        sb.Append(SageFormat.Table(chargeRows,
            ("Poste", r => SageFormat.Text(r["Poste"])),
            ("Montant", r => SageFormat.Euro(r["Montant"]))));
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_top_clients")]
    [Description("Top clients par chiffre d'affaires facturé sur une période (factures de vente, F_DOCENTETE). " +
                 "Pour identifier les comptes clés. Montants HT.")]
    public static async Task<string> TopClients(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nombre de clients (défaut 20).")] int? top = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var n = Math.Clamp(top ?? 20, 1, 200);

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) d.DO_Tiers,
                      MAX(c.CT_Intitule) AS CT_Intitule,
                      SUM(d.DO_TotalHT) AS CA_HT,
                      COUNT(*) AS NbFactures
               FROM F_DOCENTETE d
               LEFT JOIN F_COMPTET c ON c.CT_Num = d.DO_Tiers
               WHERE d.DO_Domaine = 0 AND d.DO_Type IN (6, 7) AND d.DO_Date BETWEEN @from AND @to
               GROUP BY d.DO_Tiers
               ORDER BY CA_HT DESC",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune facture de vente {SagePeriod.Describe(from, to)}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["CA_HT"]));
        var table = SageFormat.Table(rows,
            ("Client", r => SageFormat.Text(r["DO_Tiers"])),
            ("Intitulé", r => SageFormat.Text(r["CT_Intitule"])),
            ("Factures", r => SageFormat.ToLong(r["NbFactures"]).ToString()),
            ("CA HT", r => SageFormat.Euro(r["CA_HT"])));

        return $"## Top {rows.Count} clients {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total top clients : {SageFormat.Euro(total)}** (HT, factures de vente)";
    }

    [McpServerTool(Name = "sage_top_articles")]
    [Description("Top articles/produits par chiffre d'affaires sur une période (lignes de factures de vente, F_DOCLIGNE). " +
                 "Montants HT et quantités vendues.")]
    public static async Task<string> TopArticles(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nombre d'articles (défaut 20).")] int? top = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var n = Math.Clamp(top ?? 20, 1, 200);

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) l.AR_Ref,
                      MAX(l.DL_Design) AS Design,
                      SUM(l.DL_Qte) AS Qte,
                      SUM(l.DL_MontantHT) AS CA_HT
               FROM F_DOCLIGNE l
               WHERE l.DO_Domaine = 0 AND l.DO_Type IN (6, 7) AND l.DO_Date BETWEEN @from AND @to
                 AND l.AR_Ref IS NOT NULL AND l.AR_Ref <> ''
               GROUP BY l.AR_Ref
               ORDER BY CA_HT DESC",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune ligne de facture de vente {SagePeriod.Describe(from, to)}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["CA_HT"]));
        var table = SageFormat.Table(rows,
            ("Référence", r => SageFormat.Text(r["AR_Ref"])),
            ("Désignation", r => SageFormat.Text(r["Design"])),
            ("Qté vendue", r => SageFormat.ToDecimal(r["Qte"]).ToString("N0", SageFormat.Fr)),
            ("CA HT", r => SageFormat.Euro(r["CA_HT"])));

        return $"## Top {rows.Count} articles {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total top articles : {SageFormat.Euro(total)}** (HT)";
    }

    [McpServerTool(Name = "sage_ca_par_departement")]
    [Description("Chiffre d'affaires ventilé par département géographique du client (déduit du code postal pour la France) " +
                 "et par pays pour les clients étrangers. Basé sur les factures de vente (F_DOCENTETE). Montants HT. " +
                 "Permet d'analyser la répartition territoriale des ventes.")]
    public static async Task<string> CaParDepartement(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Zone : 'france' (départements uniquement), 'etranger' (pays étrangers) ou 'tout' (défaut).")] string? zone = null,
        [Description("Nombre maximum de lignes affichées (défaut 50).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var n = Math.Clamp(limite ?? 50, 1, 200);

        // Un client est "français" si CT_Pays est vide ou commence par "France".
        const string estFrancais = "(ISNULL(c.CT_Pays, '') = '' OR c.CT_Pays LIKE 'France%')";
        var filtreZone = (zone?.Trim().ToLowerInvariant()) switch
        {
            "france" or "fr" => $" AND {estFrancais}",
            "etranger" or "étranger" or "export" => $" AND NOT {estFrancais}",
            _ => ""
        };

        // Le code département : 3 chiffres pour l'outre-mer (97x/98x), sinon 2 chiffres.
        const string deptExpr =
            "CASE WHEN LEN(LTRIM(ISNULL(c.CT_CodePostal,''))) < 2 THEN 'ND' " +
            "WHEN LEFT(LTRIM(c.CT_CodePostal),2) IN ('97','98') THEN LEFT(LTRIM(c.CT_CodePostal),3) " +
            "ELSE LEFT(LTRIM(c.CT_CodePostal),2) END";
        var deptForFr = $"CASE WHEN {estFrancais} THEN {deptExpr} ELSE '' END";
        var paysEtr = $"CASE WHEN {estFrancais} THEN '' ELSE c.CT_Pays END";

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) {deptForFr} AS Dept, {paysEtr} AS PaysEtr,
                      SUM(d.DO_TotalHT) AS CA_HT, COUNT(*) AS NbFactures,
                      SUM(SUM(d.DO_TotalHT)) OVER () AS GrandTotal,
                      SUM(SUM(CASE WHEN {estFrancais} THEN d.DO_TotalHT ELSE 0 END)) OVER () AS GrandTotalFrance
               FROM F_DOCENTETE d
               LEFT JOIN F_COMPTET c ON c.CT_Num = d.DO_Tiers
               WHERE d.DO_Domaine = 0 AND d.DO_Type IN (6, 7) AND d.DO_Date BETWEEN @from AND @to{filtreZone}
               GROUP BY {deptForFr}, {paysEtr}
               ORDER BY CA_HT DESC",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune facture de vente {SagePeriod.Describe(from, to)}.";

        // Totaux réels (toutes lignes), indépendants du TOP, via fonction fenêtre.
        decimal total = SageFormat.ToDecimal(rows[0]["GrandTotal"]);
        decimal caFrance = SageFormat.ToDecimal(rows[0]["GrandTotalFrance"]);
        decimal caAffiche = rows.Sum(r => SageFormat.ToDecimal(r["CA_HT"]));

        string Libelle(IReadOnlyDictionary<string, object?> r)
        {
            var pays = SageFormat.Text(r["PaysEtr"]);
            if (!string.IsNullOrEmpty(pays)) return $"🌍 {pays} (étranger)";
            var dept = SageFormat.Text(r["Dept"]);
            return dept == "ND" ? "Non déterminé" : $"{dept} – {FrenchDepartments.Name(dept)}";
        }

        var table = SageFormat.Table(rows,
            ("Département / Pays", Libelle),
            ("Factures", r => SageFormat.ToLong(r["NbFactures"]).ToString()),
            ("CA HT", r => SageFormat.Euro(r["CA_HT"])),
            ("% du total", r => total == 0 ? "0 %"
                : (SageFormat.ToDecimal(r["CA_HT"]) / total).ToString("P1", SageFormat.Fr)));

        var zoneLabel = (zone?.Trim().ToLowerInvariant()) switch
        {
            "france" or "fr" => " (France)",
            "etranger" or "étranger" or "export" => " (étranger)",
            _ => ""
        };

        var noteTronque = caAffiche < total
            ? $" · {rows.Count} lignes affichées sur le total"
            : $" · {rows.Count} lignes";

        return $"## Chiffre d'affaires par département{zoneLabel} {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total CA : {SageFormat.Euro(total)}** — dont France : {SageFormat.Euro(caFrance)} " +
               $"({(total == 0 ? "0 %" : (caFrance / total).ToString("P1", SageFormat.Fr))}) · " +
               $"étranger : {SageFormat.Euro(total - caFrance)}{noteTronque}";
    }

    [McpServerTool(Name = "sage_ventes_par_mois")]
    [Description("Ventes HT facturées par mois (factures de gestion commerciale, F_DOCENTETE). " +
                 "Correspond au « CA HT facturé » et à la courbe d'évolution des ventes par mois. " +
                 "Inclut factures (DO_Type 6) et factures comptabilisées (7). " +
                 "Différent de sage_chiffre_affaires qui calcule le CA en comptabilité (classe 7).")]
    public static async Task<string> VentesParMois(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var rows = await registry.QueryAsync(base_sage,
            @"SELECT YEAR(DO_Date) AS Annee, MONTH(DO_Date) AS Mois,
                     SUM(DO_TotalHT) AS CA_HT, COUNT(*) AS NbFactures
              FROM F_DOCENTETE
              WHERE DO_Domaine = 0 AND DO_Type IN (6, 7) AND DO_Date BETWEEN @from AND @to
              GROUP BY YEAR(DO_Date), MONTH(DO_Date)
              ORDER BY Annee, Mois",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune facture de vente {SagePeriod.Describe(from, to)}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["CA_HT"]));
        long nbFact = rows.Sum(r => SageFormat.ToLong(r["NbFactures"]));
        var table = SageFormat.Table(rows,
            ("Mois", r => $"{SageFormat.MonthsFr[(int)SageFormat.ToLong(r["Mois"])]} {SageFormat.ToLong(r["Annee"])}"),
            ("Factures", r => SageFormat.ToLong(r["NbFactures"]).ToString()),
            ("Ventes HT", r => SageFormat.Euro(r["CA_HT"])));

        return $"## Ventes HT facturées par mois {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total ventes HT : {SageFormat.Euro(total)}** — {nbFact} factures · " +
               $"moyenne mensuelle : {SageFormat.Euro(total / Math.Max(1, rows.Count))}";
    }

    [McpServerTool(Name = "sage_ca_par_collaborateur")]
    [Description("Ventes HT par collaborateur / commercial (champ collaborateur des factures, " +
                 "F_DOCENTETE.CO_No → F_COLLABORATEUR). Répartition du chiffre d'affaires par vendeur, " +
                 "avec part en pourcentage — idéal pour un camembert/donut.")]
    public static async Task<string> CaParCollaborateur(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nombre de collaborateurs (défaut 30).")] int? top = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var n = Math.Clamp(top ?? 30, 1, 200);

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) d.CO_No,
                      MAX(LTRIM(RTRIM(ISNULL(co.CO_Prenom, '') + ' ' + ISNULL(co.CO_Nom, '')))) AS Collaborateur,
                      SUM(d.DO_TotalHT) AS CA_HT, COUNT(*) AS NbFactures,
                      SUM(SUM(d.DO_TotalHT)) OVER () AS GrandTotal
               FROM F_DOCENTETE d
               LEFT JOIN F_COLLABORATEUR co ON co.CO_No = d.CO_No
               WHERE d.DO_Domaine = 0 AND d.DO_Type IN (6, 7) AND d.DO_Date BETWEEN @from AND @to
               GROUP BY d.CO_No
               ORDER BY CA_HT DESC",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune vente {SagePeriod.Describe(from, to)}.";

        decimal total = SageFormat.ToDecimal(rows[0]["GrandTotal"]);
        string Nom(IReadOnlyDictionary<string, object?> r)
        {
            var nom = SageFormat.Text(r["Collaborateur"]);
            return string.IsNullOrEmpty(nom) ? $"Non attribué (#{SageFormat.ToLong(r["CO_No"])})" : nom;
        }

        var table = SageFormat.Table(rows,
            ("Collaborateur", Nom),
            ("Factures", r => SageFormat.ToLong(r["NbFactures"]).ToString()),
            ("Ventes HT", r => SageFormat.Euro(r["CA_HT"])),
            ("% du total", r => total == 0 ? "0 %"
                : (SageFormat.ToDecimal(r["CA_HT"]) / total).ToString("P1", SageFormat.Fr)));

        return $"## Ventes HT par collaborateur {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total ventes HT : {SageFormat.Euro(total)}** ({rows.Count} collaborateurs)";
    }

    [McpServerTool(Name = "sage_clients_par_collaborateur")]
    [Description("Portefeuille clients par collaborateur / commercial : nombre de clients rattachés à chaque " +
                 "collaborateur (champ collaborateur de la fiche client, F_COMPTET.CO_No → F_COLLABORATEUR). " +
                 "Si le paramètre 'collaborateur' est précisé, liste les clients de ce commercial au lieu des effectifs.")]
    public static async Task<string> ClientsParCollaborateur(
        SageDatabaseRegistry registry,
        [Description("Nom (ou partie du nom) d'un collaborateur pour lister SES clients. Vide = répartition globale par collaborateur.")] string? collaborateur = null,
        [Description("Nombre maximum de lignes (défaut 50 pour la répartition, 200 pour la liste détaillée).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        // Mode détail : liste des clients d'un collaborateur donné.
        if (!string.IsNullOrWhiteSpace(collaborateur))
        {
            var n = Math.Clamp(limite ?? 200, 1, 1000);
            var clients = await registry.QueryAsync(base_sage,
                $@"SELECT TOP ({n}) c.CT_Num, c.CT_Intitule, c.CT_Ville, c.CT_Telephone, c.CT_Sommeil
                   FROM F_COMPTET c
                   LEFT JOIN F_COLLABORATEUR co ON co.CO_No = c.CO_No
                   WHERE c.CT_Type = 0
                     AND (co.CO_Nom LIKE @q OR co.CO_Prenom LIKE @q
                          OR LTRIM(RTRIM(ISNULL(co.CO_Prenom,'') + ' ' + ISNULL(co.CO_Nom,''))) LIKE @q)
                   ORDER BY c.CT_Intitule",
                new Dictionary<string, object?> { ["@q"] = $"%{collaborateur.Trim()}%" }, ct);

            if (clients.Count == 0)
                return $"Aucun client rattaché à un collaborateur correspondant à « {collaborateur} ».";

            var tableD = SageFormat.Table(clients,
                ("Numéro", r => SageFormat.Text(r["CT_Num"])),
                ("Client", r => SageFormat.Text(r["CT_Intitule"])),
                ("Ville", r => SageFormat.Text(r["CT_Ville"])),
                ("Téléphone", r => SageFormat.Text(r["CT_Telephone"])),
                ("État", r => SageFormat.ToLong(r["CT_Sommeil"]) != 0 ? "Sommeil" : "Actif"));

            return $"## Clients du collaborateur « {collaborateur} » ({clients.Count})\n\n" + tableD;
        }

        // Mode répartition : nombre de clients par collaborateur.
        var top = Math.Clamp(limite ?? 50, 1, 500);
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({top}) c.CO_No,
                      MAX(LTRIM(RTRIM(ISNULL(co.CO_Prenom,'') + ' ' + ISNULL(co.CO_Nom,'')))) AS Collaborateur,
                      COUNT(*) AS NbClients,
                      SUM(CASE WHEN c.CT_Sommeil <> 0 THEN 1 ELSE 0 END) AS NbSommeil,
                      SUM(COUNT(*)) OVER () AS GrandTotal
               FROM F_COMPTET c
               LEFT JOIN F_COLLABORATEUR co ON co.CO_No = c.CO_No
               WHERE c.CT_Type = 0
               GROUP BY c.CO_No
               ORDER BY NbClients DESC",
            null, ct);

        if (rows.Count == 0) return "Aucun client trouvé.";

        long totalClients = SageFormat.ToLong(rows[0]["GrandTotal"]);
        string Nom(IReadOnlyDictionary<string, object?> r)
        {
            var nom = SageFormat.Text(r["Collaborateur"]);
            return string.IsNullOrEmpty(nom) ? $"Non attribué (#{SageFormat.ToLong(r["CO_No"])})" : nom;
        }

        var table = SageFormat.Table(rows,
            ("Collaborateur", Nom),
            ("Nb clients", r => SageFormat.ToLong(r["NbClients"]).ToString()),
            ("dont en sommeil", r => SageFormat.ToLong(r["NbSommeil"]).ToString()),
            ("% du portefeuille", r => totalClients == 0 ? "0 %"
                : ((decimal)SageFormat.ToLong(r["NbClients"]) / totalClients).ToString("P1", SageFormat.Fr)));

        return $"## Portefeuille clients par collaborateur\n\n" + table +
               $"\n**Total : {totalClients} clients** répartis sur {rows.Count} collaborateur(s)." +
               "\n*Précisez le paramètre `collaborateur` pour obtenir la liste détaillée de ses clients.*";
    }

    [McpServerTool(Name = "sage_tableau_de_bord")]
    [Description("Tableau de bord de synthèse pour le dirigeant sur une période : chiffre d'affaires, charges, " +
                 "résultat, encours clients et dettes fournisseurs en un seul appel.")]
    public static async Task<string> TableauDeBord(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var db = registry.Resolve(base_sage);

        var gestion = await registry.QueryAsync(base_sage,
            @"SELECT
                SUM(CASE WHEN LEFT(CG_Num,1)='7' THEN (CASE WHEN EC_Sens=1 THEN EC_Montant ELSE -EC_Montant END) ELSE 0 END) AS Produits,
                SUM(CASE WHEN LEFT(CG_Num,1)='6' THEN (CASE WHEN EC_Sens=0 THEN EC_Montant ELSE -EC_Montant END) ELSE 0 END) AS Charges
              FROM F_ECRITUREC
              WHERE LEFT(CG_Num,1) IN ('6','7') AND EC_Date BETWEEN @from AND @to",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        var encours = await registry.QueryAsync(base_sage,
            @"SELECT
                SUM(CASE WHEN CG_Num LIKE '411%' THEN (CASE WHEN EC_Sens=0 THEN EC_Montant ELSE -EC_Montant END) ELSE 0 END) AS Clients,
                SUM(CASE WHEN CG_Num LIKE '401%' THEN (CASE WHEN EC_Sens=1 THEN EC_Montant ELSE -EC_Montant END) ELSE 0 END) AS Fournisseurs
              FROM F_ECRITUREC
              WHERE (CG_Num LIKE '411%' OR CG_Num LIKE '401%')
                AND (EC_Lettrage IS NULL OR EC_Lettrage = '') AND EC_Date <= @to",
            new Dictionary<string, object?> { ["@to"] = to }, ct);

        var produits = SageFormat.ToDecimal(gestion[0]["Produits"]);
        var charges = SageFormat.ToDecimal(gestion[0]["Charges"]);
        var resultat = produits - charges;
        var clients = SageFormat.ToDecimal(encours[0]["Clients"]);
        var fournisseurs = SageFormat.ToDecimal(encours[0]["Fournisseurs"]);

        var sb = new StringBuilder($"## Tableau de bord — {db.Name}\n");
        sb.AppendLine($"*Période {SagePeriod.Describe(from, to)}*\n");
        sb.AppendLine("| Indicateur | Montant |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 📈 Chiffre d'affaires / produits | {SageFormat.Euro(produits)} |");
        sb.AppendLine($"| 📉 Charges | {SageFormat.Euro(charges)} |");
        sb.AppendLine($"| 💰 Résultat | **{SageFormat.Euro(resultat)}** ({(resultat >= 0 ? "bénéfice" : "perte")}) |");
        if (produits != 0)
            sb.AppendLine($"| Taux de marge | {(resultat / produits).ToString("P1", SageFormat.Fr)} |");
        sb.AppendLine($"| 🧾 Encours clients (non réglé au {to:dd/MM/yyyy}) | {SageFormat.Euro(clients)} |");
        sb.AppendLine($"| 🏭 Dettes fournisseurs (à payer au {to:dd/MM/yyyy}) | {SageFormat.Euro(fournisseurs)} |");
        sb.AppendLine($"| ⚖️ Position nette (clients - fournisseurs) | {SageFormat.Euro(clients - fournisseurs)} |");
        sb.AppendLine("\n*Détails disponibles via sage_chiffre_affaires, sage_compte_resultat, sage_encours_clients, sage_balance_agee_clients.*");
        return sb.ToString();
    }
}
