/**
 * §4.10 release versions, mirroring backend `Aide.Domain/Releases/SemVer.cs` and `ReleaseVersionRules.cs` so the
 * Start new version dialog (FR-60, FR-61) and the wizard (FR-66) validate as the server does. The server still
 * decides: these only give live feedback.
 */

/** Core parts are `bigint`s: the domain parses them as C# `long`, beyond `Number`'s exact range. */
export interface SemVer {
  major: bigint
  minor: bigint
  patch: bigint
  /** Dot-separated pre-release identifiers; empty for a release. */
  prerelease: readonly string[]
}

/** C# `long.MaxValue`: the domain (`long.TryParse`) refuses larger core parts as VERSION_INVALID. */
const LONG_MAX = 9_223_372_036_854_775_807n

/** The longest version text accepted (as the domain). */
export const VERSION_MAX_LENGTH = 64

/** FR-66: the initial version of a new project when none is given. */
export const DEFAULT_INITIAL_VERSION = '0.1.0'

// SemVer 2.0.0 (semver.org regex) without build metadata.
const pattern =
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?$/

/** Parses `MAJOR.MINOR.PATCH[-prerelease]`; one leading `v`/`V` and surrounding whitespace are stripped. */
export function parseSemVer(text: string | null | undefined): SemVer | null {
  let trimmed = (text ?? '').trim()
  if (trimmed.startsWith('v') || trimmed.startsWith('V')) {
    trimmed = trimmed.slice(1)
  }
  if (trimmed.length === 0 || trimmed.length > VERSION_MAX_LENGTH) {
    return null
  }
  const match = pattern.exec(trimmed)
  if (!match) {
    return null
  }
  const [, major = '', minor = '', patch = '', pre] = match
  const parts = [BigInt(major), BigInt(minor), BigInt(patch)] as const
  if (parts.some((n) => n > LONG_MAX)) {
    return null
  }
  return { major: parts[0], minor: parts[1], patch: parts[2], prerelease: pre ? pre.split('.') : [] }
}

/** Normalised text without the leading `v`, e.g. `1.2.0-rc.1`. */
export function formatSemVer(v: SemVer): string {
  const core = `${String(v.major)}.${String(v.minor)}.${String(v.patch)}`
  return v.prerelease.length > 0 ? `${core}-${v.prerelease.join('.')}` : core
}

/** `v1.2.0` → `1.2.0`; `null` when the text isn't a version. */
export function normalizeVersion(text: string | null | undefined): string | null {
  const parsed = parseSemVer(text)
  return parsed ? formatSemVer(parsed) : null
}

const isNumeric = (s: string) => /^\d+$/.test(s)

function compareIdentifier(a: string, b: string): number {
  const an = isNumeric(a)
  const bn = isNumeric(b)
  if (an && bn) {
    // No leading zeros, so a longer number is larger; equal lengths compare as text.
    return a.length !== b.length ? Math.sign(a.length - b.length) : a < b ? -1 : a > b ? 1 : 0
  }
  if (an !== bn) {
    return an ? -1 : 1
  }
  return a < b ? -1 : a > b ? 1 : 0
}

/** SemVer 2.0.0 precedence: negative when `a < b`, 0 when equal, positive when `a > b`. */
export function compareSemVer(a: SemVer, b: SemVer): number {
  const cmp = (x: bigint, y: bigint) => (x < y ? -1 : x > y ? 1 : 0)
  const core = cmp(a.major, b.major) || cmp(a.minor, b.minor) || cmp(a.patch, b.patch)
  if (core !== 0) {
    return core
  }
  // A release ranks above any of its pre-releases.
  if ((a.prerelease.length > 0) !== (b.prerelease.length > 0)) {
    return a.prerelease.length > 0 ? -1 : 1
  }
  for (let i = 0; i < Math.min(a.prerelease.length, b.prerelease.length); i++) {
    const c = compareIdentifier(a.prerelease[i] ?? '', b.prerelease[i] ?? '')
    if (c !== 0) {
      return c
    }
  }
  return Math.sign(a.prerelease.length - b.prerelease.length)
}

export type VersionCheck =
  | { ok: true; version: string }
  | { ok: false; code: 'VERSION_INVALID' | 'VERSION_NOT_GREATER'; message: string }

/**
 * FR-61 client-side check of a new version: valid semver (VERSION_INVALID), and strictly greater than the released
 * version when there is one (VERSION_NOT_GREATER). `existing` lists versions the project already has (also refused,
 * as the domain does). TAG_EXISTS can only be checked by the server.
 */
export function checkNewVersion(
  input: string,
  released: string | null | undefined,
  existing: readonly string[] = [],
): VersionCheck {
  const parsed = parseSemVer(input)
  if (!parsed) {
    return {
      ok: false,
      code: 'VERSION_INVALID',
      message: 'Enter a version as MAJOR.MINOR.PATCH with an optional -prerelease, for example 1.2.0 or 2.0.0-beta.1.',
    }
  }
  const version = formatSemVer(parsed)
  const base = released ? parseSemVer(released) : null
  if (base && compareSemVer(parsed, base) <= 0) {
    return {
      ok: false,
      code: 'VERSION_NOT_GREATER',
      message: `The version must be greater than the released version ${formatSemVer(base)}.`,
    }
  }
  if (existing.some((e) => normalizeVersion(e) === version)) {
    return { ok: false, code: 'VERSION_NOT_GREATER', message: `Version ${version} already exists in this project.` }
  }
  return { ok: true, version }
}

/** The prefill of Start new version (FR-60): the next minor of the released version, else the initial version. */
export function suggestNextVersion(released: string | null | undefined): string {
  const parsed = released ? parseSemVer(released) : null
  return parsed ? `${String(parsed.major)}.${String(parsed.minor + 1n)}.0` : DEFAULT_INITIAL_VERSION
}
