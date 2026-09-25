'use strict';

// Interface d'administration des licences : page statique qui appelle /api/admin/* avec la clé
// d'administration saisie à la connexion. Aucune donnée n'est insérée en HTML brut (noms de postes
// et de clients viennent de l'extérieur) : tout passe par textContent via h().

// Catalogue des outils du serveur MCP, par fichier de Sage100Mcp/Tools. À tenir à jour quand un outil
// est ajouté ; un outil autorisé par une licence mais absent d'ici reste affiché (groupe « Autres »).
const TOOL_GROUPS = [
  ['Direction', ['sage_tableau_de_bord', 'sage_chiffre_affaires', 'sage_compte_resultat', 'sage_top_clients',
    'sage_top_articles', 'sage_ca_par_departement', 'sage_ventes_par_mois', 'sage_ca_par_collaborateur',
    'sage_clients_par_collaborateur']],
  ['Pilotage', ['sage_evolution_ca_annuelle', 'sage_ca_par_famille', 'sage_top_fournisseurs', 'sage_achats_par_mois',
    'sage_commandes_clients_en_cours', 'sage_devis_clients_en_cours', 'sage_clients_inactifs', 'sage_fiche_client',
    'sage_commandes_clients_detail']],
  ['Analyse', ['sage_analyse_abc_clients', 'sage_comparatif_ventes', 'sage_nouveaux_clients', 'sage_panier_moyen',
    'sage_ratios_pilotage']],
  ['Suivi', ['sage_balance_agee_fournisseurs', 'sage_retards_clients', 'sage_detail_facture', 'sage_journal_comptable',
    'sage_articles_dormants']],
  ['Comptabilité', ['sage_balance_generale', 'sage_grand_livre_compte', 'sage_grand_livre_tiers']],
  ['Trésorerie', ['sage_encours_clients', 'sage_dettes_fournisseurs', 'sage_balance_agee_clients',
    'sage_conditions_reglement_tiers']],
  ['Finance', ['sage_tva', 'sage_tresorerie', 'sage_previsionnel_encaissements', 'sage_echeancier_detaille',
    'sage_echeancier_emprunts', 'sage_cadence_charges_fixes']],
  ['Stock', ['sage_stock_inventaire', 'sage_articles_en_rupture']],
  ['Référentiel', ['sage_lister_bases', 'sage_lister_journaux', 'sage_rechercher_comptes', 'sage_rechercher_tiers']],
];

const KEY_STORAGE = 'sage-licences-admin-key';
const DAY = 86400000;

const state = { key: null, licenses: [], releases: [], currentId: null };

// --- Utilitaires -----------------------------------------------------------------------------

const $ = (id) => document.getElementById(id);

function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [name, value] of Object.entries(attrs || {})) {
    if (value === null || value === undefined || value === false) continue;
    if (name === 'class') el.className = value;
    else if (name.startsWith('on')) el.addEventListener(name.slice(2), value);
    else el.setAttribute(name, value === true ? '' : value);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

const dateFmt = new Intl.DateTimeFormat('fr-FR', { day: '2-digit', month: '2-digit', year: 'numeric' });
const dateTimeFmt = new Intl.DateTimeFormat('fr-FR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' });
const fmtDate = (iso) => (iso ? dateFmt.format(new Date(iso)) : '—');
const fmtDateTime = (iso) => (iso ? dateTimeFmt.format(new Date(iso)) : '—');
const toDateInput = (iso) => new Date(iso).toISOString().slice(0, 10);
const fromDateInput = (value) => `${value}T23:59:59Z`;

function relative(iso) {
  if (!iso) return 'jamais';
  const days = Math.floor((Date.now() - new Date(iso).getTime()) / DAY);
  if (days <= 0) return "aujourd'hui";
  if (days === 1) return 'hier';
  return `il y a ${days} j`;
}

function toast(message, isError = false) {
  const el = h('div', { class: 'toast' + (isError ? ' bad' : '') }, message);
  $('toasts').append(el);
  setTimeout(() => el.remove(), isError ? 7000 : 3500);
}

