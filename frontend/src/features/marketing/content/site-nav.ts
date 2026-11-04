import { DOCS_URL } from '../../../lib/site'

/**
 * FR-210, FR-222: the marketing header and footer links. Internal links are app paths (client navigation); external
 * ones (docs site, GitHub) open in a new tab and are marked with ↗ and a "(opens in a new tab)" label.
 */
export const GITHUB_URL = 'https://github.com/BenFielder1/AI-Driven-Engineering-Platform'

/** A page on the docs site (FR-224), e.g. `docsUrl('guides/cli/')`. */
export function docsUrl(path = ''): string {
  return `${DOCS_URL}${path}`
}

export interface NavLink {
  label: string
  href: string
  /** Off-site (docs, GitHub): new tab. */
  external?: boolean
}

export const docsLink: NavLink = { label: 'Docs', href: docsUrl(), external: true }

/** Header, left to right (FR-210). The mobile sheet lists the same links. */
export const headerLinks: readonly NavLink[] = [
  { label: 'Product', href: '/product' },
  { label: 'How it works', href: '/how-it-works' },
  { label: 'Integrations', href: '/integrations' },
  { label: 'Self-host', href: '/self-host' },
  docsLink,
  { label: 'Changelog', href: '/changelog' },
]

export const githubLink: NavLink = { label: 'GitHub', href: GITHUB_URL, external: true }

export interface FooterColumn {
  title: string
  links: readonly NavLink[]
}

/** Footer columns (FR-210). */
export const footerColumns: readonly FooterColumn[] = [
  {
    title: 'Product',
    links: [
      { label: 'Features', href: '/product' },
      { label: 'How it works', href: '/how-it-works' },
      { label: 'Integrations', href: '/integrations' },
      { label: 'Changelog', href: '/changelog' },
    ],
  },
  {
    title: 'Developers',
    links: [
      docsLink,
      { label: 'CLI', href: docsUrl('guides/cli/'), external: true },
      { label: 'API reference', href: docsUrl('reference/api-and-contract/'), external: true },
      githubLink,
    ],
  },
  {
    title: 'Self-host',
    links: [
      { label: 'Install', href: docsUrl('getting-started/'), external: true },
      { label: 'Requirements', href: docsUrl('getting-started/prerequisites/'), external: true },
      { label: 'Upgrade', href: docsUrl('operations/postgres-18-upgrade/'), external: true },
    ],
  },
  {
    title: 'Trust',
    links: [
      { label: 'Security', href: '/security' },
      { label: 'FAQ', href: '/faq' },
    ],
  },
]
