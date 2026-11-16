using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Sage100Mcp.Data;

namespace Sage100Mcp.Tools;

/// <summary>
/// Outils côté achats, pendants des outils de vente : factures d'achat (DO_Domaine = 1, DO_Type 16 = facture,
/// 17 = facture comptabilisée), commandes fournisseurs (12), demandes d'achat (10), fournisseurs (CT_Type = 1).
/// </summary>
[McpServerToolType]
public sealed class AchatsTools
{
    /// <summary>Filtre des factures d'achat sur F_DOCENTETE / F_DOCLIGNE (alias à préfixer).</summary>
    private const string FacturesAchat = "DO_Domaine = 1 AND DO_Type IN (16, 17)";

    /// <summary>Même filtre, colonnes préfixées par l'alias de table (requêtes avec jointure).</summary>
    private static string Factures(string alias) => $"{alias}.DO_Domaine = 1 AND {alias}.DO_Type IN (16, 17)";

    // ----- Volumes ---------------------------------------------------------------------------------

    [McpServerTool(Name = "sage_achats_factures_par_mois")]
    [Description("Achats HT facturés par mois (factures fournisseurs de gestion commerciale, F_DOCENTETE, " +
                 "DO_Type 16 et 17). Pendant de sage_ventes_par_mois côté achats. Différent de sage_achats_par_mois " +
                 "qui calcule les achats en comptabilité (comptes 60).")]
    public static async Task<string> AchatsFacturesParMois(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT YEAR(DO_Date) AS Annee, MONTH(DO_Date) AS Mois,
                      SUM(DO_TotalHT) AS Achats_HT, COUNT(*) AS NbFactures
               FROM F_DOCENTETE
               WHERE {FacturesAchat} AND DO_Date BETWEEN @from AND @to
               GROUP BY YEAR(DO_Date), MONTH(DO_Date)
               ORDER BY Annee, Mois",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune facture d'achat {SagePeriod.Describe(from, to)}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["Achats_HT"]));
        long nbFact = rows.Sum(r => SageFormat.ToLong(r["NbFactures"]));
        var table = SageFormat.Table(rows,
            ("Mois", r => $"{SageFormat.MonthsFr[(int)SageFormat.ToLong(r["Mois"])]} {SageFormat.ToLong(r["Annee"])}"),
            ("Factures", r => SageFormat.ToLong(r["NbFactures"]).ToString()),
            ("Achats HT", r => SageFormat.Euro(r["Achats_HT"])));

        return $"## Achats HT facturés par mois {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total achats HT : {SageFormat.Euro(total)}** — {nbFact} factures · " +
               $"moyenne mensuelle : {SageFormat.Euro(total / Math.Max(1, rows.Count))}";
    }

    [McpServerTool(Name = "sage_evolution_achats_annuelle")]
    [Description("Évolution des achats par année (comptes de charges 60, débit − crédit) avec variation en % d'une " +
                 "année sur l'autre. Pendant de sage_evolution_ca_annuelle côté achats.")]
    public static async Task<string> EvolutionAchatsAnnuelle(
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
                     SUM(CASE WHEN EC_Sens = 0 THEN EC_Montant ELSE -EC_Montant END) AS Achats
              FROM F_ECRITUREC
              WHERE CG_Num LIKE '60%' AND YEAR(JM_Date) BETWEEN @a1 AND @a2
              GROUP BY YEAR(JM_Date)
              ORDER BY An",
            new Dictionary<string, object?> { ["@a1"] = a1, ["@a2"] = a2 }, ct);

        if (rows.Count == 0) return $"Aucun achat (comptes 60) entre {a1} et {a2}.";