function compareVersions(a, b) {
  const pa = a.split('.').map(Number), pb = b.split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d) return d;
  }
  return 0;
}

// --- API -------------------------------------------------------------------------------------

class ApiError extends Error {}

async function api(method, path, body) {
  const response = await fetch(path, {
    method,
    headers: { 'X-Admin-Key': state.key, ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (response.status === 401) {
    logout();
    throw new ApiError("Clé d'administration refusée.");
  }
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!response.ok) {
    const detail = typeof data === 'string' ? data : data?.title || data?.detail || '';
    throw new ApiError(`Erreur ${response.status}${detail ? ' : ' + detail : ''}`);
  }
  return data;
}

// Exécute une action, signale le résultat et recharge les données.
async function run(action, successMessage) {
  try {
    await action();
    if (successMessage) toast(successMessage);
    await loadAll();
  } catch (err) {
    toast(err.message || String(err), true);
  }
}

async function loadAll() {
  const [licenses, releases] = await Promise.all([api('GET', '/api/admin/licenses'), api('GET', '/api/admin/releases')]);
  state.licenses = licenses;
  state.releases = releases.sort((a, b) => compareVersions(b.version, a.version));
  renderLicenses();
  renderReleases();
  if (state.currentId && $('license-dialog').open) renderLicenseDialog();
}

// --- Connexion -------------------------------------------------------------------------------

function showApp(visible) {
  $('login').hidden = visible;
  $('app').hidden = !visible;
}

function logout() {
  state.key = null;
  try { sessionStorage.removeItem(KEY_STORAGE); } catch { /* stockage indisponible */ }
  document.querySelectorAll('dialog[open]').forEach((d) => d.close());
  showApp(false);
  $('login-key').value = '';
  $('login-key').focus();
}

$('login-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  state.key = $('login-key').value.trim();
  $('login-error').hidden = true;
  try {
    await loadAll();
    try { sessionStorage.setItem(KEY_STORAGE, state.key); } catch { /* stockage indisponible */ }
    showApp(true);
  } catch (err) {
    $('login-error').textContent = err.message;
    $('login-error').hidden = false;
    showApp(false);
  }
});

$('logout').addEventListener('click', logout);
$('refresh').addEventListener('click', () => run(() => Promise.resolve(), 'Données actualisées.'));

// --- Onglets ---------------------------------------------------------------------------------

document.querySelectorAll('.tab').forEach((tab) => tab.addEventListener('click', () => {
  document.querySelectorAll('.tab').forEach((t) => t.setAttribute('aria-selected', String(t === tab)));
  $('tab-licenses').hidden = tab.dataset.tab !== 'licenses';
  $('tab-releases').hidden = tab.dataset.tab !== 'releases';
}));

document.querySelectorAll('[data-close]').forEach((button) =>
  button.addEventListener('click', () => button.closest('dialog').close()));

// --- Licences --------------------------------------------------------------------------------

function licenseStatus(license) {
  if (license.isRevoked) return { key: 'revoked', label: 'Révoquée', cls: 'bad' };
  const days = Math.floor((new Date(license.expiresAtUtc).getTime() - Date.now()) / DAY);
  if (days < 0) return { key: 'expired', label: 'Expirée', cls: 'bad' };
  if (days <= 30) return { key: 'expiring', label: `Expire dans ${days} j`, cls: 'warn' };
  return { key: 'active', label: 'Active', cls: 'ok' };
}

const activeMachines = (license) => license.machines.filter((m) => m.isActive).length;

function renderStats() {
  const live = state.licenses.filter((l) => ['active', 'expiring'].includes(licenseStatus(l).key));
  const used = live.reduce((n, l) => n + activeMachines(l), 0);
  const seats = live.reduce((n, l) => n + l.maxMachines, 0);
  const expiring = state.licenses.filter((l) => licenseStatus(l).key === 'expiring').length;
  const latest = state.releases.find((r) => r.channel === 'stable' && !r.isYanked);
  const stat = (value, label) => h('div', { class: 'stat' }, h('div', { class: 'value' }, value), h('div', { class: 'label' }, label));
  $('stats').replaceChildren(
    stat(String(live.length), `licence(s) en cours sur ${state.licenses.length}`),
    stat(`${used} / ${seats}`, 'postes activés / autorisés'),
    stat(String(expiring), 'expirent sous 30 jours'),
    stat(latest ? latest.version : '—', 'dernière version stable'),
  );
}

function renderLicenses() {
  renderStats();
  const query = $('search').value.trim().toLowerCase();
  const filter = $('filter').value;
  const rows = state.licenses
    .filter((l) => !filter || licenseStatus(l).key === filter || (filter === 'active' && licenseStatus(l).key === 'expiring'))
    .filter((l) => !query || [l.clientName, l.keyPrefix, ...l.machines.map((m) => m.machineName || '')]
      .some((text) => text.toLowerCase().includes(query)))
    .sort((a, b) => a.clientName.localeCompare(b.clientName, 'fr'));

  $('licenses-body').replaceChildren(...rows.map((l) => {
    const status = licenseStatus(l);
    const used = activeMachines(l);
    const refused = l.machines.length - used;
    return h('tr', { class: 'clickable', tabindex: '0', onclick: () => openLicense(l.id),
      onkeydown: (e) => { if (e.key === 'Enter') openLicense(l.id); } },
      h('td', {}, h('span', { class: 'client-name' }, l.clientName),
        h('span', { class: 'sub' }, l.machines.map((m) => m.machineName || '?').join(', ') || 'aucun poste')),
      h('td', {}, h('code', {}, l.keyPrefix + '…')),
      h('td', {}, fmtDate(l.expiresAtUtc)),
      h('td', {}, h('span', { class: 'badge ' + status.cls }, status.label)),
      h('td', { class: 'num' }, h('span', { class: refused > 0 ? 'over' : '' }, `${used} / ${l.maxMachines}`),
        refused > 0 ? h('span', { class: 'sub' }, `${refused} refusé(s)`) : null),
      h('td', {}, relative(l.lastValidatedAtUtc), h('span', { class: 'sub' }, fmtDateTime(l.lastValidatedAtUtc))),
      h('td', {}, l.lastInstalledVersion || '—', l.lastTransport ? h('span', { class: 'sub' }, l.lastTransport) : null),
      h('td', {}, l.updateChannel, l.pinnedVersion ? h('span', { class: 'sub' }, `épinglé ${l.pinnedVersion}`) : null),
    );
  }));
  $('licenses-empty').hidden = rows.length > 0;
}

$('search').addEventListener('input', renderLicenses);
$('filter').addEventListener('change', renderLicenses);

// --- Détail d'une licence --------------------------------------------------------------------

const currentLicense = () => state.licenses.find((l) => l.id === state.currentId);

function openLicense(id) {
  state.currentId = id;
  renderLicenseDialog();
  const dialog = $('license-dialog');
  if (!dialog.open) dialog.showModal();
}

function renderLicenseDialog() {
  const l = currentLicense();
  if (!l) { $('license-dialog').close(); return; }
  const status = licenseStatus(l);

  $('ld-title').replaceChildren(l.clientName, ' ', h('span', { class: 'badge ' + status.cls }, status.label));
  $('ld-subtitle').textContent =
    `Clé ${l.keyPrefix}… · créée le ${fmtDate(l.createdAtUtc)} · dernière validation ${relative(l.lastValidatedAtUtc)}` +
    (l.lastValidatedIp ? ` depuis ${l.lastValidatedIp}` : '');

  const general = $('ld-general').elements;
  general.clientName.value = l.clientName;
  general.expires.value = toDateInput(l.expiresAtUtc);
  general.maxMachines.value = l.maxMachines;

  // Postes
  $('ld-machines').replaceChildren(...l.machines.map((m) => h('tr', {},
    h('td', {}, m.machineName || '(sans nom)'),
    h('td', {}, h('code', { title: m.machineId }, m.machineId.slice(0, 12) + '…')),
    h('td', {}, fmtDateTime(m.activatedAtUtc)),
    h('td', {}, fmtDateTime(m.lastSeenAtUtc)),
    h('td', {}, h('span', { class: 'badge ' + (m.isActive ? 'ok' : 'bad') }, m.isActive ? 'Autorisé' : 'Hors quota')),
    h('td', {}, h('div', { class: 'row-actions' }, h('button', { class: 'btn ghost small', type: 'button',
      onclick: () => releaseMachine(l, m) }, 'Libérer'))),
  )));
  $('ld-machines-empty').hidden = l.machines.length > 0;
  $('ld-release-all').disabled = l.machines.length === 0;

  renderTools(l);

  // Politique de mise à jour
  const policy = $('ld-policy').elements;
  policy.updateChannel.value = l.updateChannel;
  const versions = state.releases.filter((r) => !r.isYanked || r.version === l.pinnedVersion);
  policy.pinnedVersion.replaceChildren(
    h('option', { value: '' }, 'Aucune (suit le dernier du canal)'),
    ...versions.map((r) => h('option', { value: r.version }, `${r.version} — ${r.channel}${r.isYanked ? ' (retirée)' : ''}`)),
  );
  policy.pinnedVersion.value = l.pinnedVersion || '';

  // Révocation
  $('ld-revoke-label').textContent = l.isRevoked ? 'Réactiver la licence' : 'Révoquer la licence';
  $('ld-revoke-help').textContent = l.isRevoked
    ? 'La licence redevient utilisable au prochain démarrage des postes.'
    : "Refusée au prochain démarrage de chaque poste (le mode hors connexion reste possible jusqu'à 7 jours).";
  $('ld-revoke').textContent = l.isRevoked ? 'Réactiver' : 'Révoquer';
}

function renderTools(l) {
  const allowed = new Set(l.allowedTools || []);
  const known = new Set(TOOL_GROUPS.flatMap(([, tools]) => tools));
  const groups = [...TOOL_GROUPS];
  const unknown = [...allowed].filter((t) => !known.has(t));
  if (unknown.length) groups.push(['Autres', unknown]);

  const form = $('ld-tools');
  form.elements.toolsMode.value = l.allowedTools ? 'some' : 'all';

  $('ld-tools-list').replaceChildren(...groups.map(([name, tools]) => {
    const boxes = tools.map((tool) => h('input', { type: 'checkbox', name: 'tool', value: tool, checked: allowed.has(tool) }));
    return h('div', { class: 'tool-group' },
      h('div', { class: 'tool-group-head' }, name,
        h('button', { class: 'link', type: 'button', onclick: () => {
          const all = boxes.every((b) => b.checked);
          boxes.forEach((b) => { b.checked = !all; });
        } }, 'tout / rien')),
      ...tools.map((tool, i) => h('label', { class: 'check' }, boxes[i], h('code', {}, tool))));
  }));
  syncToolsMode();
}

function syncToolsMode() {
  $('ld-tools-list').classList.toggle('disabled', $('ld-tools').elements.toolsMode.value === 'all');
}
$('ld-tools').addEventListener('change', (e) => { if (e.target.name === 'toolsMode') syncToolsMode(); });

$('ld-general').addEventListener('submit', (event) => {
  event.preventDefault();
  const l = currentLicense();
  const f = event.target.elements;
  const max = Number(f.maxMachines.value);
  if (max < activeMachines(l) && !confirm(
    `${activeMachines(l)} poste(s) sont activés : avec ${max} poste(s) autorisé(s), les derniers activés seront refusés. Continuer ?`)) return;
  run(() => api('PUT', `/api/admin/licenses/${l.id}`, {
    clientName: f.clientName.value.trim(), expiresAtUtc: fromDateInput(f.expires.value), maxMachines: max,
  }), 'Licence enregistrée.');
});

$('ld-tools').addEventListener('submit', (event) => {
  event.preventDefault();
  const l = currentLicense();
  const form = event.target;
  if (form.elements.toolsMode.value === 'all') {
    run(() => api('PUT', `/api/admin/licenses/${l.id}`, { clearAllowedTools: true }), 'Tous les outils sont autorisés.');
    return;
  }
  const tools = [...form.querySelectorAll('input[name="tool"]:checked')].map((b) => b.value);
  if (tools.length === 0 && !confirm("Aucun outil coché : le serveur MCP n'exposera plus rien. Continuer ?")) return;
  run(() => api('PUT', `/api/admin/licenses/${l.id}`, { allowedTools: tools }), `${tools.length} outil(s) autorisé(s).`);
});

$('ld-policy').addEventListener('submit', (event) => {
  event.preventDefault();
  const l = currentLicense();
  const f = event.target.elements;
  const pinned = f.pinnedVersion.value;
  const body = { updateChannel: f.updateChannel.value.trim() };
  if (pinned) body.pinnedVersion = pinned;
  else if (l.pinnedVersion) body.clearPinnedVersion = true;
  run(() => api('PUT', `/api/admin/licenses/${l.id}/update-policy`, body), 'Politique de mise à jour enregistrée.');
});

function releaseMachine(l, m) {
  if (!confirm(`Libérer le poste « ${m.machineName || m.machineId.slice(0, 12)} » ? Sa place revient au prochain poste qui se présente.`)) return;
  run(() => api('DELETE', `/api/admin/licenses/${l.id}/machines/${encodeURIComponent(m.machineId)}`), 'Poste libéré.');
}

$('ld-release-all').addEventListener('click', () => {
  const l = currentLicense();
  if (!confirm(`Libérer les ${l.machines.length} poste(s) de ${l.clientName} ?`)) return;
  run(() => api('DELETE', `/api/admin/licenses/${l.id}/machines`), 'Tous les postes ont été libérés.');
});

$('ld-revoke').addEventListener('click', () => {
  const l = currentLicense();
  if (!l.isRevoked && !confirm(`Révoquer la licence de ${l.clientName} ?`)) return;
  run(() => api('POST', `/api/admin/licenses/${l.id}/${l.isRevoked ? 'unrevoke' : 'revoke'}`),
    l.isRevoked ? 'Licence réactivée.' : 'Licence révoquée.');
});

$('ld-delete').addEventListener('click', () => {
  const l = currentLicense();
  const typed = prompt(`Suppression définitive. Tapez le nom du client pour confirmer :\n${l.clientName}`);
  if (typed === null) return;
  if (typed.trim() !== l.clientName) { toast('Nom saisi différent : suppression annulée.', true); return; }
  run(async () => {
    await api('DELETE', `/api/admin/licenses/${l.id}`);
    $('license-dialog').close();
  }, 'Licence supprimée.');
});

// --- Création d'une licence ------------------------------------------------------------------

$('new-license').addEventListener('click', () => {
  const form = $('create-form');
  form.reset();
  const nextYear = new Date(Date.now() + 365 * DAY);
  form.elements.expires.value = nextYear.toISOString().slice(0, 10);
  $('create-dialog').showModal();
});

$('create-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  const f = event.target.elements;
  try {
    const created = await api('POST', '/api/admin/licenses', {
      clientName: f.clientName.value.trim(),
      expiresAtUtc: fromDateInput(f.expires.value),
      maxMachines: Number(f.maxMachines.value),
      allowedTools: null,
    });
    $('create-dialog').close();
    $('key-value').textContent = created.licenseKey;
    $('key-dialog').showModal();
    await loadAll();
  } catch (err) {
    toast(err.message, true);
  }
});

