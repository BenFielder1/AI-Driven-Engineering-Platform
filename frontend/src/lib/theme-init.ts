/**
 * FR-101 / SPEC §7.1: applies the theme before first paint (no flash). Inlined in <head> by the root layout with the
 * request's CSP nonce. Mirrors applyTheme() in src/lib/theme.ts (storage key `aide.theme`): keep the two in step.
 */
export const themeInitScript = `(function(){var p='system';try{var s=window.localStorage.getItem('aide.theme');if(s==='light'||s==='dark')p=s}catch(e){}var d=p==='dark'||(p==='system'&&!(window.matchMedia&&window.matchMedia('(prefers-color-scheme: light)').matches));var r=document.documentElement;r.classList.add(d?'dark':'light');r.style.colorScheme=d?'dark':'light'})()`
