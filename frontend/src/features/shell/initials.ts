/** Up to two initials from the name (or the email's local part). */
export function initials(name: string, email: string): string {
  const trimmed = name.trim()
  const local = email.split('@')[0] ?? ''
  const source = trimmed !== '' ? trimmed : local !== '' ? local : '?'
  const parts = source.split(/[\s._-]+/).filter(Boolean)
  return ((parts[0]?.[0] ?? '?') + (parts[1]?.[0] ?? '')).toUpperCase()
}
