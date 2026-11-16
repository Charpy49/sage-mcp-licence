// Site commercial : souscription (Stripe Checkout) et accès au portail client.
// Aucune donnée de paiement ne transite ici : le serveur crée une session Stripe et le navigateur
// est redirigé vers la page de paiement hébergée par Stripe.
'use strict';

const yearEl = document.getElementById('year');
if (yearEl) yearEl.textContent = String(new Date().getFullYear());

// --- Animations -----------------------------------------------------------------------------
// Les styles inline étant interdits par la CSP, tout passe par des classes et des variables CSS
// posées via CSSOM (element.style.setProperty), que la CSP autorise.

const calm = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// Les blocs .rv ne sont masqués (en attente d'apparition) que si ce script tourne.
document.documentElement.classList.add('js');

// Défilement : menu opaque, barre de progression, filigrane qui monte, grossit et se dévoile.
(() => {
  const nav = document.getElementById('nav');
  const progress = document.getElementById('progress');
  const watermark = document.querySelector('.watermark-stack');
  let ticking = false;

  function onScroll() {
    const y = window.scrollY;
    const max = Math.max(1, document.documentElement.scrollHeight - window.innerHeight);
    const p = Math.min(1, Math.max(0, y / max));
    if (nav) nav.classList.toggle('solid', y > 24);
    if (progress) progress.style.setProperty('--sp', p.toFixed(4));
    if (watermark) {
      watermark.style.setProperty('--p', p.toFixed(4));
      watermark.style.setProperty('--wm-y', (-p * 90).toFixed(1));
      watermark.style.setProperty('--wm-s', (1 + p * 0.16).toFixed(4));
    }
    ticking = false;
  }
  const request = () => {
    if (!ticking) {
      ticking = true;
      requestAnimationFrame(onScroll);
    }
  };
  window.addEventListener('scroll', request, { passive: true });
  window.addEventListener('resize', request);
  onScroll();
})();

// Apparition des blocs à leur entrée dans l'écran, légèrement décalée pour les blocs voisins.
(() => {
  const blocks = document.querySelectorAll('.rv');
  if (calm || !('IntersectionObserver' in window)) {
    blocks.forEach((el) => el.classList.add('in'));
    return;
  }
  const io = new IntersectionObserver((entries) => {
    entries.forEach((entry) => {
      if (entry.isIntersecting) {
        entry.target.classList.add('in');
        io.unobserve(entry.target);
      }
    });
  }, { rootMargin: '0px 0px -8% 0px', threshold: 0.06 });
  blocks.forEach((el) => {
    // Rang parmi ses frères .rv : les cartes d'une même grille arrivent l'une après l'autre.
    const siblings = el.parentElement ? [...el.parentElement.children].filter((c) => c.classList.contains('rv')) : [el];
    el.style.setProperty('transition-delay', `${Math.min(siblings.indexOf(el), 4) * 80}ms`);
    io.observe(el);
  });
  // Le délai ne doit pas retarder les effets de survol une fois le bloc apparu.
  document.addEventListener('transitionend', (e) => {
    if (e.target.classList && e.target.classList.contains('in')) e.target.style.removeProperty('transition-delay');
  });
})();

// Menu mobile.
(() => {
  const burger = document.getElementById('burger');
  const menu = document.getElementById('mobile-menu');
  if (!burger || !menu) return;
  const setOpen = (open) => {
    document.body.classList.toggle('menu-open', open);
    burger.setAttribute('aria-expanded', open ? 'true' : 'false');
  };
  burger.addEventListener('click', () => setOpen(!document.body.classList.contains('menu-open')));
  menu.querySelectorAll('a').forEach((a) => a.addEventListener('click', () => setOpen(false)));
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') setOpen(false); });
})();