        var sb = new StringBuilder($"## Évolution des achats {a1}–{a2}\n\n");
        sb.AppendLine("| Année | Achats HT | Variation |");
        sb.AppendLine("|---|---|---|");
        decimal? prev = null;
        foreach (var r in rows)
        {
            var achats = SageFormat.ToDecimal(r["Achats"]);
            string var = prev is null or 0
                ? "—"
                : ((achats - prev.Value) / Math.Abs(prev.Value)).ToString("+0.0 %;-0.0 %", SageFormat.Fr);
            sb.AppendLine($"| {SageFormat.ToLong(r["An"])} | {SageFormat.Euro(achats)} | {var} |");
            prev = achats;
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_comparatif_achats")]
    [Description("Comparaison des achats facturés (factures fournisseurs, F_DOCENTETE) entre deux années, mois par " +
                 "mois, avec variation en %. Pendant de sage_comparatif_ventes côté achats.")]
    public static async Task<string> ComparatifAchats(
        SageDatabaseRegistry registry,
        [Description("Année de référence (la plus ancienne). Vide = année précédente.")] int? annee1 = null,
        [Description("Année à comparer (la plus récente). Vide = année en cours.")] int? annee2 = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var a2 = annee2 ?? DateTime.Today.Year;
        var a1 = annee1 ?? a2 - 1;

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT MONTH(DO_Date) AS Mois,
                      SUM(CASE WHEN YEAR(DO_Date) = @a1 THEN DO_TotalHT ELSE 0 END) AS V1,
                      SUM(CASE WHEN YEAR(DO_Date) = @a2 THEN DO_TotalHT ELSE 0 END) AS V2
               FROM F_DOCENTETE
               WHERE {FacturesAchat} AND YEAR(DO_Date) IN (@a1, @a2)
               GROUP BY MONTH(DO_Date)
               ORDER BY Mois",
            new Dictionary<string, object?> { ["@a1"] = a1, ["@a2"] = a2 }, ct);

        if (rows.Count == 0) return $"Aucune facture d'achat en {a1} ni en {a2}.";

        string Var(decimal v1, decimal v2) => v1 == 0 ? (v2 == 0 ? "—" : "nouveau")
            : ((v2 - v1) / Math.Abs(v1)).ToString("+0.0 %;-0.0 %", SageFormat.Fr);

        var sb = new StringBuilder($"## Comparatif des achats {a1} vs {a2}\n\n");
        sb.AppendLine($"| Mois | {a1} | {a2} | Variation |");
        sb.AppendLine("|---|---|---|---|");
        decimal t1 = 0, t2 = 0;
        foreach (var r in rows)
        {
            var v1 = SageFormat.ToDecimal(r["V1"]);
            var v2 = SageFormat.ToDecimal(r["V2"]);
            t1 += v1; t2 += v2;
            sb.AppendLine($"| {SageFormat.MonthsFr[(int)SageFormat.ToLong(r["Mois"])]} | {SageFormat.Euro(v1)} | {SageFormat.Euro(v2)} | {Var(v1, v2)} |");
        }
        sb.AppendLine($"| **Total** | **{SageFormat.Euro(t1)}** | **{SageFormat.Euro(t2)}** | **{Var(t1, t2)}** |");
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_facture_moyenne_achats")]
    [Description("Montant moyen des factures fournisseurs par mois : achats facturés divisés par le nombre de " +
                 "factures d'achat. Pendant de sage_panier_moyen côté achats.")]
    public static async Task<string> FactureMoyenneAchats(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT YEAR(DO_Date) AS Annee, MONTH(DO_Date) AS Mois, SUM(DO_TotalHT) AS Achats, COUNT(*) AS Nb
               FROM F_DOCENTETE
               WHERE {FacturesAchat} AND DO_Date BETWEEN @from AND @to
               GROUP BY YEAR(DO_Date), MONTH(DO_Date)
               ORDER BY Annee, Mois",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune facture d'achat {SagePeriod.Describe(from, to)}.";

        decimal tot = rows.Sum(r => SageFormat.ToDecimal(r["Achats"]));
        long nb = rows.Sum(r => SageFormat.ToLong(r["Nb"]));
        var table = SageFormat.Table(rows,
            ("Mois", r => $"{SageFormat.MonthsFr[(int)SageFormat.ToLong(r["Mois"])]} {SageFormat.ToLong(r["Annee"])}"),
            ("Factures", r => SageFormat.ToLong(r["Nb"]).ToString()),
            ("Achats HT", r => SageFormat.Euro(r["Achats"])),
            ("Facture moyenne", r =>
            {
                var n = SageFormat.ToLong(r["Nb"]);
                return n == 0 ? "—" : SageFormat.Euro(SageFormat.ToDecimal(r["Achats"]) / n);
            }));

        return $"## Facture fournisseur moyenne par mois {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Facture moyenne globale : {SageFormat.Euro(nb == 0 ? 0 : tot / nb)}** " +
               $"({nb} factures · achats {SageFormat.Euro(tot)})";
    }

    // ----- Répartitions ----------------------------------------------------------------------------

    [McpServerTool(Name = "sage_top_articles_achetes")]
    [Description("Top articles achetés par montant sur une période (lignes de factures d'achat, F_DOCLIGNE). " +
                 "Montants HT, quantités achetées et prix unitaire moyen. Pendant de sage_top_articles côté achats.")]
    public static async Task<string> TopArticlesAchetes(
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
                      SUM(l.DL_MontantHT) AS Achats_HT,
                      COUNT(DISTINCT l.CT_Num) AS NbFournisseurs,
                      SUM(SUM(l.DL_MontantHT)) OVER () AS GrandTotal
               FROM F_DOCLIGNE l
               WHERE {Factures("l")} AND l.DO_Date BETWEEN @from AND @to
                 AND l.AR_Ref IS NOT NULL AND l.AR_Ref <> ''
               GROUP BY l.AR_Ref
               ORDER BY Achats_HT DESC",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune ligne de facture d'achat {SagePeriod.Describe(from, to)}.";

        decimal total = SageFormat.ToDecimal(rows[0]["GrandTotal"]);
        decimal affiche = rows.Sum(r => SageFormat.ToDecimal(r["Achats_HT"]));
        var table = SageFormat.Table(rows,
            ("Référence", r => SageFormat.Text(r["AR_Ref"])),
            ("Désignation", r => SageFormat.Text(r["Design"])),
            ("Fournisseurs", r => SageFormat.ToLong(r["NbFournisseurs"]).ToString()),
            ("Qté achetée", r => SageFormat.ToDecimal(r["Qte"]).ToString("N0", SageFormat.Fr)),
            ("PU moyen", r =>
            {
                var q = SageFormat.ToDecimal(r["Qte"]);
                return q == 0 ? "—" : SageFormat.Euro(SageFormat.ToDecimal(r["Achats_HT"]) / q);
            }),
            ("Achats HT", r => SageFormat.Euro(r["Achats_HT"])),
            ("% du total", r => total == 0 ? "0 %"
                : (SageFormat.ToDecimal(r["Achats_HT"]) / total).ToString("P1", SageFormat.Fr)));

        return $"## Top {rows.Count} articles achetés {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total top articles : {SageFormat.Euro(affiche)}** sur {SageFormat.Euro(total)} d'achats d'articles (HT)";
    }

    [McpServerTool(Name = "sage_achats_par_famille")]
    [Description("Achats par famille d'articles (lignes de factures d'achat F_DOCLIGNE + F_ARTICLE) sur une période. " +
                 "Montants HT, quantités et part en %. Pendant de sage_ca_par_famille côté achats.")]
    public static async Task<string> AchatsParFamille(
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
            PilotageTools.FamilleSql(n, Factures("l")),
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucun achat {SagePeriod.Describe(from, to)}.";

        decimal total = SageFormat.ToDecimal(rows[0]["GrandTotal"]);
        var table = SageFormat.Table(rows,
            ("Famille", r => SageFormat.Text(r["Famille"])),
            ("Articles", r => SageFormat.ToLong(r["NbArticles"]).ToString()),
            ("Qté achetée", r => SageFormat.ToDecimal(r["Qte"]).ToString("N0", SageFormat.Fr)),
            ("Achats HT", r => SageFormat.Euro(r["Montant_HT"])),
            ("% du total", r => total == 0 ? "0 %"
                : (SageFormat.ToDecimal(r["Montant_HT"]) / total).ToString("P1", SageFormat.Fr)));

        return $"## Achats par famille d'articles {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total achats HT : {SageFormat.Euro(total)}** ({rows.Count} familles)";
    }

    [McpServerTool(Name = "sage_achats_par_departement")]
    [Description("Achats ventilés par département géographique du fournisseur (déduit du code postal pour la France) " +
                 "et par pays pour les fournisseurs étrangers. Basé sur les factures d'achat (F_DOCENTETE). Montants HT. " +
                 "Mesure la part des achats locaux, nationaux et à l'import. Pendant de sage_ca_par_departement.")]
    public static async Task<string> AchatsParDepartement(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Zone : 'france' (départements uniquement), 'etranger' (pays étrangers) ou 'tout' (défaut).")] string? zone = null,
        [Description("Nombre maximum de lignes affichées (défaut 50).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
        => await ExecutiveTools.ParDepartementAsync(registry, achat: true, date_debut, date_fin, zone, limite, base_sage, ct);

    [McpServerTool(Name = "sage_achats_par_acheteur")]
    [Description("Achats HT par acheteur (collaborateur des factures d'achat, F_DOCENTETE.CO_No → F_COLLABORATEUR), " +
                 "avec part en pourcentage. Pendant de sage_ca_par_collaborateur côté achats.")]
    public static async Task<string> AchatsParAcheteur(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nombre d'acheteurs (défaut 30).")] int? top = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var n = Math.Clamp(top ?? 30, 1, 200);

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) d.CO_No,
                      MAX(LTRIM(RTRIM(ISNULL(co.CO_Prenom, '') + ' ' + ISNULL(co.CO_Nom, '')))) AS Acheteur,
                      SUM(d.DO_TotalHT) AS Achats_HT, COUNT(*) AS NbFactures,
                      COUNT(DISTINCT d.DO_Tiers) AS NbFournisseurs,
                      SUM(SUM(d.DO_TotalHT)) OVER () AS GrandTotal
               FROM F_DOCENTETE d
               LEFT JOIN F_COLLABORATEUR co ON co.CO_No = d.CO_No
               WHERE {Factures("d")} AND d.DO_Date BETWEEN @from AND @to
               GROUP BY d.CO_No
               ORDER BY Achats_HT DESC",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucune facture d'achat {SagePeriod.Describe(from, to)}.";

        decimal total = SageFormat.ToDecimal(rows[0]["GrandTotal"]);
        string Nom(IReadOnlyDictionary<string, object?> r)
        {
            var nom = SageFormat.Text(r["Acheteur"]);
            return string.IsNullOrEmpty(nom) ? $"Non attribué (#{SageFormat.ToLong(r["CO_No"])})" : nom;
        }

        var table = SageFormat.Table(rows,
            ("Acheteur", Nom),
            ("Fournisseurs", r => SageFormat.ToLong(r["NbFournisseurs"]).ToString()),
            ("Factures", r => SageFormat.ToLong(r["NbFactures"]).ToString()),
            ("Achats HT", r => SageFormat.Euro(r["Achats_HT"])),
            ("% du total", r => total == 0 ? "0 %"
                : (SageFormat.ToDecimal(r["Achats_HT"]) / total).ToString("P1", SageFormat.Fr)));

        return $"## Achats HT par acheteur {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**Total achats HT : {SageFormat.Euro(total)}** ({rows.Count} acheteurs)";
    }

    [McpServerTool(Name = "sage_analyse_abc_fournisseurs")]
    [Description("Analyse ABC (loi de Pareto 20/80) des achats facturés par fournisseur sur une période : segmente " +
                 "les fournisseurs en A (jusqu'à 80 % des achats), B (80–95 %) et C (95–100 %). Mesure la " +
                 "dépendance fournisseurs et cible les négociations. Pendant de sage_analyse_abc_clients.")]
    public static async Task<string> AnalyseAbcFournisseurs(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT d.DO_Tiers, MAX(c.CT_Intitule) AS Intitule, SUM(d.DO_TotalHT) AS Achats
               FROM F_DOCENTETE d
               LEFT JOIN F_COMPTET c ON c.CT_Num = d.DO_Tiers
               WHERE {Factures("d")} AND d.DO_Date BETWEEN @from AND @to
               GROUP BY d.DO_Tiers
               HAVING SUM(d.DO_TotalHT) > 0
               ORDER BY Achats DESC",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucun achat facturé {SagePeriod.Describe(from, to)}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["Achats"]));
        int nbA = 0, nbB = 0, nbC = 0;
        decimal vA = 0, vB = 0, vC = 0, cumul = 0;
        var topA = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in rows)
        {
            var v = SageFormat.ToDecimal(r["Achats"]);
            var pctAvant = total == 0 ? 0 : cumul / total;
            cumul += v;
            if (pctAvant < 0.80m) { nbA++; vA += v; if (topA.Count < 10) topA.Add(r); }
            else if (pctAvant < 0.95m) { nbB++; vB += v; }
            else { nbC++; vC += v; }
        }

        string Pct(decimal v) => total == 0 ? "0 %" : (v / total).ToString("P1", SageFormat.Fr);
        string PctN(int nb) => ((decimal)nb / rows.Count).ToString("P1", SageFormat.Fr);

        var sb = new StringBuilder($"## Analyse ABC fournisseurs {SagePeriod.Describe(from, to)}\n\n");
        sb.AppendLine("| Segment | Fournisseurs | % fournisseurs | Achats HT | % achats |");
        sb.AppendLine("|---|---|---|---|---|");
        sb.AppendLine($"| **A** (0–80 % des achats) | {nbA} | {PctN(nbA)} | {SageFormat.Euro(vA)} | {Pct(vA)} |");
        sb.AppendLine($"| **B** (80–95 %) | {nbB} | {PctN(nbB)} | {SageFormat.Euro(vB)} | {Pct(vB)} |");
        sb.AppendLine($"| **C** (95–100 %) | {nbC} | {PctN(nbC)} | {SageFormat.Euro(vC)} | {Pct(vC)} |");
        sb.AppendLine($"| **Total** | {rows.Count} | 100 % | {SageFormat.Euro(total)} | 100 % |");
        sb.AppendLine($"\n*{nbA} fournisseurs (segment A) concentrent {Pct(vA)} des achats.*\n");
        sb.AppendLine("**Principaux fournisseurs (segment A)**\n");
        sb.Append(SageFormat.Table(topA,
            ("Fournisseur", r => SageFormat.Text(r["DO_Tiers"])),
            ("Intitulé", r => SageFormat.Text(r["Intitule"])),
            ("Achats HT", r => SageFormat.Euro(r["Achats"])),
            ("% achats", r => Pct(SageFormat.ToDecimal(r["Achats"])))));
        return sb.ToString();
    }

    // ----- Fournisseurs ----------------------------------------------------------------------------

    [McpServerTool(Name = "sage_nouveaux_fournisseurs")]
    [Description("Fournisseurs nouvellement référencés sur une période : ceux dont la toute première facture d'achat " +
                 "tombe dans l'intervalle, avec le montant acheté depuis. Pendant de sage_nouveaux_clients.")]
    public static async Task<string> NouveauxFournisseurs(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nombre de fournisseurs affichés (défaut 50).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var n = Math.Clamp(limite ?? 50, 1, 300);
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) c.CT_Num, c.CT_Intitule, c.CT_Ville, c.CT_Pays, MIN(d.DO_Date) AS Premiere,
                      SUM(CASE WHEN d.DO_Date BETWEEN @from AND @to THEN d.DO_TotalHT ELSE 0 END) AS Achats_Periode
               FROM F_DOCENTETE d
               JOIN F_COMPTET c ON c.CT_Num = d.DO_Tiers
               WHERE {Factures("d")}
               GROUP BY c.CT_Num, c.CT_Intitule, c.CT_Ville, c.CT_Pays
               HAVING MIN(d.DO_Date) BETWEEN @from AND @to
               ORDER BY Achats_Periode DESC",
            new Dictionary<string, object?> { ["@from"] = from, ["@to"] = to }, ct);

        if (rows.Count == 0) return $"Aucun nouveau fournisseur {SagePeriod.Describe(from, to)}.";

        decimal total = rows.Sum(r => SageFormat.ToDecimal(r["Achats_Periode"]));
        var table = SageFormat.Table(rows,
            ("Fournisseur", r => SageFormat.Text(r["CT_Num"])),
            ("Intitulé", r => SageFormat.Text(r["CT_Intitule"])),
            ("Ville", r => $"{SageFormat.Text(r["CT_Ville"])} {SageFormat.Text(r["CT_Pays"])}".Trim()),
            ("1ʳᵉ facture", r => SageFormat.Date(r["Premiere"])),
            ("Achats HT", r => SageFormat.Euro(r["Achats_Periode"])));

        return $"## Nouveaux fournisseurs {SagePeriod.Describe(from, to)}\n\n" + table +
               $"\n**{rows.Count} nouveau(x) fournisseur(s)** — achats depuis : {SageFormat.Euro(total)}";
    }