$('key-copy').addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText($('key-value').textContent);
    toast('Clé copiée dans le presse-papiers.');
  } catch {
    toast('Copie impossible : sélectionnez la clé et copiez-la à la main.', true);
  }
});

// Ferme la fenêtre de la clé : on l'efface du DOM pour qu'elle ne traîne pas.
$('key-dialog').addEventListener('close', () => { $('key-value').textContent = ''; });

// --- Versions --------------------------------------------------------------------------------

function renderReleases() {
  $('releases-body').replaceChildren(...state.releases.map((r) => {
    const badges = [];
    if (r.isYanked) badges.push(h('span', { class: 'badge bad' }, 'Retirée'));
    if (r.isMinimum) badges.push(h('span', { class: 'badge warn' }, 'Plancher'));
    if (!badges.length) badges.push(h('span', { class: 'badge ok' }, 'Servie'));
    const users = state.licenses.filter((l) => l.lastInstalledVersion === r.version).length;
    return h('tr', {},
      h('td', {}, h('strong', {}, r.version), h('span', { class: 'sub' }, `${users} licence(s) sur cette version`)),
      h('td', {}, h('span', { class: 'badge info' }, r.channel)),
      h('td', {}, fmtDateTime(r.publishedAtUtc)),
      h('td', {}, ...badges.flatMap((b, i) => (i ? [' ', b] : [b]))),
      h('td', {}, h('code', { title: r.sha256 }, r.sha256.slice(0, 12) + '…'),
        h('span', { class: 'sub' }, h('a', { href: r.downloadUrl, rel: 'noopener noreferrer', target: '_blank' }, 'paquet'))),
      h('td', {}, r.notes || ''),
      h('td', {}, h('div', { class: 'row-actions' },
        h('button', { class: 'btn ghost small', type: 'button', onclick: () => toggleMinimum(r) },
          r.isMinimum ? 'Lever le plancher' : 'Définir plancher'),
        h('button', { class: 'btn ghost small', type: 'button', onclick: () => toggleYank(r) },
          r.isYanked ? 'Rétablir' : 'Retirer'),
        h('button', { class: 'btn danger small', type: 'button', onclick: () => deleteRelease(r) }, 'Supprimer'))),
    );
  }));
  $('releases-empty').hidden = state.releases.length > 0;
}

