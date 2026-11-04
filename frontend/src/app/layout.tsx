import type { Metadata, Viewport } from 'next'
import { headers } from 'next/headers'
import type { ReactNode } from 'react'

// FR-203: self-hosted fonts (CSP stays font-src 'self'): Geist Sans for UI and headings, Geist Mono for IDs, hashes,
// versions, code and eyebrow labels. Latin only.
import '@fontsource/geist-sans/latin-400.css'
import '@fontsource/geist-sans/latin-500.css'
import '@fontsource/geist-sans/latin-600.css'
import '@fontsource/geist-sans/latin-700.css'
import '@fontsource/geist-mono/latin-400.css'
import '@fontsource/geist-mono/latin-500.css'
import '../index.css'
import { TAGLINE } from '../components/brand-geometry'
import { themeInitScript } from '../lib/theme-init'
import { Providers } from './providers'

/**
 * FR-196: favicon (SVG, with a 32 px PNG fallback), the Apple touch icon and the Open Graph image, all from public/.
 * Open Graph needs absolute URLs: they are built from the request's own Host (checked to be a plain host name), since
 * the public origin is not a build-time constant. The title is renamed with the rest of the app copy (FR-197).
 */
export async function generateMetadata(): Promise<Metadata> {
  const requestHeaders = await headers()
  const host = requestHeaders.get('host') ?? ''
  const proto = requestHeaders.get('x-forwarded-proto') === 'https' ? 'https' : 'http'
  const metadataBase = /^[a-z0-9.-]+(:\d{1,5})?$/i.test(host) ? new URL(`${proto}://${host}`) : undefined
  return {
    title: 'Tevrin',
    metadataBase,
    icons: {
      icon: [
        { url: '/favicon.svg', type: 'image/svg+xml' },
        { url: '/icon-32.png', type: 'image/png', sizes: '32x32' },
      ],
      apple: { url: '/apple-touch-icon.png', sizes: '180x180', type: 'image/png' },
    },
    openGraph: {
      siteName: 'Tevrin',
      description: TAGLINE,
      images: [{ url: '/og.png', width: 1200, height: 630, alt: `Tevrin: ${TAGLINE}` }],
    },
    twitter: { card: 'summary_large_image', images: ['/og.png'] },
  }
}

export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
  colorScheme: 'dark light',
}

/**
 * Root layout (SPEC §7): static markup only, no API data (D-V3-2). Reading the request headers makes every page
 * dynamic, which the per-request CSP nonce (FR-99, set by server.ts) needs. The theme-init script runs before first
 * paint (FR-204) and carries the nonce; it changes <html>'s class, hence suppressHydrationWarning.
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