    [McpServerTool(Name = "sage_fournisseurs_inactifs")]
    [Description("Fournisseurs actifs (non en sommeil) sans facture d'achat depuis un certain nombre de mois " +
                 "(défaut 12), triés par montant historique acheté — pour nettoyer le fichier fournisseurs ou " +
                 "repérer un approvisionnement abandonné. Pendant de sage_clients_inactifs.")]
    public static async Task<string> FournisseursInactifs(
        SageDatabaseRegistry registry,
        [Description("Nombre de mois d'inactivité (défaut 12).")] int? mois_inactivite = null,
        [Description("Date de référence AAAA-MM-JJ (vide = aujourd'hui).")] string? date_reference = null,
        [Description("true pour inclure les fournisseurs n'ayant jamais été facturés en gestion commerciale (défaut false).")] bool? inclure_jamais_factures = null,
        [Description("Nombre de fournisseurs affichés (défaut 30).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var reference = SagePeriod.ParseDate(date_reference, DateTime.Today);
        var mois = Math.Clamp(mois_inactivite ?? 12, 1, 120);
        var cutoff = reference.AddMonths(-mois);
        var n = Math.Clamp(limite ?? 30, 1, 300);
        var jamais = inclure_jamais_factures == true ? " OR MAX(d.DO_Date) IS NULL" : "";

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) c.CT_Num, c.CT_Intitule, c.CT_Ville,
                      MAX(d.DO_Date) AS Derniere, SUM(d.DO_TotalHT) AS Achats_Total, COUNT(d.DO_Piece) AS NbFact
               FROM F_COMPTET c
               LEFT JOIN F_DOCENTETE d ON d.DO_Tiers = c.CT_Num AND d.DO_Domaine = 1 AND d.DO_Type IN (16, 17)
               WHERE c.CT_Type = 1 AND c.CT_Sommeil = 0
               GROUP BY c.CT_Num, c.CT_Intitule, c.CT_Ville
               HAVING MAX(d.DO_Date) < @cutoff{jamais}
               ORDER BY SUM(d.DO_TotalHT) DESC",
            new Dictionary<string, object?> { ["@cutoff"] = cutoff }, ct);

        if (rows.Count == 0)
            return $"Aucun fournisseur inactif depuis plus de {mois} mois (référence {reference:dd/MM/yyyy}).";

        var table = SageFormat.Table(rows,
            ("Fournisseur", r => SageFormat.Text(r["CT_Num"])),
            ("Intitulé", r => SageFormat.Text(r["CT_Intitule"])),
            ("Ville", r => SageFormat.Text(r["CT_Ville"])),
            ("Dernière facture", r => r["Derniere"] is null ? "jamais" : SageFormat.Date(r["Derniere"])),
            ("Factures", r => SageFormat.ToLong(r["NbFact"]).ToString()),
            ("Achats historiques", r => SageFormat.Euro(r["Achats_Total"])));

        return $"## Fournisseurs inactifs depuis plus de {mois} mois (au {reference:dd/MM/yyyy})\n\n" + table +
               $"\n**{rows.Count} fournisseur(s)** (triés par montant historique acheté).";
    }

