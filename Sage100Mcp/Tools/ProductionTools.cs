using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Sage100Mcp.Data;

namespace Sage100Mcp.Tools;

/// <summary>
/// Outils de production lus dans les <b>bons de fabrication</b> (F_DOCLIGNE, DO_Domaine = 2, DO_Type = 26) et non
/// dans F_NOMENCLAT : beaucoup de dossiers fabriquent sans tenir leurs nomenclatures à jour, la réalité est dans les
/// bons. Une fabrication = (DO_Piece, DO_Date, AR_RefCompose) : les numéros de pièce sont réutilisés d'une année sur
/// l'autre et un bon peut fabriquer plusieurs composés.
/// <list type="bullet">
/// <item>composant consommé : AR_Ref ≠ AR_RefCompose et DL_MvtStock = 3 (sortie), valorisé au CMUP dans DL_MontantHT ;</item>
/// <item>quantité fabriquée : ligne du composé lui-même (AR_Ref = AR_RefCompose) en entrée, DL_MvtStock = 1. Elle
/// manque sur une bonne part des bons ; on l'estime alors depuis les composants consommés (voir <see cref="Cte"/>) ;</item>
/// <item>la valeur d'entrée du composé n'est pas exploitée : elle ne correspond pas au coût des composants et contient
/// des valeurs aberrantes. Le coût de revient affiché est donc un <b>coût matière</b>.</item>
/// </list>
/// </summary>
[McpServerToolType]
public sealed class ProductionTools
{
    [McpServerTool(Name = "sage_fabrications")]
    [Description("Volumes fabriqués d'après les bons de fabrication Sage : nombre de fabrications, quantités " +
                 "fabriquées et coût matière consommé, par composé (défaut) ou par mois. Quand un bon ne déclare " +
                 "pas la quantité fabriquée, elle est estimée depuis les composants consommés (signalé dans le " +
                 "tableau). Coût matière = composants valorisés au CMUP, hors main d'œuvre.")]
    public static async Task<string> Fabrications(
        SageDatabaseRegistry registry,
        [Description("Date de début AAAA-MM-JJ (défaut : 1er janvier de l'année en cours).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (défaut : 31 décembre de l'année en cours).")] string? date_fin = null,
        [Description("Regroupement : 'compose' (défaut) ou 'mois'.")] string? regroupement = null,
        [Description("Filtre sur la référence du composé (recherche partielle). Vide = tous.")] string? article = null,
        [Description("Tri par composé : 'quantite' (défaut), 'cout' ou 'fabrications'.")] string? tri = null,
        [Description("Nombre maximum de composés (défaut 30).")] int? top = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        var (from, to) = SagePeriod.Resolve(date_debut, date_fin);
        var n = Math.Clamp(top ?? 30, 1, 500);
        var prms = new Dictionary<string, object?>();
        var filtre = "";
        if (!string.IsNullOrWhiteSpace(article))
        {
            filtre = " AND AR_RefCompose LIKE @compose";
            prms["@compose"] = $"%{article.Trim()}%";
        }

        var fabs = await ChargerFabricationsAsync(registry, base_sage, filtre, prms, from, to, ct);
        if (fabs.Count == 0)
            return $"Aucun bon de fabrication {SagePeriod.Describe(from, to)}" +
                   (string.IsNullOrWhiteSpace(article) ? "." : $" pour un composé contenant « {article.Trim()} ».");

        var parMois = (regroupement?.Trim().ToLowerInvariant()) is "mois" or "mensuel";
        var sb = new StringBuilder();

        if (parMois)
        {
            var mois = fabs.GroupBy(f => new DateTime(f.Date.Year, f.Date.Month, 1)).OrderBy(g => g.Key).ToList();
            sb.Append($"## Fabrications par mois {SagePeriod.Describe(from, to)}\n\n");
            sb.Append(Tableau(mois,
                ("Mois", g => $"{SageFormat.MonthsFr[g.Key.Month]} {g.Key.Year}"),
                ("Fabrications", g => g.Count().ToString("N0", SageFormat.Fr)),
                ("Composés", g => g.Select(f => f.Compose).Distinct().Count().ToString("N0", SageFormat.Fr)),
                ("Qté fabriquée", g => Qte(g.Sum(f => f.Quantite))),
                ("dont estimée", g => Qte(g.Where(f => f.Estimee).Sum(f => f.Quantite))),
                ("Coût matière", g => SageFormat.Euro(g.Sum(f => f.CoutMatiere)))));
        }
        else
        {
            var composes = fabs.GroupBy(f => f.Compose, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Compose = g.Key,
                    Designation = g.Select(f => f.Designation).FirstOrDefault(d => d.Length > 0) ?? "",
                    Nb = g.Count(),
                    Quantite = g.Sum(f => f.Quantite),
                    Estimee = g.Where(f => f.Estimee).Sum(f => f.Quantite),
                    Cout = g.Sum(f => f.CoutMatiere),
                    CoutUnitaire = CoutUnitaire(g),
                    Derniere = g.Max(f => f.Date)
                });
            var lignes = ((tri?.Trim().ToLowerInvariant()) switch
            {
                "cout" or "coût" => composes.OrderByDescending(c => c.Cout),
                "fabrications" or "nombre" => composes.OrderByDescending(c => c.Nb),
                _ => composes.OrderByDescending(c => c.Quantite).ThenByDescending(c => c.Nb)
            }).Take(n).ToList();

            var designations = await DesignationsAsync(registry, base_sage,
                lignes.Where(c => c.Designation.Length == 0).Select(c => c.Compose), ct);

            sb.Append($"## Fabrications par composé {SagePeriod.Describe(from, to)}\n\n");
            sb.Append(Tableau(lignes,
                ("Composé", c => Txt(c.Compose)),
                ("Désignation", c => Txt(c.Designation.Length > 0 ? c.Designation : designations.GetValueOrDefault(c.Compose, ""))),
                ("Fabrications", c => c.Nb.ToString("N0", SageFormat.Fr)),
                ("Qté fabriquée", c => Qte(c.Quantite)),
                ("dont estimée", c => c.Estimee > 0 ? Qte(c.Estimee) : "—"),
                ("Coût matière", c => SageFormat.Euro(c.Cout)),
                ("Coût matière / unité", c => c.CoutUnitaire is decimal u ? SageFormat.Euro(u) : "—"),
                ("Dernière", c => SageFormat.Date(c.Derniere))));
        }

