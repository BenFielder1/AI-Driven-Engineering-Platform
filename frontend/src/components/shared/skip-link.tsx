/** "Skip to content" (WCAG 2.4.1): the first focusable element of every shell, jumping to `<main id="main">`. */
export function SkipLink() {
  return (
    <a
      href="#main"
      className="sr-only z-50 rounded-lg bg-primary px-4 py-3 text-primary-foreground focus:not-sr-only focus:fixed focus:top-2 focus:left-2"
    >
      Skip to content
    </a>
  )
}
