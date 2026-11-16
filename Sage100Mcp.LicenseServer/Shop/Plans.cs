namespace Sage100Mcp.LicenseServer.Shop;

/// <summary>
/// Offre commerciale. <see cref="AllowedTools"/> est recopiée dans la licence à sa création et à chaque
/// changement d'abonnement ; null = aucune restriction (tous les outils, y compris ceux à venir).
/// </summary>
public sealed record Plan(string Code, string Name, int MonthlyPriceEur, IReadOnlyList<string>? AllowedTools,
    int MaxMachines);

/// <summary>
/// Catalogue des offres. Les prix affichés ici ne servent qu'à l'information : le montant réellement
/// facturé est celui du prix Stripe configuré dans <c>Stripe:Prices:&lt;code&gt;</c>.
/// <para>
/// ⚠️ Comme <c>TOOL_GROUPS</c> dans <c>wwwroot/admin/admin.js</c>, les listes ci-dessous recopient les noms
/// d'outils <c>sage_*</c> : un nouvel outil doit être ajouté à l'offre qui le contient, sinon les abonnés
/// Compta et Gestion commerciale ne le verront pas. Les licences existantes reprennent la liste à jour au
/// prochain événement Stripe de leur abonnement (renouvellement mensuel au plus tard).
/// </para>
/// </summary>
public static class Plans
{
    public const string Trial = "essai";
    public const string Compta = "compta";
    public const string Gescom = "gescom";
    public const string Production = "production";

    /// <summary>Durée de l'essai gratuit, toutes options.</summary>
    public const int TrialDays = 30;

    /// <summary>Outils qui ne lisent que la comptabilité (F_ECRITUREC, F_COMPTEG, F_COMPTET, F_JOURNAUX, règlements).</summary>
    private static readonly string[] ComptaTools =
    [
        // Pilotage financier
        "sage_tableau_de_bord", "sage_chiffre_affaires", "sage_compte_resultat", "sage_evolution_ca_annuelle",
        "sage_ratios_pilotage", "sage_top_fournisseurs", "sage_achats_par_mois",
        // Comptabilité
        "sage_balance_generale", "sage_grand_livre_compte", "sage_grand_livre_tiers", "sage_journal_comptable",
        // Trésorerie, créances, fiscal
        "sage_encours_clients", "sage_dettes_fournisseurs", "sage_balance_agee_clients",
        "sage_balance_agee_fournisseurs", "sage_retards_clients", "sage_conditions_reglement_tiers",
        "sage_tva", "sage_tresorerie", "sage_previsionnel_encaissements", "sage_echeancier_detaille",
        "sage_echeancier_emprunts", "sage_cadence_charges_fixes",
        // Référentiel
        "sage_lister_bases", "sage_lister_journaux", "sage_rechercher_comptes", "sage_rechercher_tiers",
    ];

    /// <summary>Outils qui lisent la gestion commerciale (F_DOCENTETE, F_DOCLIGNE, F_ARTICLE, F_ARTSTOCK, F_COLLABORATEUR).</summary>
    private static readonly string[] GescomTools =
    [
        "sage_top_clients", "sage_top_articles", "sage_ca_par_departement", "sage_ventes_par_mois",
        "sage_ca_par_collaborateur", "sage_clients_par_collaborateur", "sage_ca_par_famille",
        "sage_comparatif_ventes", "sage_analyse_abc_clients", "sage_nouveaux_clients", "sage_panier_moyen",
        "sage_commandes_clients_en_cours", "sage_commandes_clients_detail", "sage_devis_clients_en_cours",
        "sage_clients_inactifs", "sage_fiche_client", "sage_detail_facture",
        "sage_stock_inventaire", "sage_articles_en_rupture", "sage_articles_dormants",
    ];

    public static readonly IReadOnlyList<Plan> All =
    [
        new(Trial, "Essai gratuit", 0, AllowedTools: null, MaxMachines: 1),
        new(Compta, "Comptabilité", 49, ComptaTools, MaxMachines: 1),
        new(Gescom, "Gestion commerciale", 99, [.. ComptaTools, .. GescomTools], MaxMachines: 1),
        // Production : aucune restriction, donc les futurs outils de production y sont inclus d'office.
        new(Production, "Production", 149, AllowedTools: null, MaxMachines: 1),
    ];

    public static Plan? Find(string? code) =>
        All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
}
