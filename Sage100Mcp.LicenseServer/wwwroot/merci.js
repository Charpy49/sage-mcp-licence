// Page de retour de Stripe Checkout : récupère la clé de licence créée pour la session.
// Le webhook Stripe et cette page arrivent souvent en même temps ; le serveur provisionne au premier
// appel, quel qu'il soit. Tant que Stripe n'a pas finalisé la session, on réessaie quelques fois.
'use strict';

const sessionId = new URLSearchParams(window.location.search).get('session_id');
const states = ['loading', 'ready', 'expired', 'error'];
const MAX_ATTEMPTS = 8;
const dateFmt = new Intl.DateTimeFormat('fr-FR', { day: 'numeric', month: 'long', year: 'numeric' });

function show(state) {
  for (const s of states) document.getElementById(`state-${s}`).hidden = s !== state;
}

function renderReady(result, installerUrl) {
  document.getElementById('ready-plan').textContent = result.planName || '';
  document.getElementById('license-key').textContent = result.licenseKey;

  const until = result.expiresAtUtc ? dateFmt.format(new Date(result.expiresAtUtc)) : null;
  const who = result.clientName ? `${result.clientName} — ` : '';
  document.getElementById('ready-title').textContent = result.isTrial
    ? 'Votre essai gratuit commence maintenant'
    : 'Merci, votre abonnement est actif';
  document.getElementById('ready-summary').textContent = result.isTrial
    ? `${who}toutes les options sont ouvertes jusqu'au ${until}.`
    : `${who}votre licence est valable jusqu'au ${until}, puis renouvelée automatiquement chaque mois.`;

  if (installerUrl) {
    const link = document.getElementById('installer-link');
    link.href = installerUrl;
    link.hidden = false;
  }
  show('ready');
}

async function load(attempt = 1) {
  show('loading');
  if (!sessionId) {
    show('error');
    return;
  }

  try {
    const response = await fetch(`/api/shop/session/${encodeURIComponent(sessionId)}`, { cache: 'no-store' });
    if (response.status === 404) {
      document.getElementById('error-detail').textContent =
        "Ce lien de confirmation n'est pas valide. Si vous venez de payer, vérifiez l'adresse de la page.";
      show('error');
      return;
    }
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || 'Erreur serveur');

    const { result, installerUrl } = data;
    if (result.status === 'ready') return renderReady(result, installerUrl);
    if (result.status === 'expired') return show('expired');
    // "pending" : paiement en cours de finalisation chez Stripe.
    if (attempt < MAX_ATTEMPTS) {
      setTimeout(() => load(attempt + 1), 2500);
      return;
    }
    show('error');
  } catch {
    if (attempt < MAX_ATTEMPTS) {
      setTimeout(() => load(attempt + 1), 3000);
      return;
    }
    show('error');
  }
}

document.getElementById('retry').addEventListener('click', () => load());

document.getElementById('copy-key').addEventListener('click', async (event) => {
  const button = event.currentTarget;
  try {
    await navigator.clipboard.writeText(document.getElementById('license-key').textContent);
    button.textContent = 'Copiée ✓';
  } catch {
    // Presse-papiers refusé (contexte non sécurisé, permission) : sélection manuelle.
    const range = document.createRange();
    range.selectNodeContents(document.getElementById('license-key'));
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    button.textContent = 'Ctrl+C pour copier';
  }
  setTimeout(() => { button.textContent = 'Copier'; }, 2500);
});

load();
