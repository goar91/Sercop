(function initCrmTheme() {
  try {
    var storageKey = 'crm_theme_mode';
    var raw = String(window.localStorage.getItem(storageKey) || '').trim().toLowerCase();
    var mode = raw === 'light' || raw === 'dark' || raw === 'system' ? raw : 'system';

    var root = document.documentElement;
    if (mode === 'light' || mode === 'dark') {
      root.setAttribute('data-theme', mode);
    } else {
      root.removeAttribute('data-theme');
    }

    var isDark =
      mode === 'dark' ||
      (mode === 'system' &&
        typeof window.matchMedia === 'function' &&
        window.matchMedia('(prefers-color-scheme: dark)').matches);

    var meta = document.querySelector('meta[name=\"theme-color\"]');
    if (meta) {
      meta.setAttribute('content', isDark ? '#0b1220' : '#f6f7f9');
    }
  } catch (e) {
    // Ignore errors (e.g., private mode localStorage restrictions)
  }
})();