function toggleMinimum(r) {
  if (!r.isMinimum && !confirm(
    `Déclarer ${r.version} version plancher ? Tous les postes en dessous seront obligés de se mettre à jour au prochain lancement.`)) return;
  run(() => api(r.isMinimum ? 'DELETE' : 'POST', `/api/admin/releases/${encodeURIComponent(r.version)}/minimum`),
    r.isMinimum ? 'Plancher levé.' : `${r.version} est désormais la version plancher.`);
}

function toggleYank(r) {
  if (!r.isYanked && !confirm(`Retirer ${r.version} ? Elle ne sera plus servie, même aux clients épinglés dessus.`)) return;
  run(() => api('POST', `/api/admin/releases/${encodeURIComponent(r.version)}/${r.isYanked ? 'unyank' : 'yank'}`),
    r.isYanked ? 'Version rétablie.' : 'Version retirée.');
}

function deleteRelease(r) {
  if (!confirm(`Supprimer définitivement l'enregistrement de la version ${r.version} ? Préférez « Retirer » pour un simple coupe-circuit.`)) return;
  run(() => api('DELETE', `/api/admin/releases/${encodeURIComponent(r.version)}`), 'Version supprimée.');
}

$('new-release').addEventListener('click', () => {
  $('release-form').reset();
  $('release-dialog').showModal();
});

$('release-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  const f = event.target.elements;
  try {
    await api('POST', '/api/admin/releases', {
      version: f.version.value.trim(),
      channel: f.channel.value.trim(),
      downloadUrl: f.downloadUrl.value.trim(),
      sha256: f.sha256.value.trim().toUpperCase(),
      notes: f.notes.value.trim() || null,
      isMinimum: f.isMinimum.checked,
    });
    $('release-dialog').close();
    toast(`Version ${f.version.value.trim()} enregistrée.`);
    await loadAll();
  } catch (err) {
    toast(err.message, true);
  }
});

// --- Démarrage -------------------------------------------------------------------------------

(async function start() {
  let saved = null;
  try { saved = sessionStorage.getItem(KEY_STORAGE); } catch { /* stockage indisponible */ }
  if (!saved) { showApp(false); return; }
  state.key = saved;
  try {
    await loadAll();
    showApp(true);
  } catch {
    logout();
  }
})();
