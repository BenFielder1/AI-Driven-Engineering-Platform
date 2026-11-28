/** FR-366: a fire time (UTC from the API) in the schedule's IANA timezone, with the zone's abbreviation. */
export function formatInZone(iso: string, timeZone: string): string {
  const date = new Date(iso)
  const options: Intl.DateTimeFormatOptions = {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
    timeZoneName: 'short',
  }
  try {
    return new Intl.DateTimeFormat(undefined, { ...options, timeZone }).format(date)
  } catch {
    // A zone this browser doesn't know: show UTC rather than nothing.
    return new Intl.DateTimeFormat(undefined, { ...options, timeZone: 'UTC' }).format(date)
  }
}

/** The browser's IANA timezone, the default for a new schedule. */
export function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
}

/** Five whitespace-separated fields: minute hour day-of-month month day-of-week (§4.71). */
export const cronShape = /^\s*\S+(\s+\S+){4}\s*$/

/** The webhook's URL as a caller uses it (the API may return it relative to this origin). */
export function absoluteUrl(url: string): string {
  try {
    return new URL(url, window.location.origin).toString()
  } catch {
    return url
  }
}

/**
 * §4.72: how a sender signs a call. `X-Tevrin-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256(secret, "<t>.<raw body>")>`
 * plus a fresh `X-Tevrin-Delivery` uuid. The secret is read from an environment variable, never pasted into the script.
 */
export function signingExample(url: string, inputTitle: string | null): string {
  return [
    "export TEVRIN_WEBHOOK_SECRET='<the secret above>'",
    `BODY=$(cat input.json)  # ${inputTitle ? `the ${inputTitle.toLowerCase()}` : 'the input'} as JSON, sent byte for byte`,
    'T=$(date +%s)',
    `SIG=$(printf '%s.%s' "$T" "$BODY" | openssl dgst -sha256 -hmac "$TEVRIN_WEBHOOK_SECRET" | sed 's/^.*= //')`,
    `curl -X POST '${url}' \\`,
    "  -H 'Content-Type: application/json' \\",
    '  -H "X-Tevrin-Signature: t=$T,v1=$SIG" \\',
    '  -H "X-Tevrin-Delivery: $(uuidgen)" \\',
    '  --data "$BODY"',
  ].join('\n')
}
