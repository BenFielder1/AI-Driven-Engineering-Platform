/** Client-side checks mirroring the API (CreateVercelProjectRequest, PutDeploymentEnv); the API re-validates. */

const PROJECT_NAME = /^[a-z0-9._-]{1,100}$/

/** FR-88: lowercase letters, digits, `.`, `_`, `-`, up to 100, without `---`. */
export function checkVercelProjectName(v: string): string | null {
  const t = v.trim()
  if (!t) {
    return 'Enter a name for the new Vercel project.'
  }
  if (!PROJECT_NAME.test(t) || t.includes('---')) {
    return 'Use up to 100 lowercase letters, digits, ".", "_" or "-", without "---".'
  }
  return null
}

/** A valid Vercel project name from a project slug or name ("Inventory Tracker" → "inventory-tracker"). */
export function suggestVercelProjectName(text: string): string {
  return (
    text
      .toLowerCase()
      .replace(/[^a-z0-9._-]+/g, '-')
      .replace(/-{3,}/g, '--')
      .replace(/^[-.]+|[-.]+$/g, '')
      .slice(0, 100) || 'aide-app'
  )
}

/** The app's directory in the repository: relative, no `..`. Empty means the repository root (`.`). */
export function checkRootDirectory(v: string): string | null {
  const t = v.trim()
  if (t === '' || t === '.') {
    return null
  }
  if (t.startsWith('/') || t.split('/').includes('..') || t.length > 255) {
    return 'Enter a directory inside the repository, such as "apps/web" (no leading "/" or "..").'
  }
  return null
}

const ENV_KEY = /^[A-Za-z_][A-Za-z0-9_]*$/
export const ENV_KEY_MAX = 256
export const ENV_VALUE_MAX = 64 * 1024

/** FR-88: `^[A-Za-z_][A-Za-z0-9_]*$`, up to 256. */
export function checkEnvKey(v: string): string | null {
  const t = v.trim()
  if (!t) {
    return 'Enter the variable name, such as NEXT_PUBLIC_API_URL.'
  }
  if (t.length > ENV_KEY_MAX) {
    return `Names are at most ${String(ENV_KEY_MAX)} characters.`
  }
  return ENV_KEY.test(t) ? null : 'Use letters, digits and "_" only, not starting with a digit.'
}
