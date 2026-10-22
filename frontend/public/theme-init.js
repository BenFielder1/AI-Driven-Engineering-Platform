// SPEC §7.1: apply the theme before first paint (no flash). A same-origin, render-blocking script, so the CSP's
// `script-src 'self'` allows it. Mirrors applyTheme() in src/lib/theme.ts (storage key `aide.theme`).
;(function () {
  var preference = 'system'
  try {
    var stored = window.localStorage.getItem('aide.theme')
    if (stored === 'light' || stored === 'dark') preference = stored
  } catch (e) {
    // Storage unavailable: follow the system setting.
  }
  var dark =
    preference === 'dark' ||
    (preference === 'system' && !(window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches))
  var root = document.documentElement
  root.classList.add(dark ? 'dark' : 'light')
  root.style.colorScheme = dark ? 'dark' : 'light'
})()