        var inconnues = fabs.Count(f => f.Quantite == 0);
        sb.Append($"\n**Total : {fabs.Count:N0} fabrications · {Qte(fabs.Sum(f => f.Quantite))} unités fabriquées " +
                  $"(dont {Qte(fabs.Where(f => f.Estimee).Sum(f => f.Quantite))} estimées) · " +
                  $"coût matière {SageFormat.Euro(fabs.Sum(f => f.CoutMatiere))}**");
        if (!parMois)
            sb.Append($" — {fabs.Select(f => f.Compose).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} composés");
        sb.Append('\n');
        sb.Append(NoteQuantites(fabs.Count(f => f.Estimee), inconnues));
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_cout_revient_fabrication")]
    [Description("Coût de revient matière réel d'un article fabriqué, calculé depuis ses bons de fabrication : " +
                 "coût des composants consommés (au CMUP) rapporté à la quantité fabriquée, son évolution par année, " +
                 "trimestre ou mois, et le détail des dernières fabrications. Hors main d'œuvre et frais. " +
                 "Par défaut sur les 5 dernières années civiles.")]
    public static async Task<string> CoutRevientFabrication(
        SageDatabaseRegistry registry,
        [Description("Référence exacte de l'article fabriqué (composé).")] string? article = null,
        [Description("Périodicité de l'évolution : 'annee' (défaut), 'trimestre' ou 'mois'.")] string? periodicite = null,
        [Description("Date de début AAAA-MM-JJ (défaut : 1er janvier, 4 ans avant l'année de fin).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (défaut : 31 décembre de l'année en cours).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(article))
            return "Précisez la référence de l'article fabriqué (paramètre `article`).";
        var reference = article.Trim();
        var (from, to) = ResolveHistorique(date_debut, date_fin, 5);

        var fabs = await ChargerFabricationsAsync(registry, base_sage, " AND AR_RefCompose = @compose",
            new Dictionary<string, object?> { ["@compose"] = reference }, from, to, ct);
        if (fabs.Count == 0)
            return $"Aucune fabrication de « {reference} » {SagePeriod.Describe(from, to)}." +
                   await SuggestionsAsync(registry, base_sage, reference, compose: true, ct);

        var designation = await DesignationComposeAsync(registry, base_sage, reference, fabs, ct);
        var chiffrables = fabs.Where(f => f.Chiffrable).ToList();
        if (chiffrables.Count == 0)
            return $"## Coût de revient matière — {reference} {designation}\n\n" +
                   $"{fabs.Count} fabrication(s) {SagePeriod.Describe(from, to)}, mais aucune n'est chiffrable : " +
                   "quantité fabriquée inconnue (ni déclarée, ni estimable) ou aucun composant consommé.";

        var (cle, libelle) = Periodicite(periodicite);
        var periodes = chiffrables.GroupBy(f => cle(f.Date)).OrderBy(g => g.Key).ToList();

        var sb = new StringBuilder($"## Coût de revient matière — {reference} {designation}\n\n");
        sb.Append($"*{SagePeriod.Describe(from, to)} · coût des composants consommés (CMUP) / quantité fabriquée, " +
                  "hors main d'œuvre et frais.*\n\n");
        sb.Append(Tableau(periodes,
            ("Période", g => libelle(g.First().Date)),
            ("Fabrications", g => g.Count().ToString("N0", SageFormat.Fr)),
            ("Qté fabriquée", g => Qte(g.Sum(f => f.Quantite))),
            ("Coût matière", g => SageFormat.Euro(g.Sum(f => f.CoutMatiere))),
            ("Coût unitaire moyen", g => SageFormat.Euro(CoutUnitaire(g) ?? 0)),
            ("Mini", g => SageFormat.Euro(g.Min(f => f.CoutMatiere / f.Quantite))),
            ("Maxi", g => SageFormat.Euro(g.Max(f => f.CoutMatiere / f.Quantite)))));

        var dernieres = chiffrables.OrderByDescending(f => f.Date).ThenByDescending(f => f.Piece).Take(10).ToList();
        sb.Append("\n### Dernières fabrications\n\n");
        sb.Append(Tableau(dernieres,
            ("Date", f => SageFormat.Date(f.Date)),
            ("Bon", f => Txt(f.Piece)),
            ("Qté", f => f.Estimee ? $"~{Qte(f.Quantite)}" : Qte(f.Quantite)),
            ("Composants", f => f.NbComposants.ToString()),
            ("Coût matière", f => SageFormat.Euro(f.CoutMatiere)),
            ("Coût unitaire", f => SageFormat.Euro(f.CoutMatiere / f.Quantite))));

        var moyen = CoutUnitaire(chiffrables) ?? 0;
        var premier = CoutUnitaire(periodes[0]) ?? 0;
        var dernier = CoutUnitaire(periodes[^1]) ?? 0;
        sb.Append($"\n**Coût unitaire moyen : {SageFormat.Euro(moyen)}** sur {chiffrables.Count} fabrication(s) chiffrée(s)");
        if (periodes.Count > 1 && premier > 0)
            sb.Append($" · évolution {libelle(periodes[0].First().Date)} → {libelle(periodes[^1].First().Date)} : " +
                      string.Format(SageFormat.Fr, "{0:+0.0;-0.0;0} %", (dernier - premier) / premier * 100));
        sb.Append('\n');
        if (dernieres.Any(f => f.Estimee)) sb.Append("\n*~ = quantité non déclarée sur le bon, estimée depuis les composants consommés.*\n");
        sb.Append(NoteQuantites(0, fabs.Count - chiffrables.Count, horsCalcul: true));
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_nomenclature_constatee")]
    [Description("Nomenclature réellement consommée d'un article fabriqué, reconstituée depuis ses bons de " +
                 "fabrication : chaque composant, sa fréquence d'utilisation, sa quantité par unité fabriquée " +
                 "(médiane), son prix moyen et sa part du coût matière. Si une nomenclature est déclarée dans Sage " +
                 "(F_NOMENCLAT), elle est mise en regard pour repérer les écarts. Par défaut sur les 2 dernières " +
                 "années civiles.")]
    public static async Task<string> NomenclatureConstatee(
        SageDatabaseRegistry registry,
        [Description("Référence exacte de l'article fabriqué (composé).")] string? article = null,
        [Description("Date de début AAAA-MM-JJ (défaut : 1er janvier de l'année précédente).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (défaut : 31 décembre de l'année en cours).")] string? date_fin = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(article))
            return "Précisez la référence de l'article fabriqué (paramètre `article`).";
        var reference = article.Trim();
        var (from, to) = ResolveHistorique(date_debut, date_fin, 2);
        var prms = new Dictionary<string, object?> { ["@compose"] = reference };

        var lignes = await registry.QueryAsync(base_sage,
            $@"{Cte(" AND AR_RefCompose = @compose")}
               SELECT DO_Piece, DO_Date, AR_Ref, Design, QSortie, MSortie, QteDeclaree, QteEstimee
               FROM e
               WHERE EstComposant = 1 AND DO_Date BETWEEN @from AND @to
               OPTION (MAXDOP 1)",
            AvecPeriode(prms, from, to), ct);
        if (lignes.Count == 0)
        {
            var declaree = await NomenclatureDeclareeAsync(registry, base_sage, reference, ct);
            return $"Aucune consommation de composants pour « {reference} » {SagePeriod.Describe(from, to)}." +
                   (declaree.Count > 0
                       ? $"\n\nNomenclature déclarée dans Sage (F_NOMENCLAT) : {declaree.Count} composant(s) — " +
                         string.Join(", ", declaree.Select(kv => $"`{kv.Key}` × {kv.Value}")) + "."
                       : await SuggestionsAsync(registry, base_sage, reference, compose: true, ct));
        }

        // Quantité fabriquée de chaque fabrication : déclarée, sinon estimée (même règle que Fabrication.Quantite).
        var fabs = lignes
            .GroupBy(r => (Piece: SageFormat.Text(r["DO_Piece"]), Date: (DateTime)r["DO_Date"]!))
            .ToDictionary(g => g.Key, g => Quantite(SageFormat.ToDecimal(g.First()["QteDeclaree"]), Nullable(g.First()["QteEstimee"])));
        var nbFabs = fabs.Count;
        var nbEstimees = fabs.Count(kv => kv.Value.Estimee);
        var qteTotale = fabs.Values.Sum(q => q.Quantite);

        var composants = lignes
            .GroupBy(r => SageFormat.Text(r["AR_Ref"]), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var parFab = g.Select(r => (Fab: fabs[(SageFormat.Text(r["DO_Piece"]), (DateTime)r["DO_Date"]!)],
                                            Qte: SageFormat.ToDecimal(r["QSortie"]), Montant: SageFormat.ToDecimal(r["MSortie"])))
                              .ToList();
                var quantifiees = parFab.Where(x => x.Fab.Quantite > 0).ToList();
                var qte = parFab.Sum(x => x.Qte);
                return new ComposantConstate(
                    g.Key,
                    g.Select(r => SageFormat.Text(r["Design"])).FirstOrDefault(d => d.Length > 0) ?? "",
                    parFab.Count,
                    Mediane(quantifiees.Select(x => x.Qte / x.Fab.Quantite)),
                    qte != 0 ? parFab.Sum(x => x.Montant) / qte : 0,
                    // Montant consommé / quantité fabriquée, sur toutes les fabrications quantifiées : la somme sur
                    // les composants redonne le coût matière unitaire moyen.
                    qteTotale > 0 ? quantifiees.Sum(x => x.Montant) / qteTotale : 0);
            })
            .OrderByDescending(c => c.CoutParUnite).ThenByDescending(c => c.Nb)
            .ToList();

        var nomenclature = await NomenclatureDeclareeAsync(registry, base_sage, reference, ct);

        var designation = await DesignationComposeAsync(registry, base_sage, reference, [], ct);
        var colonnes = new List<(string, Func<ComposantConstate, string>)>
        {
            ("Composant", c => Txt(c.Ref)),
            ("Désignation", c => Txt(c.Designation)),
            ("Présent dans", c => $"{c.Nb}/{nbFabs} ({(decimal)c.Nb / nbFabs:P0})"),
            ("Qté / unité", c => c.QteParUnite is decimal q ? Qte(q) : "—"),
            ("Prix moyen", c => SageFormat.Euro(c.Prix)),
            ("Coût / unité", c => SageFormat.Euro(c.CoutParUnite))
        };
        if (nomenclature.Count > 0)
            colonnes.Insert(4, ("Qté nomenclature Sage", c => nomenclature.GetValueOrDefault(c.Ref, "absent")));

        var sb = new StringBuilder($"## Nomenclature constatée — {reference} {designation}\n\n");
        sb.Append($"*D'après {nbFabs} fabrication(s) {SagePeriod.Describe(from, to)} · " +
                  $"{Qte(qteTotale)} unités fabriquées.*\n\n");
        sb.Append(Tableau(composants, colonnes.ToArray()));
        sb.Append($"\n**Coût matière moyen par unité : {SageFormat.Euro(composants.Sum(c => c.CoutParUnite))}** " +
                  $"({composants.Count} composants distincts)\n");

        if (nomenclature.Count > 0)
        {
            var nonConsommes = nomenclature.Keys.Where(k => composants.All(c => !string.Equals(c.Ref, k, StringComparison.OrdinalIgnoreCase))).ToList();
            if (nonConsommes.Count > 0)
                sb.Append($"\nComposants de la nomenclature Sage jamais consommés sur la période : {string.Join(", ", nonConsommes.Select(k => $"`{k}`"))}.\n");
        }
        else
            sb.Append("\n*Aucune nomenclature déclarée dans Sage (F_NOMENCLAT) pour cet article.*\n");

        sb.Append("\n*Qté / unité = médiane sur les fabrications quantifiées ; un composant présent dans une partie " +
                  "seulement des fabrications est une variante ou un remplacement.*\n");
        sb.Append(NoteQuantites(nbEstimees, fabs.Count(kv => kv.Value.Quantite == 0)));
        return sb.ToString();
    }

    [McpServerTool(Name = "sage_cas_emploi_composant")]
    [Description("Cas d'emploi d'un composant : les articles fabriqués qui le consomment d'après les bons de " +
                 "fabrication, avec le nombre de fabrications, la quantité consommée, la quantité par unité " +
                 "fabriquée et la valeur consommée. Utile pour mesurer l'impact d'une rupture ou d'une hausse de prix. " +
                 "Liste aussi les nomenclatures Sage (F_NOMENCLAT) qui le déclarent. Par défaut sur les 2 dernières " +
                 "années civiles.")]
    public static async Task<string> CasEmploiComposant(
        SageDatabaseRegistry registry,
        [Description("Référence exacte du composant.")] string? article = null,
        [Description("Date de début AAAA-MM-JJ (défaut : 1er janvier de l'année précédente).")] string? date_debut = null,
        [Description("Date de fin AAAA-MM-JJ (défaut : 31 décembre de l'année en cours).")] string? date_fin = null,
        [Description("Nombre maximum d'articles fabriqués listés (défaut 30).")] int? top = null,
        [Description("Nom de la base Sage (vide = base par défaut).")] string? base_sage = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(article))
            return "Précisez la référence du composant (paramètre `article`).";
        var reference = article.Trim();
        var (from, to) = ResolveHistorique(date_debut, date_fin, 2);
        var n = Math.Clamp(top ?? 30, 1, 500);

        // Le sous-filtre passe par l'index Sage sur AR_Ref (colonne réelle) : seuls les composés qui ont un jour
        // consommé ce composant sont balayés, sur tout leur historique pour l'estimation des quantités.
        var fabs = (await ChargerFabricationsAsync(registry, base_sage,
                " AND AR_RefCompose IN (SELECT AR_RefCompose FROM F_DOCLIGNE " +
                "WHERE DO_Domaine = 2 AND DO_Type = 26 AND AR_Ref = @cible)",
                new Dictionary<string, object?> { ["@cible"] = reference }, from, to, ct))
            .Where(f => f.QteCible != 0)
            .ToList();

        var designations = await DesignationsAsync(registry, base_sage, [reference], ct);
        var titre = $"{reference} {designations.GetValueOrDefault(reference, "")}".Trim();
        var sb = new StringBuilder($"## Cas d'emploi — {titre}\n\n");

        if (fabs.Count == 0)
            sb.Append($"Aucune fabrication n'a consommé ce composant {SagePeriod.Describe(from, to)}.\n");
        else
        {
            var composes = fabs.GroupBy(f => f.Compose, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var quantifiees = g.Where(f => f.Quantite > 0).ToList();
                    var qteFab = quantifiees.Sum(f => f.Quantite);
                    return new
                    {
                        Compose = g.Key,
                        Designation = g.Select(f => f.Designation).FirstOrDefault(d => d.Length > 0) ?? "",
                        Nb = g.Count(),
                        Consommee = g.Sum(f => f.QteCible),
                        QteFab = qteFab,
                        ParUnite = qteFab > 0 ? quantifiees.Sum(f => f.QteCible) / qteFab : (decimal?)null,
                        Valeur = g.Sum(f => f.MontantCible),
                        Derniere = g.Max(f => f.Date)
                    };
                })
                .OrderByDescending(c => c.Consommee).ThenByDescending(c => c.Nb)
                .ToList();
            var affiches = composes.Take(n).ToList();
            var manquantes = await DesignationsAsync(registry, base_sage,
                affiches.Where(c => c.Designation.Length == 0).Select(c => c.Compose), ct);

            sb.Append($"*D'après les bons de fabrication {SagePeriod.Describe(from, to)}.*\n\n");
            sb.Append(Tableau(affiches,
                ("Article fabriqué", c => Txt(c.Compose)),
                ("Désignation", c => Txt(c.Designation.Length > 0 ? c.Designation : manquantes.GetValueOrDefault(c.Compose, ""))),
                ("Fabrications", c => c.Nb.ToString("N0", SageFormat.Fr)),
                ("Qté consommée", c => Qte(c.Consommee)),
                ("Qté fabriquée", c => c.QteFab > 0 ? Qte(c.QteFab) : "—"),
                ("Qté / unité", c => c.ParUnite is decimal q ? Qte(q) : "—"),
                ("Valeur consommée", c => SageFormat.Euro(c.Valeur)),
                ("Dernière", c => SageFormat.Date(c.Derniere))));
            sb.Append($"\n**Total consommé : {Qte(fabs.Sum(f => f.QteCible))} (unité du composant) dans {fabs.Count:N0} " +
                      $"fabrications de {composes.Count:N0} articles · valeur {SageFormat.Euro(fabs.Sum(f => f.MontantCible))}**");
            if (composes.Count > affiches.Count) sb.Append($" ({affiches.Count} premiers affichés)");
            sb.Append('\n');
            sb.Append(NoteQuantites(fabs.Count(f => f.Estimee), fabs.Count(f => f.Quantite == 0)));
        }

