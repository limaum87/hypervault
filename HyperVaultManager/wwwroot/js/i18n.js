// Lightweight i18n engine. Default English, with Portuguese available.
// Preference is stored in localStorage. Keys are looked up by dotted path,
// with the document re-rendered (data-i18n / data-i18n-ph / data-i18n-html)
// whenever the language changes.

(function () {
  const STORAGE_KEY = "hypervault.lang";
  const DEFAULT_LANG = "en";
  const SUPPORTED = ["en", "pt"];

  const cache = {}; // lang -> flat dictionary
  let current = localStorage.getItem(STORAGE_KEY) || DEFAULT_LANG;
  if (!SUPPORTED.includes(current)) current = DEFAULT_LANG;

  async function load(lang) {
    if (cache[lang]) return cache[lang];
    const res = await fetch(`locales/${lang}.json`, { cache: "no-cache" });
    if (!res.ok) throw new Error(`Failed to load locale ${lang}`);
    cache[lang] = await res.json();
    return cache[lang];
  }

  function lookup(lang, key) {
    const dict = cache[lang] || {};
    return Object.prototype.hasOwnProperty.call(dict, key) ? dict[key] : undefined;
  }

  function t(key) {
    return lookup(current, key) ?? lookup(DEFAULT_LANG, key) ?? key;
  }

  function applyTo(root) {
    root = root || document;
    root.querySelectorAll("[data-i18n]").forEach((el) => {
      el.textContent = t(el.getAttribute("data-i18n"));
    });
    root.querySelectorAll("[data-i18n-ph]").forEach((el) => {
      el.setAttribute("placeholder", t(el.getAttribute("data-i18n-ph")));
    });
    root.querySelectorAll("[data-i18n-html]").forEach((el) => {
      el.innerHTML = t(el.getAttribute("data-i18n-html"));
    });
    document.documentElement.setAttribute("lang", current);
    document.querySelectorAll("[data-lang-label]").forEach((el) => {
      el.textContent = t(el.getAttribute("data-lang-label"));
    });
  }

  async function setLang(lang) {
    if (!SUPPORTED.includes(lang)) return;
    await load(lang);
    current = lang;
    localStorage.setItem(STORAGE_KEY, lang);
    applyTo(document);
    document.dispatchEvent(new CustomEvent("langchange", { detail: { lang: current } }));
  }

  async function init() {
    await load(DEFAULT_LANG);
    if (current !== DEFAULT_LANG) await load(current);
    applyTo(document);
    document.dispatchEvent(new CustomEvent("langchange", { detail: { lang: current } }));
  }

  window.i18n = {
    init,
    setLang,
    t,
    apply: applyTo,
    get current() { return current; },
    supported: SUPPORTED,
    defaultLang: DEFAULT_LANG,
  };
})();