// Démonstration de conversation : la question se tape, l'outil tourne, la réponse s'affiche
// ligne à ligne, puis on recommence. En pause quand la démo n'est pas à l'écran.
(() => {
  const chat = document.getElementById('chat');
  if (!chat || calm) return;
  const typed = chat.querySelector('.typed');
  const untyped = chat.querySelector('.untyped');
  const question = typed.textContent;
  const steps = [...chat.querySelectorAll('.step')];
  let visible = true;

  if ('IntersectionObserver' in window) {
    new IntersectionObserver((entries) => { visible = entries[0].isIntersecting; }, { threshold: 0.1 }).observe(chat);
  }

  async function play() {
    chat.classList.add('playing');
    for (;;) {
      while (!visible) await sleep(400);

      steps.forEach((s) => s.classList.remove('on'));
      chat.classList.remove('answered', 'fading');
      typed.textContent = '';
      untyped.textContent = question;
      chat.classList.add('typing');
      steps[0].classList.add('on');
      await sleep(500);
      for (let i = 1; i <= question.length; i++) {
        typed.textContent = question.slice(0, i);
        untyped.textContent = question.slice(i);
        await sleep(28 + Math.random() * 40);
      }
      chat.classList.remove('typing');
      await sleep(350);

      steps[1].classList.add('on');            // appel de l'outil
      await sleep(1100);
      chat.classList.add('answered');
      for (const step of steps.slice(2)) {     // réponse, ligne par ligne
        step.classList.add('on');
        await sleep(step.tagName === 'TR' ? 260 : 420);
      }

      await sleep(6000);
      chat.classList.add('fading');
      await sleep(650);
    }
  }
  play();
})();

async function postJson(url, body) {
  let response;
  try {
    response = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
  } catch {
    throw new Error('Connexion impossible. Vérifiez votre accès à Internet et réessayez.');
  }
  if (response.status === 429) throw new Error('Trop de tentatives. Patientez une minute puis réessayez.');
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || 'Une erreur est survenue. Réessayez dans un instant.');
  return data;
}

function showError(el, message) {
  el.textContent = message;
  el.hidden = !message;
}

// Désactive le bouton pendant l'appel (le serveur de licences peut mettre une quinzaine de secondes
// à sortir de veille) et le libère en cas d'erreur ; en cas de succès, la page est quittée.
async function withBusy(button, label, action) {
  const original = button.textContent;
  button.disabled = true;
  button.textContent = label;
  try {
    await action();
  } finally {
    button.disabled = false;
    button.textContent = original;
  }
}

// --- Souscription ---------------------------------------------------------------------------

function wireCheckout(form, getEmail) {
  const button = form.querySelector('button[data-plan]');
  const error = form.querySelector('.form-error');
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    showError(error, '');
    const email = getEmail ? getEmail() : null;
    if (getEmail && (!email || !email.includes('@'))) {
      showError(error, 'Indiquez votre adresse e-mail professionnelle.');
      return;
    }
    withBusy(button, 'Redirection…', async () => {
      try {
        const { url } = await postJson('/api/shop/checkout', { plan: button.dataset.plan, email });
        window.location.assign(url);
        // Laisse le bouton désactivé pendant la navigation.
        await new Promise(() => {});
      } catch (e) {
        showError(error, e.message);
      }
    });
  });
}

const trialForm = document.getElementById('trial-form');
if (trialForm) {
  const emailInput = document.getElementById('trial-email');
  wireCheckout(trialForm, () => emailInput.value.trim());
}
document.querySelectorAll('.buy-form').forEach((form) => wireCheckout(form, null));

// Boutique pas encore configurée côté serveur : on le dit plutôt que de laisser échouer les boutons.
fetch('/api/shop/plans')
  .then((r) => (r.ok ? r.json() : null))
  .then((data) => {
    if (data && data.available === false) {
      document.getElementById('shop-closed').hidden = false;
      document.querySelectorAll('button[data-plan]').forEach((b) => { b.disabled = true; });
    }
  })
  .catch(() => {});

// --- Espace client --------------------------------------------------------------------------

const portalForm = document.getElementById('portal-form');
if (portalForm) {
  const keyInput = document.getElementById('portal-key');
  const error = document.getElementById('portal-error');
  const button = portalForm.querySelector('button');
  portalForm.addEventListener('submit', (event) => {
    event.preventDefault();
    showError(error, '');
    const licenseKey = keyInput.value.trim();
    if (!licenseKey) {
      showError(error, 'Saisissez votre clé de licence.');
      return;
    }
    withBusy(button, 'Ouverture…', async () => {
      try {
        const { url } = await postJson('/api/shop/portal', { licenseKey });
        window.location.assign(url);
        await new Promise(() => {});
      } catch (e) {
        showError(error, e.message);
      }
    });
  });
}
