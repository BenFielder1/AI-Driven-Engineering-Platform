/**
 * FR-204: applies the theme before first paint (no flash), inlined in <head> by the root layout with the request's CSP
 * nonce. It reads localStorage `aide.theme` (`light` | `dark` | `system`, default System), resolves System from
 * `prefers-color-scheme`, sets `.light` / `.dark` and `color-scheme` on <html>, and, while System is chosen, follows OS
 * changes live (it re-reads the stored choice on each change, so a choice made later in the app is respected).
 * Mirrors resolveTheme() / applyTheme() in src/lib/theme.ts: keep the two in step. Plain ES5, no dependencies.
 */
export const themeInitScript = `(function(){var k='aide.theme',r=document.documentElement,m=window.matchMedia?window.matchMedia('(prefers-color-scheme: dark)'):null;function p(){try{var s=window.localStorage.getItem(k);if(s==='light'||s==='dark'||s==='system')return s}catch(e){}return 'system'}function a(){var t=p(),d=t==='dark'||(t==='system'&&!!m&&m.matches);r.classList.remove(d?'light':'dark');r.classList.add(d?'dark':'light');r.style.colorScheme=d?'dark':'light'}a();if(m){var f=function(){if(p()==='system')a()};if(m.addEventListener)m.addEventListener('change',f);else if(m.addListener)m.addListener(f)}})()`
