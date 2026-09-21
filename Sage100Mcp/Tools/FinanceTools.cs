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
              WHERE (e.CG_Num LIKE '4457%' OR e.CG_Num LIKE '4456%') AND e.JM_Date BETWEEN @from AND @to
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
                AND e.JM_Date <= @asOf
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
            "e.EC_Echeance IS NOT NULL AND e.EC_Echeance > '19000101'"
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

    [McpServerTool(Name = "sage_echeancier_detaille")]
    [Description("Échéancier détaillé, une ligne par échéance non réglée : date d'échéance, retard, tiers, pièce, " +
                 "libellé et montant, avec cumul progressif de trésorerie. Couvre les créances clients (411) et les " +
                 "dettes fournisseurs (401) sur une plage de dates. Version ligne à ligne de " +
                 "sage_previsionnel_encaissements, qui, lui, agrège par mois.")]
    public static async Task<string> EcheancierDetaille(
        SageDatabaseRegistry registry,
        [Description("Échéances à partir du AAAA-MM-JJ (vide = toutes, y compris celles déjà en retard).")] string? date_debut = null,
        [Description("Échéances jusqu'au AAAA-MM-JJ (vide = pas de limite).")] string? date_fin = null,
        [Description("Sens retenu : 'encaissements' (clients), 'decaissements' (fournisseurs) ou 'tous' (défaut).")] string? sens = null,
        [Description("Numéro de compte tiers pour se limiter à un client ou un fournisseur (vide = tous).")] string? tiers = null,
        [Description("Nombre maximum de lignes (défaut 200, maximum 2000).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var n = Math.Clamp(limite ?? 200, 1, 2000);

        var demande = (sens ?? "tous").Trim().ToLowerInvariant();
        string racines;
        if (demande is "" or "tous" or "tout") racines = "(e.CG_Num LIKE '411%' OR e.CG_Num LIKE '401%')";
        else if (demande.StartsWith("enc") || demande.StartsWith("cli")) racines = "e.CG_Num LIKE '411%'";
        else if (demande.StartsWith("dec") || demande.StartsWith("déc") || demande.StartsWith("fou")) racines = "e.CG_Num LIKE '401%'";
        else throw new ArgumentException($"Sens inconnu : '{sens}'. Valeurs attendues : encaissements, decaissements, tous.");

        var filtres = new List<string>
        {
            racines,
            "(e.EC_Lettrage IS NULL OR e.EC_Lettrage = '')",
            "e.EC_Echeance IS NOT NULL AND e.EC_Echeance > '19000101'"
        };
        var prm = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(date_debut)) { filtres.Add("e.EC_Echeance >= @from"); prm["@from"] = SagePeriod.ParseDate(date_debut, DateTime.Today); }
        if (!string.IsNullOrWhiteSpace(date_fin)) { filtres.Add("e.EC_Echeance <= @to"); prm["@to"] = SagePeriod.ParseDate(date_fin, DateTime.Today).AddDays(1).AddTicks(-1); }
        if (!string.IsNullOrWhiteSpace(tiers)) { filtres.Add("e.CT_Num = @tiers"); prm["@tiers"] = tiers.Trim(); }

        // Les totaux sont calculés par fenêtre sur l'ensemble du jeu de résultats : le TOP n'ampute
        // que l'affichage, pas le total annoncé en pied de tableau.
        var rows = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) e.EC_Echeance, e.CT_Num, c.CT_Intitule, e.EC_Piece, e.EC_RefPiece,
                      e.JM_Date, e.JO_Num, e.EC_Intitule, e.CG_Num, e.EC_Sens, e.EC_Montant,
                      SUM(CASE WHEN e.CG_Num LIKE '411%' THEN (CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE -e.EC_Montant END) ELSE 0 END) OVER () AS TotalEnc,
                      SUM(CASE WHEN e.CG_Num LIKE '401%' THEN (CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE -e.EC_Montant END) ELSE 0 END) OVER () AS TotalDec,
                      COUNT(*) OVER () AS GrandNb
               FROM F_ECRITUREC e
               LEFT JOIN F_COMPTET c ON c.CT_Num = e.CT_Num
               WHERE {string.Join(" AND ", filtres)}
               ORDER BY e.EC_Echeance, e.CT_Num, e.EC_Piece", prm, ct);

        if (rows.Count == 0) return "Aucune échéance non réglée ne correspond à ces critères.";

        var aujourdhui = DateTime.Today;
        var sb = new StringBuilder("## Échéancier détaillé des règlements attendus\n\n");
        sb.AppendLine("| Échéance | Retard | Sens | Tiers | Intitulé | Pièce | Réf. | Date pièce | Montant | Cumul |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        decimal cumul = 0, afficheEnc = 0, afficheDec = 0;
        foreach (var r in rows)
        {
            var client = SageFormat.Text(r["CG_Num"]).StartsWith("411");
            // Créance client : le débit est à encaisser. Dette fournisseur : le crédit est à payer.
            var montant = SageFormat.ToLong(r["EC_Sens"]) == (client ? 0 : 1)
                ? SageFormat.ToDecimal(r["EC_Montant"])
                : -SageFormat.ToDecimal(r["EC_Montant"]);
            if (client) afficheEnc += montant; else afficheDec += montant;
            cumul += client ? montant : -montant;

            var echeance = r["EC_Echeance"] as DateTime?;
            var retard = echeance is DateTime e && e.Date < aujourdhui
                ? $"{(aujourdhui - e.Date).Days} j"
                : "—";

            sb.AppendLine($"| {SageFormat.DateOpt(r["EC_Echeance"])} | {retard} | {(client ? "Encaissement" : "Décaissement")} " +
                          $"| {SageFormat.Text(r["CT_Num"])} | {SageFormat.Text(r["CT_Intitule"])} " +
                          $"| {SageFormat.Text(r["EC_Piece"])} | {SageFormat.Text(r["EC_RefPiece"])} " +
                          $"| {SageFormat.DateOpt(r["JM_Date"])} | {SageFormat.Euro(montant)} | {SageFormat.Euro(cumul)} |");
        }

        var totalEnc = SageFormat.ToDecimal(rows[0]["TotalEnc"]);
        var totalDec = SageFormat.ToDecimal(rows[0]["TotalDec"]);
        var grandNb = SageFormat.ToLong(rows[0]["GrandNb"]);

        sb.AppendLine($"\n**Encaissements attendus : {SageFormat.Euro(totalEnc)}** · " +
                      $"**décaissements prévus : {SageFormat.Euro(totalDec)}** · " +
                      $"**solde net : {SageFormat.Euro(totalEnc - totalDec)}** ({grandNb} échéance(s))");
        if (rows.Count < grandNb)
            sb.AppendLine($"\n*{rows.Count} échéance(s) affichée(s) sur {grandNb} : le cumul de la dernière ligne ne couvre " +
                          $"que les lignes affichées ({SageFormat.Euro(afficheEnc - afficheDec)} net). Affinez la plage de dates ou augmentez `limite`.*");
        return sb.ToString();
    }

    /// <summary>Une échéance d'emprunt ou de crédit-bail projetée à partir de la cadence constatée.</summary>
    private sealed record EcheanceProjetee(DateTime Date, decimal Capital, decimal Interets, decimal Restant);

    [McpServerTool(Name = "sage_echeancier_emprunts")]
    [Description("Emprunts et crédits-bails : capital restant dû par contrat (comptes 16x) et échéancier prévisionnel " +
                 "reconstitué — date, capital amorti, intérêts, annuité et capital restant dû après échéance. " +
                 "Sage ne stocke aucun tableau d'amortissement : la projection est déduite de la cadence et des " +
                 "montants réellement constatés en comptabilité. Traite aussi les redevances de crédit-bail (612x), " +
                 "qui n'ont pas de composante capital. Complète les outils de charges, où seuls les intérêts " +
                 "(classe 6) apparaissent, sans le remboursement du capital.")]
    public static async Task<string> EcheancierEmprunts(
        SageDatabaseRegistry registry,
        [Description("Préfixe de compte à analyser (ex. '164150' pour un seul emprunt). Vide = emprunts 16x et crédits-bails 612x.")] string? compte = null,
        [Description("Date d'arrêté AAAA-MM-JJ (vide = aujourd'hui).")] string? date_arrete = null,
        [Description("Nombre d'échéances futures projetées par contrat (défaut 12, maximum 60).")] int? nb_echeances = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var asOf = SagePeriod.ParseDate(date_arrete, DateTime.Today);
        var nb = Math.Clamp(nb_echeances ?? 12, 1, 60);

        var filtreCompte = string.IsNullOrWhiteSpace(compte)
            ? "(e.CG_Num LIKE '16%' OR e.CG_Num LIKE '612%')"
            : "e.CG_Num LIKE @cpt";
        var prm = new Dictionary<string, object?> { ["@asOf"] = asOf };
        if (!string.IsNullOrWhiteSpace(compte)) prm["@cpt"] = $"{compte.Trim()}%";

        var mouvements = await registry.QueryAsync(base_sage,
            $@"SELECT e.CG_Num, MAX(g.CG_Intitule) AS Intitule, e.JM_Date, e.JO_Num, e.EC_ANType,
                      SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END) AS Debit,
                      SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END) AS Credit
               FROM F_ECRITUREC e
               LEFT JOIN F_COMPTEG g ON g.CG_Num = e.CG_Num
               WHERE {filtreCompte} AND e.JM_Date <= @asOf
               GROUP BY e.CG_Num, e.JM_Date, e.JO_Num, e.EC_ANType
               ORDER BY e.CG_Num, e.JM_Date", prm, ct);

        if (mouvements.Count == 0)
            return $"Aucun mouvement sur les comptes d'emprunt (16x) ou de crédit-bail (612x) au {asOf:dd/MM/yyyy}.";

        // Les intérêts (661x) ne partagent avec la ligne de capital ni le numéro de pièce ni le compte :
        // le journal et la date de l'échéance sont la seule clé commune fiable.
        var interetsRows = await registry.QueryAsync(base_sage,
            @"SELECT i.JO_Num, i.JM_Date,
                     SUM(CASE WHEN i.EC_Sens = 0 THEN i.EC_Montant ELSE -i.EC_Montant END) AS Interets
              FROM F_ECRITUREC i
              WHERE i.CG_Num LIKE '661%' AND i.EC_ANType = 0 AND i.JM_Date <= @asOf
              GROUP BY i.JO_Num, i.JM_Date",
            new Dictionary<string, object?> { ["@asOf"] = asOf }, ct);

        static DateTime DateDe(object? v) => v as DateTime? ?? DateTime.MinValue;

        var interetsParCle = new Dictionary<(string, DateTime), decimal>();
        foreach (var r in interetsRows)
            interetsParCle[(SageFormat.Text(r["JO_Num"]), DateDe(r["JM_Date"]))] = SageFormat.ToDecimal(r["Interets"]);

        // Quand plusieurs emprunts sont remboursés le même jour dans le même journal, les intérêts
        // constatés sont communs : on les répartit au prorata du capital amorti par chaque contrat.
        var capitalParCle = new Dictionary<(string, DateTime), decimal>();
        foreach (var r in mouvements)
        {
            if (!SageFormat.Text(r["CG_Num"]).StartsWith("16")) continue;
            if (SageFormat.ToLong(r["EC_ANType"]) != 0) continue;
            var debit = SageFormat.ToDecimal(r["Debit"]);
            if (debit <= 0) continue;
            var cle = (SageFormat.Text(r["JO_Num"]), DateDe(r["JM_Date"]));
            capitalParCle[cle] = capitalParCle.GetValueOrDefault(cle) + debit;
        }

        var synthese = new StringBuilder();
        var detail = new StringBuilder();
        var fluxParMois = new SortedDictionary<DateTime, (decimal Capital, decimal Interets)>();
        decimal totalCrd = 0;
        var nbContrats = 0;

        foreach (var groupe in mouvements.GroupBy(r => SageFormat.Text(r["CG_Num"])).OrderBy(g => g.Key))
        {
            var numero = groupe.Key;
            var estEmprunt = numero.StartsWith("16");
            var lignes = groupe.OrderBy(r => DateDe(r["JM_Date"])).ToList();
            var intitule = SageFormat.Text(lignes[0]["Intitule"]);
            nbContrats++;

            // Capital restant dû : on repart du dernier à-nouveau, qui porte déjà le solde reporté.
            // Cumuler depuis l'origine compterait deux fois tous les exercices antérieurs.
            var depart = lignes.FindLastIndex(r => SageFormat.ToLong(r["EC_ANType"]) != 0);
            if (depart < 0) depart = 0;
            decimal crd = 0;
            for (var i = depart; i < lignes.Count; i++)
                crd += SageFormat.ToDecimal(lignes[i]["Credit"]) - SageFormat.ToDecimal(lignes[i]["Debit"]);

            // Échéances constatées : les débits hors à-nouveaux, soit le capital amorti (emprunt)
            // ou la redevance passée en charge (crédit-bail).
            var echeances = lignes
                .Where(r => SageFormat.ToLong(r["EC_ANType"]) == 0 && SageFormat.ToDecimal(r["Debit"]) > 0)
                .Select(r =>
                {
                    var cle = (SageFormat.Text(r["JO_Num"]), DateDe(r["JM_Date"]));
                    var capital = SageFormat.ToDecimal(r["Debit"]);
                    var totalCle = capitalParCle.GetValueOrDefault(cle);
                    var interet = estEmprunt && totalCle > 0 && interetsParCle.TryGetValue(cle, out var it)
                        ? it * capital / totalCle
                        : 0m;
                    return (Date: cle.Item2, Capital: capital, Interets: interet);
                })
                .ToList();

            if (echeances.Count == 0 && crd <= 0.005m) { nbContrats--; continue; }
            if (estEmprunt) totalCrd += crd;

            var cadence = SageCadence.Detecter(echeances.Select(e => e.Date).ToList());
            var derniere = echeances.Count > 0 ? echeances[^1] : default;
            var annuite = derniere.Capital + derniere.Interets;

            synthese.AppendLine(
                $"| {numero} | {intitule} | {(estEmprunt ? "Emprunt" : "Crédit-bail")} " +
                $"| {(estEmprunt ? SageFormat.Euro(crd) : "—")} " +
                $"| {(echeances.Count > 0 ? SageFormat.DateOpt(derniere.Date) : "—")} " +
                $"| {cadence?.Libelle ?? "—"} " +
                $"| {(echeances.Count > 0 ? SageFormat.Euro(estEmprunt ? annuite : derniere.Capital) : "—")} " +
                $"| {echeances.Count} |");

            var projection = new List<EcheanceProjetee>();
            var note = "";
            if (cadence is null || !cadence.Reguliere || echeances.Count == 0)
            {
                note = echeances.Count < 2
                    ? "Trop peu d'échéances constatées pour déduire une cadence."
                    : "Cadence irrégulière : aucune projection fiable.";
            }
            else if (estEmprunt && crd > 0.5m)
            {
                // Taux périodique déduit de la dernière échéance : les intérêts portent sur le capital
                // restant dû *avant* ce remboursement.
                var crdAvant = crd + derniere.Capital;
                var taux = crdAvant > 0 ? derniere.Interets / crdAvant : 0m;
                var restant = crd;
                for (var k = 1; k <= nb && restant > 0.005m; k++)
                {
                    var interet = Math.Round(restant * taux, 2);
                    var capital = annuite > 0 ? annuite - interet : derniere.Capital;
                    if (capital <= 0)
                    {
                        note = "L'annuité constatée ne couvre pas les intérêts projetés : projection interrompue.";
                        break;
                    }
                    if (capital > restant) capital = restant;
                    restant = Math.Round(restant - capital, 2);
                    projection.Add(new EcheanceProjetee(SageCadence.Suivante(derniere.Date, cadence, k), capital, interet, restant));
                }
                if (projection.Count > 0 && projection[^1].Restant > 0.005m)
                    note = $"Capital non soldé au terme des {nb} échéances projetées : augmentez `nb_echeances` pour voir la fin du prêt.";
            }
            else if (!estEmprunt)
            {
                var moyenne = Math.Round(echeances.TakeLast(3).Average(e => e.Capital), 2);
                for (var k = 1; k <= nb; k++)
                    projection.Add(new EcheanceProjetee(SageCadence.Suivante(derniere.Date, cadence, k), moyenne, 0m, 0m));
                note = "Redevances de crédit-bail projetées à cadence et montant constants : la durée résiduelle du " +
                       "contrat n'est pas connue de la comptabilité, recoupez avec l'échéancier du bailleur.";
            }

            if (projection.Count == 0 && string.IsNullOrEmpty(note)) continue;

            detail.AppendLine($"\n### {numero} — {intitule}\n");
            if (projection.Count > 0)
            {
                detail.AppendLine(estEmprunt
                    ? "| Échéance | Capital | Intérêts | Annuité | Capital restant dû |"
                    : "| Échéance | Redevance |");
                detail.AppendLine(estEmprunt ? "|---|---|---|---|---|" : "|---|---|");
                foreach (var e in projection)
                {
                    detail.AppendLine(estEmprunt
                        ? $"| {e.Date:dd/MM/yyyy} | {SageFormat.Euro(e.Capital)} | {SageFormat.Euro(e.Interets)} " +
                          $"| {SageFormat.Euro(e.Capital + e.Interets)} | {SageFormat.Euro(e.Restant)} |"
                        : $"| {e.Date:dd/MM/yyyy} | {SageFormat.Euro(e.Capital)} |");

                    var mois = new DateTime(e.Date.Year, e.Date.Month, 1);
                    var cumul = fluxParMois.GetValueOrDefault(mois);
                    fluxParMois[mois] = (cumul.Capital + e.Capital, cumul.Interets + e.Interets);
                }
                detail.AppendLine($"\n*Total projeté : {SageFormat.Euro(projection.Sum(e => e.Capital + e.Interets))} " +
                                  $"sur {projection.Count} échéance(s).*");
            }
            if (!string.IsNullOrEmpty(note)) detail.AppendLine($"\n> {note}");
        }

        if (nbContrats == 0)
            return $"Aucun emprunt ni crédit-bail actif au {asOf:dd/MM/yyyy}.";

        var sb = new StringBuilder($"## Emprunts et crédits-bails au {asOf:dd/MM/yyyy}\n\n");
        sb.AppendLine("| Compte | Intitulé | Nature | Capital restant dû | Dernière échéance | Cadence | Dernière annuité | Échéances constatées |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        sb.Append(synthese);
        sb.AppendLine($"\n**Capital restant dû (emprunts 16x) : {SageFormat.Euro(totalCrd)}** sur {nbContrats} contrat(s).");
        sb.Append(detail);

        if (fluxParMois.Count > 0)
        {
            sb.AppendLine("\n### Décaissements projetés par mois (tous contrats)\n");
            sb.AppendLine("| Mois | Capital | Intérêts / redevances | Total |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var (mois, flux) in fluxParMois)
                sb.AppendLine($"| {SageFormat.MonthsFr[mois.Month]} {mois.Year} | {SageFormat.Euro(flux.Capital)} " +
                              $"| {SageFormat.Euro(flux.Interets)} | {SageFormat.Euro(flux.Capital + flux.Interets)} |");
        }

        sb.AppendLine("\n*Projection reconstituée : Sage ne conserve pas les tableaux d'amortissement. La cadence, " +
                      "l'annuité et le taux périodique sont déduits des dernières échéances comptabilisées, les " +
                      "intérêts étant rapprochés des comptes 661x par journal et par date. À recouper avec les " +
                      "tableaux d'amortissement des organismes prêteurs avant tout engagement.*");
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_cadence_charges_fixes")]
    [Description("Cadence réelle des charges fixes, lue dans le grand livre de banque (journaux de trésorerie) : " +
                 "pour chaque poste récurrent — fournisseur prélevé ou compte de charge — la périodicité constatée " +
                 "(hebdomadaire, mensuelle, trimestrielle, annuelle…), le jour du mois habituel, le montant moyen, " +
                 "la dernière occurrence, la prochaine échéance attendue et le coût annualisé. " +
                 "Permet de caler un prévisionnel sur les dates réelles de décaissement (le 5 de chaque mois, " +
                 "un trimestre sur l'autre…) au lieu de lisser les charges à la semaine.")]
    public static async Task<string> CadenceChargesFixes(
        SageDatabaseRegistry registry,
        [Description("Profondeur d'historique analysée, en mois (défaut 18, maximum 60).")] int? mois_historique = null,
        [Description("Date d'arrêté AAAA-MM-JJ (vide = aujourd'hui).")] string? date_arrete = null,
        [Description("Nombre minimum d'occurrences pour retenir un poste (défaut 3).")] int? occurrences_min = null,
        [Description("Montant minimum d'un décaissement pour être pris en compte, en euros (défaut 0).")] double? montant_min = null,
        [Description("Code d'un journal de trésorerie pour se limiter à une banque (vide = toutes).")] string? journal = null,
        [Description("true pour afficher aussi les postes à cadence irrégulière (défaut false).")] bool? inclure_irregulier = null,
        [Description("Nombre de postes affichés (défaut 40, maximum 300).")] int? limite = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var asOf = SagePeriod.ParseDate(date_arrete, DateTime.Today);
        var mois = Math.Clamp(mois_historique ?? 18, 1, 60);
        var depuis = asOf.AddMonths(-mois);
        var minOccurrences = Math.Clamp(occurrences_min ?? 3, 2, 100);
        var seuil = (decimal)Math.Max(0, montant_min ?? 0);
        var n = Math.Clamp(limite ?? 40, 1, 300);
        var avecIrreguliers = inclure_irregulier ?? false;

        var filtreJournal = string.IsNullOrWhiteSpace(journal) ? "" : " AND e.JO_Num = @journal";
        // Le seuil de montant est appliqué en mémoire : passé en paramètre d'un HAVING, il faisait
        // dégénérer le plan d'exécution du regroupement (quelques dizaines de ms → plusieurs minutes).
        var prm = new Dictionary<string, object?> { ["@depuis"] = depuis, ["@asOf"] = asOf };
        if (!string.IsNullOrWhiteSpace(journal)) prm["@journal"] = journal.Trim();

        // Le grand livre de banque proprement dit ne porte souvent qu'une contrepartie centralisée :
        // ce sont les lignes des journaux de trésorerie (JO_Type = 2) qui identifient le poste payé.
        // Un décaissement s'y lit au débit de la contrepartie ; on écarte les comptes de trésorerie
        // eux-mêmes, les virements internes et les règlements clients.
        var rows = await registry.QueryAsync(base_sage,
            $@"WITH decaissements AS (
                   SELECT e.JM_Date,
                          CASE WHEN e.CT_Num IS NULL OR e.CT_Num = '' THEN e.CG_Num ELSE e.CT_Num END AS Cle,
                          CASE WHEN e.CT_Num IS NULL OR e.CT_Num = '' THEN 'Compte' ELSE 'Tiers' END AS Nature,
                          e.EC_Montant
                   FROM F_ECRITUREC e
                   JOIN F_JOURNAUX j ON j.JO_Num = e.JO_Num
                   WHERE j.JO_Type = 2 AND e.EC_Sens = 0 AND e.EC_ANType = 0
                     AND e.JM_Date >= @depuis AND e.JM_Date <= @asOf
                     AND e.CG_Num NOT LIKE '411%' AND e.CG_Num NOT LIKE '51%'
                     AND e.CG_Num NOT LIKE '53%' AND e.CG_Num NOT LIKE '58%'{filtreJournal}
               )
               SELECT Cle, MAX(Nature) AS Nature, JM_Date, SUM(EC_Montant) AS Montant
               FROM decaissements
               GROUP BY Cle, JM_Date
               ORDER BY Cle, JM_Date", prm, ct);

        if (rows.Count == 0)
            return $"Aucun décaissement de trésorerie entre le {depuis:dd/MM/yyyy} et le {asOf:dd/MM/yyyy}.";

        // Les intitulés sont lus à part : joindre F_COMPTET — une table de plus de deux cents colonnes —
        // à l'intérieur du regroupement fait basculer le plan en boucles imbriquées, et la requête ne
        // rend plus la main. Les deux tables de libellés tiennent en quelques milliers de lignes.
        var libellesComptes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in await registry.QueryAsync(base_sage, "SELECT CG_Num, CG_Intitule FROM F_COMPTEG", null, ct))
            libellesComptes[SageFormat.Text(r["CG_Num"])] = SageFormat.Text(r["CG_Intitule"]);
        var libellesTiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in await registry.QueryAsync(base_sage, "SELECT CT_Num, CT_Intitule FROM F_COMPTET", null, ct))
            libellesTiers[SageFormat.Text(r["CT_Num"])] = SageFormat.Text(r["CT_Intitule"]);

        var postes = new List<(string Cle, string Nature, string Libelle, SageCadenceInfo Cadence,
                               int Occurrences, decimal Moyenne, decimal Dernier, DateTime Derniere,
                               DateTime Prochaine, decimal CoutAnnuel)>();
        var ecartes = 0;

        foreach (var groupe in rows.GroupBy(r => SageFormat.Text(r["Cle"])))
        {
            var occurrences = groupe
                .Select(r => (Date: r["JM_Date"] as DateTime? ?? DateTime.MinValue, Montant: SageFormat.ToDecimal(r["Montant"])))
                .Where(o => o.Montant >= seuil)
                .OrderBy(o => o.Date)
                .ToList();
            if (occurrences.Count < minOccurrences) continue;

            var cadence = SageCadence.Detecter(occurrences.Select(o => o.Date).ToList());
            if (cadence is null) continue;
            if (!cadence.Reguliere && !avecIrreguliers) { ecartes++; continue; }

            var derniere = occurrences[^1];
            var moyenne = occurrences.Average(o => o.Montant);
            var coutAnnuel = moyenne * 365m / cadence.JoursMedian;

            var nature = SageFormat.Text(groupe.First()["Nature"]);
            var libelle = (nature == "Tiers" ? libellesTiers : libellesComptes).GetValueOrDefault(groupe.Key, "");

            postes.Add((groupe.Key,
                        nature,
                        libelle,
                        cadence,
                        occurrences.Count,
                        Math.Round(moyenne, 2),
                        derniere.Montant,
                        derniere.Date,
                        SageCadence.Suivante(derniere.Date, cadence),
                        Math.Round(coutAnnuel, 2)));
        }

        if (postes.Count == 0)
            return $"Aucune charge récurrente détectée entre le {depuis:dd/MM/yyyy} et le {asOf:dd/MM/yyyy} " +
                   $"(minimum {minOccurrences} occurrences)." +
                   (ecartes > 0 ? $" {ecartes} poste(s) récurrent(s) mais à cadence irrégulière ont été écartés : passez `inclure_irregulier` à true pour les voir." : "");

        var affiches = postes.OrderByDescending(p => p.CoutAnnuel).Take(n).ToList();
        var totalAnnuel = postes.Sum(p => p.CoutAnnuel);

        var sb = new StringBuilder($"## Cadence des charges fixes — grand livre de banque du {depuis:dd/MM/yyyy} au {asOf:dd/MM/yyyy}\n\n");
        sb.AppendLine("| Poste | Nature | Intitulé | Cadence | Jour type | Occ. | Montant moyen | Dernier montant | Dernière | Prochaine attendue | Coût annualisé |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var p in affiches)
            sb.AppendLine($"| {p.Cle} | {p.Nature} | {p.Libelle} | {p.Cadence.Libelle} " +
                          $"| {(p.Cadence.JourDuMois is int j ? $"le {j}" : "—")} | {p.Occurrences} " +
                          $"| {SageFormat.Euro(p.Moyenne)} | {SageFormat.Euro(p.Dernier)} " +
                          $"| {p.Derniere:dd/MM/yyyy} | {p.Prochaine:dd/MM/yyyy} | {SageFormat.Euro(p.CoutAnnuel)} |");

        sb.AppendLine($"\n**{postes.Count} poste(s) récurrent(s) détecté(s)** — coût annualisé total : " +
                      $"**{SageFormat.Euro(totalAnnuel)}**" +
                      (affiches.Count < postes.Count ? $" ({affiches.Count} affichés, triés par coût décroissant)." : "."));
        if (ecartes > 0)
            sb.AppendLine($"\n*{ecartes} poste(s) récurrent(s) mais à cadence irrégulière ont été écartés " +
                          $"(`inclure_irregulier` = true pour les afficher).*");
        sb.AppendLine("\n*Cadence et jour type déduits des dates réellement comptabilisées dans les journaux de " +
                      "trésorerie : la prochaine échéance est une projection, pas un engagement contractuel. " +
                      "Les règlements clients, les virements internes et les mouvements entre comptes de " +
                      "trésorerie sont exclus.*");
        return sb.ToString();
    }
}
