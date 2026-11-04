import type { Metadata } from 'next'

import { marketingPages, TAGLINE, type MarketingPath } from './content/pages'

const OG_IMAGE = { url: '/og.png', width: 1200, height: 630, alt: `Tevrin: ${TAGLINE}` }

/**
 * FR-221: the `metadata` a marketing route's server `layout.tsx` exports (pages stay client components, D-V3-2).
 * Next merges metadata per top-level key, so Open Graph and Twitter are complete here; relative URLs resolve against the
 * root layout's host-derived `metadataBase`. The title is absolute (no template), e.g. `Product · Tevrin`.
 */
export function marketingMetadata(path: MarketingPath): Metadata {
  const page = marketingPages[path]
  return {
    title: { absolute: page.title },
    description: page.description,
    alternates: { canonical: path },
    openGraph: {
      type: 'website',
      siteName: 'Tevrin',
      url: path,
      title: page.title,
      description: page.description,
      images: [OG_IMAGE],
    },
    twitter: {
      card: 'summary_large_image',
      title: page.title,
      description: page.description,
      images: ['/og.png'],
    },
  }
}