        var declares = await registry.QueryAsync(base_sage,
            $@"SELECT TOP ({n}) n.AR_Ref, MAX(a.AR_Design) AS AR_Design, MIN(n.NO_Qte) AS QteMin, MAX(n.NO_Qte) AS QteMax
               FROM F_NOMENCLAT n
               LEFT JOIN F_ARTICLE a ON a.AR_Ref = n.AR_Ref
               WHERE n.NO_RefDet = @cible
               GROUP BY n.AR_Ref
               ORDER BY n.AR_Ref",
            new Dictionary<string, object?> { ["@cible"] = reference }, ct);
        if (declares.Count > 0)
        {
            sb.Append("\n### Nomenclatures Sage qui déclarent ce composant\n\n");
            sb.Append(SageFormat.Table(declares,
                ("Article", r => Txt(r["AR_Ref"])),
                ("Désignation", r => Txt(r["AR_Design"])),
                ("Qté nomenclature", r => QteNomenclature(r))));
        }
        else if (fabs.Count == 0)
            sb.Append(await SuggestionsAsync(registry, base_sage, reference, compose: false, ct));

        return sb.ToString();
    }

    // --- Socle commun ----------------------------------------------------------------------------------------

    /// <summary>Une fabrication : un composé sur un bon de fabrication (pièce + date).</summary>
    private sealed record Fabrication(
        string Piece, DateTime Date, string Compose, string Designation,
        decimal QteDeclaree, decimal? QteEstimee, decimal CoutMatiere, int NbComposants,
        decimal QteCible, decimal MontantCible)
    {
        private (decimal Quantite, bool Estimee) Retenue => ProductionTools.Quantite(QteDeclaree, QteEstimee);
        /// <summary>Quantité déclarée, sinon estimée ; 0 si inconnue.</summary>
        public decimal Quantite => Retenue.Quantite;
        public bool Estimee => Retenue.Estimee;
        public bool Chiffrable => Quantite > 0 && NbComposants > 0;
    }

    private sealed record ComposantConstate(
        string Ref, string Designation, int Nb, decimal? QteParUnite, decimal Prix, decimal CoutParUnite);

    /// <summary>Quantité retenue : déclarée, sinon estimée arrondie à l'unité (au moins 1), sinon 0.</summary>
    private static (decimal Quantite, bool Estimee) Quantite(decimal declaree, decimal? estimee)
        => declaree > 0 ? (declaree, false)
         : estimee is decimal e && e > 0 ? (Math.Max(1, Math.Round(e, MidpointRounding.AwayFromZero)), true)
         : (0, false);

    /// <summary>
    /// Chaîne de CTE sur les lignes de bons de fabrication, en <b>un seul balayage</b> de F_DOCLIGNE : chaque CTE
    /// n'est référencée qu'une fois et tout le reste passe par des fonctions de fenêtrage (une CTE référencée deux
    /// fois serait rebalayée, et une jointure sur DO_Piece fait dégénérer le plan). Expose <c>e</c>, une ligne par
    /// (fabrication, article) :
    /// <list type="bullet">
    /// <item><c>QteDeclaree</c> : quantité du composé entrée en stock sur le bon (0 si la ligne manque) ;</item>
    /// <item><c>QteParUnite</c> : consommation du composant par unité fabriquée, Σ consommé / Σ fabriqué sur les
    /// fabrications du même composé qui déclarent leur quantité — sur <b>tout l'historique</b>, la borne de date
    /// n'étant appliquée qu'après ;</item>
    /// <item><c>QteEstimee</c> : pour une fabrication sans quantité déclarée, Σ consommé / Σ consommation par unité
    /// sur ses composants. Validé sur CRISTAL_ET_BRONZE : la quantité exacte est retrouvée dans 87 % des cas.</item>
    /// </list>
    /// Une médiane (PERCENTILE_CONT … OVER) serait un peu plus juste mais ne rend jamais la main sur ce volume ;
    /// pour la même raison, les requêtes qui utilisent cette chaîne imposent <c>OPTION (MAXDOP 1)</c> : en parallèle,
    /// les spools de fenêtrage tournent plusieurs minutes là où le plan série prend une dizaine de secondes.
    /// </summary>
    private static string Cte(string filtre) => $@"
        WITH g AS (
            SELECT DO_Piece, DO_Date, AR_RefCompose AS Compose, AR_Ref,
                   SUM(CASE WHEN DL_MvtStock = 1 THEN DL_Qte ELSE 0 END) AS QEntree,
                   SUM(CASE WHEN DL_MvtStock = 3 THEN DL_Qte ELSE 0 END) AS QSortie,
                   SUM(CASE WHEN DL_MvtStock = 3 THEN DL_MontantHT ELSE 0 END) AS MSortie,
                   MAX(DL_Design) AS Design
            FROM F_DOCLIGNE
            WHERE DO_Domaine = 2 AND DO_Type = 26 AND AR_RefCompose <> ''{filtre}
            GROUP BY DO_Piece, DO_Date, AR_RefCompose, AR_Ref
        ),
        f AS (
            SELECT g.*, CASE WHEN AR_Ref <> Compose AND QSortie > 0 THEN 1 ELSE 0 END AS EstComposant,
                   SUM(CASE WHEN AR_Ref = Compose THEN QEntree ELSE 0 END)
                       OVER (PARTITION BY DO_Piece, DO_Date, Compose) AS QteDeclaree
            FROM g
        ),
        r AS (
            SELECT f.*,
                   SUM(CASE WHEN EstComposant = 1 AND QteDeclaree > 0 THEN QSortie END) OVER (PARTITION BY Compose, AR_Ref)
                 / NULLIF(SUM(CASE WHEN EstComposant = 1 AND QteDeclaree > 0 THEN QteDeclaree END)
                       OVER (PARTITION BY Compose, AR_Ref), 0) AS QteParUnite
            FROM f
        ),
        e AS (
            SELECT r.*,
                   CASE WHEN QteDeclaree = 0 THEN
                       SUM(CASE WHEN EstComposant = 1 AND QteParUnite > 0 THEN QSortie END)
                           OVER (PARTITION BY DO_Piece, DO_Date, Compose)
                     / NULLIF(SUM(CASE WHEN EstComposant = 1 AND QteParUnite > 0 THEN QteParUnite END)
                           OVER (PARTITION BY DO_Piece, DO_Date, Compose), 0)
                   END AS QteEstimee
            FROM r
        )";

    /// <summary>Charge les fabrications de la période, une ligne par (pièce, date, composé).</summary>
    private static async Task<List<Fabrication>> ChargerFabricationsAsync(
        SageDatabaseRegistry registry, string? baseSage, string filtre, Dictionary<string, object?> prms,
        DateTime from, DateTime to, CancellationToken ct)
    {
        prms.TryAdd("@cible", DBNull.Value);
        var rows = await registry.QueryAsync(baseSage,
            $@"{Cte(filtre)}
               SELECT DO_Piece, DO_Date, Compose,
                      MAX(CASE WHEN AR_Ref = Compose THEN Design END) AS Design,
                      MAX(QteDeclaree) AS QteDeclaree, MAX(QteEstimee) AS QteEstimee,
                      SUM(CASE WHEN EstComposant = 1 THEN MSortie ELSE 0 END) AS CoutMatiere,
                      SUM(EstComposant) AS NbComposants,
                      SUM(CASE WHEN EstComposant = 1 AND AR_Ref = @cible THEN QSortie ELSE 0 END) AS QteCible,
                      SUM(CASE WHEN EstComposant = 1 AND AR_Ref = @cible THEN MSortie ELSE 0 END) AS MontantCible
               FROM e
               WHERE DO_Date BETWEEN @from AND @to
               GROUP BY DO_Piece, DO_Date, Compose
               OPTION (MAXDOP 1)",
            AvecPeriode(prms, from, to), ct);

        return rows.Select(r => new Fabrication(
                SageFormat.Text(r["DO_Piece"]), (DateTime)r["DO_Date"]!, SageFormat.Text(r["Compose"]),
                SageFormat.Text(r["Design"]),
                SageFormat.ToDecimal(r["QteDeclaree"]), Nullable(r["QteEstimee"]),
                SageFormat.ToDecimal(r["CoutMatiere"]), (int)SageFormat.ToLong(r["NbComposants"]),
                SageFormat.ToDecimal(r["QteCible"]), SageFormat.ToDecimal(r["MontantCible"])))
            .ToList();
    }

    private static Dictionary<string, object?> AvecPeriode(Dictionary<string, object?> prms, DateTime from, DateTime to)
    {
        prms["@from"] = from;
        prms["@to"] = to;
        return prms;
    }

    /// <summary>Période « historique » : comme <see cref="SagePeriod.Resolve"/>, mais remonte de <paramref name="annees"/>
    /// années civiles quand la date de début est omise (un coût de revient ou une nomenclature se lit sur la durée).</summary>
    private static (DateTime From, DateTime To) ResolveHistorique(string? dateDebut, string? dateFin, int annees)
    {
        var (from, to) = SagePeriod.Resolve(dateDebut, dateFin);
        if (string.IsNullOrWhiteSpace(dateDebut))
            from = new DateTime(to.Year - (annees - 1), 1, 1);
        return (from, to);
    }

    private static (Func<DateTime, int> Cle, Func<DateTime, string> Libelle) Periodicite(string? valeur)
        => (valeur?.Trim().ToLowerInvariant()) switch
        {
            "mois" or "mensuel" or "mensuelle" =>
                (d => d.Year * 100 + d.Month, d => $"{SageFormat.MonthsFr[d.Month]} {d.Year}"),
            "trimestre" or "trimestriel" or "trimestrielle" =>
                (d => d.Year * 10 + (d.Month - 1) / 3 + 1, d => $"T{(d.Month - 1) / 3 + 1} {d.Year}"),
            _ => (d => d.Year, d => d.Year.ToString())
        };

    /// <summary>Coût matière unitaire moyen : Σ coût / Σ quantité sur les fabrications chiffrables.</summary>
    private static decimal? CoutUnitaire(IEnumerable<Fabrication> fabs)
    {
        var chiffrables = fabs.Where(f => f.Chiffrable).ToList();
        var qte = chiffrables.Sum(f => f.Quantite);
        return qte > 0 ? chiffrables.Sum(f => f.CoutMatiere) / qte : null;
    }

    private static decimal? Mediane(IEnumerable<decimal> valeurs)
    {
        var tri = valeurs.OrderBy(v => v).ToList();
        if (tri.Count == 0) return null;
        var m = tri.Count / 2;
        return tri.Count % 2 == 1 ? tri[m] : (tri[m - 1] + tri[m]) / 2;
    }

    private static decimal? Nullable(object? valeur) => valeur is null ? null : SageFormat.ToDecimal(valeur);

    private static string NoteQuantites(int estimees, int inconnues, bool horsCalcul = false)
    {
        var sb = new StringBuilder();
        if (estimees > 0)
            sb.Append($"\n*{estimees:N0} fabrication(s) sans quantité déclarée sur le bon : quantité estimée depuis les " +
                      "composants consommés, par comparaison avec les fabrications du même article qui la déclarent.*\n");
        if (inconnues > 0)
            sb.Append($"\n*{inconnues:N0} fabrication(s) sans quantité déclarée ni estimable (article jamais fabriqué " +
                      $"avec une quantité déclarée){(horsCalcul ? " ou sans composant : exclues du calcul" : " : comptées sans quantité")}.*\n");
        return sb.ToString();
    }

    /// <summary>Désignations F_ARTICLE d'une liste de références, en une requête (recherche par AR_Ref).</summary>
    private static async Task<Dictionary<string, string>> DesignationsAsync(
        SageDatabaseRegistry registry, string? baseSage, IEnumerable<string> refs, CancellationToken ct)
    {
        var liste = refs.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (liste.Count == 0) return result;

        var prms = new Dictionary<string, object?>();
        for (var i = 0; i < liste.Count; i++) prms[$"@r{i}"] = liste[i];
        var rows = await registry.QueryAsync(baseSage,
            $"SELECT AR_Ref, AR_Design FROM F_ARTICLE WHERE AR_Ref IN ({string.Join(", ", prms.Keys)})", prms, ct);
        foreach (var r in rows) result[SageFormat.Text(r["AR_Ref"])] = SageFormat.Text(r["AR_Design"]);
        return result;
    }

    private static async Task<string> DesignationComposeAsync(
        SageDatabaseRegistry registry, string? baseSage, string reference, IEnumerable<Fabrication> fabs, CancellationToken ct)
    {
        var designation = fabs.Select(f => f.Designation).FirstOrDefault(d => d.Length > 0);
        designation ??= (await DesignationsAsync(registry, baseSage, [reference], ct)).GetValueOrDefault(reference, "");
        return designation.Length > 0 ? $"({Txt(designation)})" : "";
    }

    /// <summary>
    /// Nomenclature déclarée d'un composé : composant → quantité. F_NOMENCLAT porte une ligne par gamme (énuméré)
    /// de l'article ou du composant, d'où une fourchette « 1 à 3 » quand la quantité varie d'une gamme à l'autre.
    /// </summary>
    private static async Task<Dictionary<string, string>> NomenclatureDeclareeAsync(
        SageDatabaseRegistry registry, string? baseSage, string compose, CancellationToken ct)
    {
        var rows = await registry.QueryAsync(baseSage,
            @"SELECT NO_RefDet, MIN(NO_Qte) AS QteMin, MAX(NO_Qte) AS QteMax
              FROM F_NOMENCLAT WHERE AR_Ref = @compose
              GROUP BY NO_RefDet ORDER BY MIN(NO_Ordre)",
            new Dictionary<string, object?> { ["@compose"] = compose }, ct);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) result[SageFormat.Text(r["NO_RefDet"])] = QteNomenclature(r);
        return result;
    }

    private static string QteNomenclature(IReadOnlyDictionary<string, object?> r)
    {
        var min = SageFormat.ToDecimal(r["QteMin"]);
        var max = SageFormat.ToDecimal(r["QteMax"]);
        return min == max ? Qte(min) : $"{Qte(min)} à {Qte(max)}";
    }

    /// <summary>Références proches trouvées dans les bons de fabrication, pour une référence inconnue.</summary>
    private static async Task<string> SuggestionsAsync(
        SageDatabaseRegistry registry, string? baseSage, string recherche, bool compose, CancellationToken ct)
    {
        var sql = compose
            ? @"SELECT TOP (10) AR_RefCompose AS Ref, MAX(CASE WHEN AR_Ref = AR_RefCompose THEN DL_Design END) AS Design
                FROM F_DOCLIGNE
                WHERE DO_Domaine = 2 AND DO_Type = 26
                  AND (AR_RefCompose LIKE @x OR (AR_Ref = AR_RefCompose AND DL_Design LIKE @x))
                GROUP BY AR_RefCompose
                ORDER BY COUNT(*) DESC"
            : @"SELECT TOP (10) AR_Ref AS Ref, MAX(DL_Design) AS Design
                FROM F_DOCLIGNE
                WHERE DO_Domaine = 2 AND DO_Type = 26 AND AR_Ref <> AR_RefCompose
                  AND (AR_Ref LIKE @x OR DL_Design LIKE @x)
                GROUP BY AR_Ref
                ORDER BY COUNT(*) DESC";
        var rows = await registry.QueryAsync(baseSage, sql,
            new Dictionary<string, object?> { ["@x"] = $"%{recherche}%" }, ct);
        if (rows.Count == 0) return "";
        return $"\n\n{(compose ? "Articles fabriqués" : "Composants")} approchants dans les bons de fabrication : " +
               string.Join(", ", rows.Select(r =>
               {
                   var d = SageFormat.Text(r["Design"]);
                   return d.Length > 0 ? $"`{SageFormat.Text(r["Ref"])}` ({Txt(d)})" : $"`{SageFormat.Text(r["Ref"])}`";
               })) + ".";
    }

    /// <summary>Quantité : entière sans décimale, sinon jusqu'à 3 décimales.</summary>
    private static string Qte(decimal v) => v.ToString("#,##0.###", SageFormat.Fr);

    /// <summary>Texte de cellule : les « | » des désignations casseraient le tableau Markdown.</summary>
    private static string Txt(object? v) => SageFormat.Text(v).Replace("|", "/");

    private static string Tableau<T>(IReadOnlyList<T> items, params (string Header, Func<T, string> Cell)[] colonnes)
    {
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", colonnes.Select(c => c.Header))).AppendLine(" |");
        sb.Append('|').Append(string.Join("|", colonnes.Select(_ => "---"))).AppendLine("|");
        foreach (var item in items)
            sb.Append("| ").Append(string.Join(" | ", colonnes.Select(c => c.Cell(item)))).AppendLine(" |");
        return sb.ToString();
    }
}
