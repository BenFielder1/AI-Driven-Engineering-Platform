import type { PlatformConnection, PlatformProvider } from './platform-connections-api'

/**
 * What each platform needs (FR-142, SPEC §4.20 "Token (pasted) and validation", §4.21 Supabase required permissions):
 * shown in the add dialog so the user creates exactly the right token. Copy only; the backend does the checking.
 */
export interface PlatformProviderInfo {
  id: PlatformProvider
  name: string
  /** Where on the provider the token is created (opened in a new tab). */
  tokenPageUrl: string
  /** The hostname of the token page, for the link text. */
  tokenPageSite: string
  /** What the token is called on the provider ("API key" on Render). */
  tokenNoun: string
  /** The token type to create. */
  tokenType: string
  /** Scopes or permissions to grant; empty when the provider has none to pick (`scopeNote` says why). */
  permissions: readonly string[]
  /** Said about the scope next to (or instead of) the permission list. */
  scopeNote: string
  expiry: string
  /** Extra notes (e.g. a Git app to install before deploying). */
  notes?: readonly string[]
  /** Railway: a workspace token can't name its workspace, so the dialog asks for its id (`accountId`). */
  accountIdField?: { label: string; hint: string }
  /** The word for what a token reaches (team, account, workspace, organisation). */
  accountNoun: string
}

export const platformProviders: Readonly<Record<PlatformProvider, PlatformProviderInfo>> = {
  vercel: {
    id: 'vercel',
    name: 'Vercel',
    tokenPageUrl: 'https://vercel.com/account/settings/tokens',
    tokenPageSite: 'vercel.com',
    tokenNoun: 'access token',
    tokenType: 'an access token scoped to the team that owns the projects (or to your personal account).',
    permissions: [],
    scopeNote: 'Vercel tokens have no finer permissions: the token reaches everything in the team you scope it to.',
    expiry: 'pick an expiry (for example 90 days or a year). AIDE shows it here, and an expired token is marked invalid.',
    accountNoun: 'Team',
  },
  netlify: {
    id: 'netlify',
    name: 'Netlify',
    tokenPageUrl: 'https://app.netlify.com/user/applications#personal-access-tokens',
    tokenPageSite: 'app.netlify.com',
    tokenNoun: 'personal access token',
    tokenType: 'a personal access token (User settings → Applications → Personal access tokens).',
    permissions: [],
    scopeNote: 'Netlify tokens have no scopes: the token acts as you, in every team you belong to.',
    expiry: 'Netlify asks for an expiry: choose one that suits you, and add a new token here before it expires.',
    notes: ['For a GitHub repository, install the Netlify GitHub App on it before AIDE creates the site.'],
    accountNoun: 'Team',
  },
  cloudflare: {
    id: 'cloudflare',
    name: 'Cloudflare',
    tokenPageUrl: 'https://dash.cloudflare.com/profile/api-tokens',
    tokenPageSite: 'dash.cloudflare.com',
    tokenNoun: 'API token',
    tokenType:
      'a user API token (My Profile → API Tokens → Create Token). Account-owned tokens (starting cfat_) don\'t work: Workers Builds needs a user token.',
    permissions: ['Workers Scripts: Edit', 'Workers CI: Edit', 'Account Settings: Read', 'User Details: Read', 'Cloudflare Pages: Edit (only to link an existing Pages project)'],
    scopeNote: 'Limit it to the account the Workers live in.',
    expiry: 'optional: set an end date (TTL) if your policy needs one, and add a new token here before it ends.',
    notes: ['Install the Cloudflare GitHub or GitLab app in the Cloudflare dashboard before AIDE connects a repository.'],
    accountNoun: 'Account',
  },
  render: {
    id: 'render',
    name: 'Render',
    tokenPageUrl: 'https://dashboard.render.com/u/settings#api-keys',
    tokenPageSite: 'dashboard.render.com',
    tokenNoun: 'API key',
    tokenType: 'an API key (Account Settings → API Keys).',
    permissions: [],
    scopeNote: 'Render API keys have no scopes: the key reaches every workspace you belong to, and you pick the workspace per project.',
    expiry: "Render API keys don't expire. Revoke the key in Render when you no longer need it.",
    notes: ['Connect your Git host (GitHub, GitLab or Bitbucket) to Render once, in the Render dashboard.'],
    accountNoun: 'Workspace',
  },
  railway: {
    id: 'railway',
    name: 'Railway',
    tokenPageUrl: 'https://railway.com/account/tokens',
    tokenPageSite: 'railway.com',
    tokenNoun: 'token',
    tokenType: 'a workspace token (Workspace Settings → Tokens), or an account token if AIDE should reach all your workspaces.',
    permissions: [],
    scopeNote: 'Railway tokens have no scopes: a workspace token reaches only its workspace.',
    expiry: "Railway tokens don't expire. Delete the token in Railway when you no longer need it.",
    notes: ['Install the Railway GitHub App on the repository before AIDE creates services.'],
    accountIdField: {
      label: 'Workspace ID',
      hint: "Needed for a workspace token, which can't tell AIDE its workspace: copy the ID from the workspace's settings. Leave it empty for an account token.",
    },
    accountNoun: 'Workspace',
  },
  supabase: {
    id: 'supabase',
    name: 'Supabase',
    tokenPageUrl: 'https://supabase.com/dashboard/account/tokens',
    tokenPageSite: 'supabase.com',
    tokenNoun: 'access token',
    tokenType: 'a scoped personal access token (Account → Access Tokens), limited to the organisation AIDE should use.',
    permissions: [
      'Projects: Read and write',
      'Database: Read and write',
      'Connection Pooling: Read',
      'API Keys: Read',
      'API Key Secrets: Read',
      'Auth Config: Read and write',
      'Data API Config: Read and write',
      'Organization Projects: Read (for the project picker)',
    ],
    scopeNote: 'Without one of these the connection is saved as "Missing permissions", and AIDE names what to add.',
    expiry: 'pick an expiry date, and add a new token here before it expires.',
    accountNoun: 'Organisation',
  },
}

/** FR-142 sections of /connections. */
export const deploymentPlatformIds = ['vercel', 'netlify', 'cloudflare', 'render', 'railway'] as const satisfies readonly PlatformProvider[]
export const databasePlatformIds = ['supabase'] as const satisfies readonly PlatformProvider[]

/**
 * What the token reaches: "Team acme", "Workspace Acme (+2 more)", or "Personal account (ada)" for a personal
 * Vercel token. `null` when the platform named nothing.
 */
export function platformAccountLabel(c: Pick<PlatformConnection, 'provider' | 'accountName' | 'accountId' | 'metadata'>): string | null {
  const info = platformProviders[c.provider]
  if (c.provider === 'vercel') {
    if (c.metadata.teamSlug) {
      return `Team ${c.metadata.teamSlug}${c.accountName ? ` · account ${c.accountName}` : ''}`
    }
    return c.accountName ? `Personal account (${c.accountName})` : 'Personal account'
  }
  const accounts = c.metadata.accounts
  const primary = c.accountName ?? accounts.find((a) => a.id === c.accountId)?.name ?? accounts[0]?.name ?? c.accountId
  if (!primary) {
    return null
  }
  const others = accounts.filter((a) => a.name !== primary && a.id !== c.accountId).length
  return `${info.accountNoun} ${primary}${others > 0 ? ` (+${String(others)} more)` : ''}`
}
