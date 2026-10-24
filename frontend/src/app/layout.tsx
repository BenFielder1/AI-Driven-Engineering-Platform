import type { Metadata, Viewport } from 'next'
import { headers } from 'next/headers'
import type { ReactNode } from 'react'

// Self-hosted fonts (SPEC §7.1, FR-101): DM Sans body, Outfit 600 headings, Geist Mono numbers and code. Latin only.
import '@fontsource/dm-sans/latin-400.css'
import '@fontsource/dm-sans/latin-500.css'
import '@fontsource/dm-sans/latin-600.css'
import '@fontsource/dm-sans/latin-700.css'
import '@fontsource/outfit/latin-600.css'
import '@fontsource/geist-mono/latin-400.css'
import '@fontsource/geist-mono/latin-500.css'
import '../index.css'
import { themeInitScript } from '../lib/theme-init'
import { Providers } from './providers'

export const metadata: Metadata = {
  title: 'AIDE Platform',
  icons: { icon: { url: '/favicon.svg', type: 'image/svg+xml' } },
}

export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
  colorScheme: 'dark light',
}

/**
 * Root layout (SPEC §7): static markup only, no API data (D-V3-2). Reading the request headers makes every page
 * dynamic, which the per-request CSP nonce (FR-99, set by server.ts) needs. The theme-init script runs before first
 * paint (FR-101) and carries the nonce; it changes <html>'s class, hence suppressHydrationWarning.
 */
export default async function RootLayout({ children }: { children: ReactNode }) {
  const nonce = (await headers()).get('x-nonce') ?? undefined
  return (
    <html lang="en" suppressHydrationWarning>
      <head>
        <script nonce={nonce} dangerouslySetInnerHTML={{ __html: themeInitScript }} />
      </head>
      <body>
        <Providers>{children}</Providers>
      </body>
    </html>
  )
}