    [McpServerTool(Name = "sage_fiche_fournisseur")]
    [Description("Fiche synthétique 360° d'un fournisseur : coordonnées, achats facturés sur une période, dettes non " +
                 "réglées (dont échues), commandes en cours et dernières factures. Pour préparer une négociation " +
                 "ou un point fournisseur. Pendant de sage_fiche_client.")]
    public static async Task<string> FicheFournisseur(
        SageDatabaseRegistry registry,
        [Description("Numéro de compte tiers du fournisseur (ex. 'F001').")] string tiers,
        [Description("Date de début AAAA-MM-JJ pour les achats (vide = 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ pour les achats (vide = 31 décembre).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var t = tiers.Trim();
        var prm = new Dictionary<string, object?> { ["@t"] = t };

        var head = await registry.QueryAsync(base_sage,
            @"SELECT CT_Num, CT_Intitule, CT_Type, CT_Adresse, CT_CodePostal, CT_Ville, CT_Pays,
                     CT_Telephone, CT_EMail, CT_Sommeil
              FROM F_COMPTET WHERE CT_Num = @t", prm, ct);
        if (head.Count == 0) return $"Fournisseur « {tiers} » introuvable.";
        var h = head[0];

        var achats = await registry.QueryAsync(base_sage,
            $@"SELECT SUM(DO_TotalHT) AS Achats, COUNT(*) AS NbFact
               FROM F_DOCENTETE
               WHERE {FacturesAchat} AND DO_Tiers = @t AND DO_Date BETWEEN @from AND @to",
            new Dictionary<string, object?> { ["@t"] = t, ["@from"] = from, ["@to"] = to }, ct);

        // Dette = solde créditeur des écritures 401 non lettrées, à date (pas bornée à la période).
        var dette = await registry.QueryAsync(base_sage,
            @"SELECT SUM(CASE WHEN EC_Sens = 1 THEN EC_Montant ELSE -EC_Montant END) AS Dette,
                     SUM(CASE WHEN EC_Echeance > '19000101' AND EC_Echeance < @today
                              THEN (CASE WHEN EC_Sens = 1 THEN EC_Montant ELSE -EC_Montant END) ELSE 0 END) AS Echue
              FROM F_ECRITUREC
              WHERE CT_Num = @t AND CG_Num LIKE '401%' AND (EC_Lettrage IS NULL OR EC_Lettrage = '')",
            new Dictionary<string, object?> { ["@t"] = t, ["@today"] = DateTime.Today }, ct);

        var commandes = await registry.QueryAsync(base_sage,
            @"SELECT COUNT(*) AS NbCmd, SUM(DO_TotalHT) AS MontantHT
              FROM F_DOCENTETE
              WHERE DO_Domaine = 1 AND DO_Type = 12 AND DO_Cloture = 0 AND DO_Tiers = @t", prm, ct);

        var dernieres = await registry.QueryAsync(base_sage,
            $@"SELECT TOP 5 DO_Piece, DO_Date, DO_Ref, DO_TotalHT, DO_NetAPayer
               FROM F_DOCENTETE
               WHERE {FacturesAchat} AND DO_Tiers = @t
               ORDER BY DO_Date DESC", prm, ct);

        var sb = new StringBuilder($"## Fiche fournisseur {SageFormat.Text(h["CT_Num"])} — {SageFormat.Text(h["CT_Intitule"])}\n\n");
        if (SageFormat.ToLong(h["CT_Type"]) != 1) sb.AppendLine("> ⚠️ Ce tiers n'est pas déclaré comme fournisseur dans Sage.\n");
        if (SageFormat.ToLong(h["CT_Sommeil"]) != 0) sb.AppendLine("> ⚠️ Fournisseur en sommeil\n");
        sb.AppendLine("**Coordonnées**");
        sb.AppendLine($"- {SageFormat.Text(h["CT_Adresse"])}, {SageFormat.Text(h["CT_CodePostal"])} {SageFormat.Text(h["CT_Ville"])} {SageFormat.Text(h["CT_Pays"])}".Trim());
        sb.AppendLine($"- Tél : {SageFormat.Text(h["CT_Telephone"])} · E-mail : {SageFormat.Text(h["CT_EMail"])}");
        sb.AppendLine();
        sb.AppendLine("**Indicateurs**");
        sb.AppendLine($"- Achats facturés {SagePeriod.Describe(from, to)} : **{SageFormat.Euro(achats[0]["Achats"])}** ({SageFormat.ToLong(achats[0]["NbFact"])} factures)");
        sb.AppendLine($"- Dette non réglée (comptes 401) : **{SageFormat.Euro(dette[0]["Dette"])}** — dont échue : {SageFormat.Euro(dette[0]["Echue"])}");
        sb.AppendLine($"- Commandes en cours : {SageFormat.ToLong(commandes[0]["NbCmd"])} pour {SageFormat.Euro(commandes[0]["MontantHT"])} HT");
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

    [McpServerTool(Name = "sage_retards_fournisseurs")]
    [Description("Factures fournisseurs échues et non réglées (comptes 401, non lettrées, échéance dépassée), par " +
                 "fournisseur, avec montant en retard, échéance la plus ancienne et jours de retard maxi. Pour " +
                 "prioriser les paiements et éviter pénalités ou blocages de livraison. Pendant de sage_retards_clients.")]
    public static async Task<string> RetardsFournisseurs(
        SageDatabaseRegistry registry,
        [Description("Date d'arrêté AAAA-MM-JJ (vide = aujourd'hui).")] string? date_arrete = null,
        [Description("Nombre de fournisseurs affichés (défaut 30).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var asOf = SagePeriod.ParseDate(date_arrete, DateTime.Today);
        var n = Math.Clamp(limite ?? 30, 1, 300);
        // F_COMPTET n'est joint qu'après le TOP : la jointure dans l'agrégat sur F_ECRITUREC fait dégénérer le plan.
        var rows = await registry.QueryAsync(base_sage,
            $@"WITH r AS (
                 SELECT TOP ({n}) e.CT_Num,
                        SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE -e.EC_Montant END) AS Retard,
                        MIN(e.EC_Echeance) AS PlusAncienne,
                        MAX(DATEDIFF(day, e.EC_Echeance, @asOf)) AS JoursMax,
                        SUM(SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE -e.EC_Montant END)) OVER () AS GrandTotal,
                        COUNT(*) OVER () AS GrandNb
                 FROM F_ECRITUREC e
                 WHERE e.CG_Num LIKE '401%' AND (e.EC_Lettrage IS NULL OR e.EC_Lettrage = '')
                   AND e.EC_Echeance IS NOT NULL AND e.EC_Echeance > '19000101' AND e.EC_Echeance < @asOf
                   AND e.JM_Date <= @asOf
                   AND e.CT_Num IS NOT NULL AND e.CT_Num <> ''
                 GROUP BY e.CT_Num
                 HAVING SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE -e.EC_Montant END) > 0.005
                 ORDER BY Retard DESC)
               SELECT r.*, c.CT_Intitule AS Intitule
               FROM r LEFT JOIN F_COMPTET c ON c.CT_Num = r.CT_Num
               ORDER BY r.Retard DESC",
            new Dictionary<string, object?> { ["@asOf"] = asOf }, ct);

        if (rows.Count == 0) return $"Aucune facture fournisseur échue non réglée au {asOf:dd/MM/yyyy}.";

        decimal total = SageFormat.ToDecimal(rows[0]["GrandTotal"]);
        long nbTotal = SageFormat.ToLong(rows[0]["GrandNb"]);
        var table = SageFormat.Table(rows,
            ("Fournisseur", r => SageFormat.Text(r["CT_Num"])),
            ("Intitulé", r => SageFormat.Text(r["Intitule"])),
            ("Montant échu", r => SageFormat.Euro(r["Retard"])),
            ("Échéance la + ancienne", r => SageFormat.Date(r["PlusAncienne"])),
            ("Jours de retard", r => $"{SageFormat.ToLong(r["JoursMax"])} j"));

        return $"## Factures fournisseurs échues non réglées au {asOf:dd/MM/yyyy}\n\n" + table +
               $"\n**Total échu : {SageFormat.Euro(total)}** ({nbTotal} fournisseurs, {rows.Count} affichés)";
    }

    // ----- Documents en cours ----------------------------------------------------------------------

    [McpServerTool(Name = "sage_commandes_fournisseurs_en_cours")]
    [Description("Commandes fournisseurs en cours (bons de commande d'achat non clôturés, restant à réceptionner/" +
                 "facturer), regroupées par fournisseur, avec la réception prévue la plus proche et le nombre de " +
                 "commandes en retard de réception. Pendant de sage_commandes_clients_en_cours.")]
    public static async Task<string> CommandesFournisseursEnCours(
        SageDatabaseRegistry registry,
        [Description("Nombre de fournisseurs affichés (défaut 30).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var n = Math.Clamp(limite ?? 30, 1, 300);
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) d.DO_Tiers, MAX(c.CT_Intitule) AS Intitule,
                      COUNT(*) AS NbCmd, SUM(d.DO_TotalHT) AS MontantHT, MIN(d.DO_Date) AS PlusAncienne,
                      MIN(CASE WHEN d.DO_DateLivr > '19000101' THEN d.DO_DateLivr END) AS ProchaineReception,
                      SUM(CASE WHEN d.DO_DateLivr > '19000101' AND d.DO_DateLivr < @today THEN 1 ELSE 0 END) AS NbEnRetard,
                      SUM(SUM(d.DO_TotalHT)) OVER () AS GrandTotal, SUM(COUNT(*)) OVER () AS GrandNb,
                      SUM(SUM(CASE WHEN d.DO_DateLivr > '19000101' AND d.DO_DateLivr < @today THEN 1 ELSE 0 END)) OVER () AS GrandNbRetard
               FROM F_DOCENTETE d
               LEFT JOIN F_COMPTET c ON c.CT_Num = d.DO_Tiers
               WHERE d.DO_Domaine = 1 AND d.DO_Type = 12 AND d.DO_Cloture = 0
               GROUP BY d.DO_Tiers
               ORDER BY MontantHT DESC",
            new Dictionary<string, object?> { ["@today"] = DateTime.Today }, ct);

        if (rows.Count == 0) return "Aucune commande fournisseur en cours.";

        decimal total = SageFormat.ToDecimal(rows[0]["GrandTotal"]);
        long totalNb = SageFormat.ToLong(rows[0]["GrandNb"]);
        long totalRetard = SageFormat.ToLong(rows[0]["GrandNbRetard"]);
        var table = SageFormat.Table(rows,
            ("Fournisseur", r => SageFormat.Text(r["DO_Tiers"])),
            ("Intitulé", r => SageFormat.Text(r["Intitule"])),
            ("Nb cmd", r => SageFormat.ToLong(r["NbCmd"]).ToString()),
            ("Montant HT", r => SageFormat.Euro(r["MontantHT"])),
            ("Plus ancienne", r => SageFormat.Date(r["PlusAncienne"])),
            ("Réception prévue", r => SageFormat.DateOpt(r["ProchaineReception"])),
            ("En retard", r => SageFormat.ToLong(r["NbEnRetard"]).ToString()));

        return "## Commandes fournisseurs en cours\n\n" + table +
               $"\n**Total engagé : {SageFormat.Euro(total)}** sur {totalNb} commandes ({rows.Count} fournisseurs affichés)" +
               $" — dont {totalRetard} en retard de réception (date prévue dépassée)." +
               "\n*Détail ligne à ligne : sage_commandes_fournisseurs_detail.*";
    }

    [McpServerTool(Name = "sage_commandes_fournisseurs_detail")]
    [Description("Détail ligne à ligne des commandes fournisseurs (F_DOCENTETE + F_DOCLIGNE, bons de commande " +
                 "d'achat) : numéro de pièce, date de commande, date de réception prévue, fournisseur, statut, " +
                 "article, quantité et montant HT. Filtrable par fournisseur, pièce, période et date de réception " +
                 "prévue (pour les retards de livraison fournisseur). Pendant de sage_commandes_clients_detail.")]
    public static async Task<string> CommandesFournisseursDetail(
        SageDatabaseRegistry registry,
        [Description("Numéro de compte fournisseur. Vide = tous les fournisseurs.")] string? fournisseur = null,
        [Description("Numéro de pièce de la commande. Vide = toutes les commandes.")] string? piece = null,
        [Description("Commandes passées à partir du AAAA-MM-JJ (vide = pas de limite).")] string? date_debut = null,
        [Description("Commandes passées jusqu'au AAAA-MM-JJ (vide = pas de limite).")] string? date_fin = null,
        [Description("Réception prévue au plus tard le AAAA-MM-JJ (vide = pas de limite ; la date du jour liste les retards).")] string? livraison_avant = null,
        [Description("true pour inclure les commandes soldées/clôturées (défaut false : uniquement l'en-cours).")] bool? inclure_soldees = null,
        [Description("false pour une ligne par commande au lieu d'une ligne par article (défaut true).")] bool? detail_lignes = null,
        [Description("Nombre maximum de commandes retenues (défaut 50, maximum 500). En mode détaillé, toutes les lignes de ces commandes sont affichées.")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
        => await PilotageTools.CommandesDetailAsync(registry, achat: true, fournisseur, piece, date_debut, date_fin,
            livraison_avant, inclure_soldees, detail_lignes, limite, base_sage, ct);

    [McpServerTool(Name = "sage_demandes_achat_en_cours")]
    [Description("Demandes d'achat (documents d'achat DO_Type = 10) non transformées en commande, regroupées par " +
                 "fournisseur, avec ancienneté. Pendant de sage_devis_clients_en_cours côté achats.")]
    public static async Task<string> DemandesAchatEnCours(
        SageDatabaseRegistry registry,
        [Description("Nombre de fournisseurs affichés (défaut 30).")] int? limite = null,
        [Description("Ancienneté en mois au-delà de laquelle une demande est signalée comme à traiter (défaut 1).")] int? mois_relance = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var n = Math.Clamp(limite ?? 30, 1, 300);
        var mois = Math.Clamp(mois_relance ?? 1, 1, 120);
        var seuil = DateTime.Today.AddMonths(-mois);

        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) d.DO_Tiers, MAX(c.CT_Intitule) AS Intitule,
                      COUNT(*) AS NbDemandes, SUM(d.DO_TotalHT) AS MontantHT,
                      MIN(d.DO_Date) AS PlusAncienne, MAX(d.DO_Date) AS PlusRecente,
                      SUM(CASE WHEN d.DO_Date < @seuil THEN 1 ELSE 0 END) AS NbAnciennes,
                      SUM(SUM(d.DO_TotalHT)) OVER () AS GrandTotal, SUM(COUNT(*)) OVER () AS GrandNb
               FROM F_DOCENTETE d
               LEFT JOIN F_COMPTET c ON c.CT_Num = d.DO_Tiers
               WHERE d.DO_Domaine = 1 AND d.DO_Type = 10 AND d.DO_Cloture = 0
               GROUP BY d.DO_Tiers
               ORDER BY MontantHT DESC",
            new Dictionary<string, object?> { ["@seuil"] = seuil }, ct);

        if (rows.Count == 0) return "Aucune demande d'achat en cours.";

        decimal total = SageFormat.ToDecimal(rows[0]["GrandTotal"]);
        long totalNb = SageFormat.ToLong(rows[0]["GrandNb"]);
        var table = SageFormat.Table(rows,
            ("Fournisseur", r => SageFormat.Text(r["DO_Tiers"])),
            ("Intitulé", r => SageFormat.Text(r["Intitule"])),
            ("Nb demandes", r => SageFormat.ToLong(r["NbDemandes"]).ToString()),
            ("Montant HT", r => SageFormat.Euro(r["MontantHT"])),
            ("Plus ancienne", r => SageFormat.Date(r["PlusAncienne"])),
            ("Plus récente", r => SageFormat.Date(r["PlusRecente"])),
            ($"Dont > {mois} mois", r => SageFormat.ToLong(r["NbAnciennes"]).ToString()));

        return "## Demandes d'achat en cours\n\n" + table +
               $"\n**Total : {SageFormat.Euro(total)}** sur {totalNb} demandes ({rows.Count} fournisseurs affichés)";
    }

    [McpServerTool(Name = "sage_detail_facture_fournisseur")]
    [Description("Détail d'une facture d'achat (fournisseur) : entête (fournisseur, date, totaux) et lignes " +
                 "(articles, quantités, prix, montants). Recherche par numéro de pièce. Pendant de sage_detail_facture.")]
    public static async Task<string> DetailFactureFournisseur(
        SageDatabaseRegistry registry,
        [Description("Numéro de pièce de la facture d'achat (ex. 'FA009424').")] string piece,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
        => await SuiviTools.DetailFactureAsync(registry, achat: true, piece, base_sage, ct);
}
