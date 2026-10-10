import { useEffect } from 'react'

/** Unique page titles (WCAG 2.4.2). */
export function useDocumentTitle(title: string): void {
  useEffect(() => {
    document.title = `${title} · AIDE`
  }, [title])
}
