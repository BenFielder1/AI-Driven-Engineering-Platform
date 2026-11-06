#!/usr/bin/env node
// CI: first-load JS budget for `/` (0.6.0 SPEC §8 Performance, milestone M43). Run after `npm run build`.
//
// Usage: node scripts/ci/check-js-budget.mjs [frontend-dir]   (default: ./frontend, or . when run from frontend/)
// Exit:  0 within budget · 1 over budget · 2 the build output for `/` could not be found or read (never a silent pass)
//
// How the route's JS is determined (Next 16 App Router, Turbopack; no route-size table is relied on). It mirrors what
// Next's app renderer emits as <script> tags (next/dist/server/app-render):
//   1. .next/app-path-routes-manifest.json maps the app page (e.g. "/(marketing)/(home)/page") to the route "/".
//   2. Bootstrap scripts: build-manifest.json rootMainFilesTree[page] || rootMainFiles (required-scripts.js).
//      polyfillFiles are excluded: they are served `noModule`, so modern browsers never download them.
//   3. Segment scripts: entryJSFiles of the page's client reference manifest
//      (.next/server/app/<page>_client-reference-manifest.js) for the layouts, page, template, error and loading
//      segments. not-found / forbidden / unauthorized / global-error / global-not-found only contribute styles on a
//      normal render (create-component-tree.js, getGlobalErrorStyles), so their scripts are not counted.
//   4. Cross-check: when `/` is prerendered (.next/server/app/index.html), every module <script src> in that HTML is
//      added too, so a file Next actually ships can never be missed. Files are deduplicated.
// Each file is gzipped with node:zlib at level 9 and the sizes are summed.
import { existsSync, readFileSync, appendFileSync } from 'node:fs'
import path from 'node:path'
import vm from 'node:vm'
import { gzipSync, constants as zlibConstants } from 'node:zlib'

// SPEC §8 Performance: "first-load JS for `/` ≤ 200 KB gzipped (checked from the Next build output in CI)".
const BUDGET_BYTES = 200 * 1024
const ROUTE = '/'

function fail(message) {
  console.error(`::error::JS budget: ${message}`)
  process.exit(2)
}

function readJson(file) {
  if (!existsSync(file)) fail(`missing ${file} (run \`npm run build\` first)`)
  try {
    return JSON.parse(readFileSync(file, 'utf8'))
  } catch (error) {
    fail(`cannot parse ${file}: ${error.message}`)
  }
}

const frontendDir = path.resolve(
  process.argv[2] ?? (existsSync(path.resolve('frontend', 'package.json')) ? 'frontend' : '.'),
)
const nextDir = path.join(frontendDir, '.next')
if (!existsSync(nextDir)) fail(`no build output at ${nextDir} (run \`npm run build\` first)`)

// 1. The app page that serves `/`.
const appPathRoutes = readJson(path.join(nextDir, 'app-path-routes-manifest.json'))
const pages = Object.entries(appPathRoutes)
  .filter(([page, route]) => route === ROUTE && page.endsWith('/page'))
  .map(([page]) => page)
if (pages.length !== 1) fail(`expected one app page for "${ROUTE}" in app-path-routes-manifest.json, found ${pages.length}`)
const page = pages[0]

// 2. Bootstrap scripts.
const buildManifest = readJson(path.join(nextDir, 'build-manifest.json'))
const rootMain = buildManifest.rootMainFilesTree?.[page] ?? buildManifest.rootMainFiles
if (!Array.isArray(rootMain) || rootMain.length === 0) fail('build-manifest.json has no rootMainFiles')

// 3. Segment scripts from the client reference manifest (a JS file that assigns globalThis.__RSC_MANIFEST[page]).
const crmFile = path.join(nextDir, 'server', 'app', `${page.replace(/^\//, '')}_client-reference-manifest.js`)
if (!existsSync(crmFile)) fail(`missing client reference manifest ${crmFile}`)
const sandbox = {}
sandbox.globalThis = sandbox
sandbox.self = sandbox
try {
  vm.runInNewContext(readFileSync(crmFile, 'utf8'), sandbox, { filename: crmFile, timeout: 5000 })
} catch (error) {
  fail(`cannot evaluate ${crmFile}: ${error.message}`)
}
const crm = sandbox.__RSC_MANIFEST?.[page]
if (!crm || typeof crm.entryJSFiles !== 'object' || crm.entryJSFiles === null) {
  fail(`${crmFile} has no entryJSFiles for "${page}"`)
}
const STYLE_ONLY_SEGMENTS = /\/(not-found|forbidden|unauthorized|global-error|global-not-found)$/
const pageEntry = page.replace(/\/page$/, '') + '/page'
const segmentKeys = Object.keys(crm.entryJSFiles).filter((key) => !STYLE_ONLY_SEGMENTS.test(key))
if (!segmentKeys.some((key) => key.endsWith(`/app${pageEntry}`) || key.endsWith('/layout'))) {
  fail(`no layout/page entries for "${page}" in ${crmFile} (keys: ${Object.keys(crm.entryJSFiles).join(', ')})`)
}

const files = new Map() // relative to .next, e.g. static/chunks/abc.js -> source
const add = (file, source) => {
  const rel = decodeURI(file).replace(/^\/?_next\//, '').replace(/^\//, '').split('?')[0]
  if (rel.endsWith('.js') && !files.has(rel)) files.set(rel, source)
}
for (const file of rootMain) add(file, 'rootMainFiles')
for (const key of segmentKeys) {
  for (const file of crm.entryJSFiles[key]) add(file, key.replace(/^\[project\]\//, ''))
}

// 4. Cross-check against the prerendered HTML when `/` is static.
const htmlFile = path.join(nextDir, 'server', 'app', 'index.html')
let htmlNote = 'not prerendered (no .next/server/app/index.html); manifests only'
if (existsSync(htmlFile)) {
  const html = readFileSync(htmlFile, 'utf8')
  let count = 0
  let extra = 0
  for (const [tag, src] of [...html.matchAll(/<script\b[^>]*?\bsrc="([^"]+)"[^>]*>/gi)].map((m) => [m[0], m[1]])) {
    if (/\bnomodule\b/i.test(tag) || !src.includes('/_next/')) continue
    count++
    const before = files.size
    add(src.slice(src.indexOf('/_next/')), 'index.html')
    if (files.size > before) extra++
  }
  htmlNote = `index.html lists ${count} module script(s); ${extra} not in the manifests (counted)`
}

if (files.size === 0) fail(`no JS files found for "${ROUTE}"`)

// Gzip and sum.
const rows = []
let total = 0
for (const [rel, source] of files) {
  const abs = path.join(nextDir, rel)
  if (!existsSync(abs)) fail(`${rel} is referenced by the build manifests but missing from ${nextDir}`)
  const raw = readFileSync(abs)
  const gz = gzipSync(raw, { level: zlibConstants.Z_BEST_COMPRESSION }).length
  total += gz
  rows.push({ rel, source, raw: raw.length, gz })
}
rows.sort((a, b) => b.gz - a.gz)

const kb = (n) => `${(n / 1024).toFixed(1)} KB`
const over = total > BUDGET_BYTES
const verdict = `${over ? 'OVER' : 'within'} budget: ${kb(total)} gzipped of ${kb(BUDGET_BYTES)} (${total} / ${BUDGET_BYTES} bytes)`

const width = Math.max(...rows.map((r) => r.rel.length), 4)
console.log(`First-load JS for "${ROUTE}" (app page ${page}), gzip level 9`)
console.log(`${'File'.padEnd(width)}  ${'Raw'.padStart(10)}  ${'Gzip'.padStart(10)}  Source`)
for (const r of rows) {
  console.log(`${r.rel.padEnd(width)}  ${kb(r.raw).padStart(10)}  ${kb(r.gz).padStart(10)}  ${r.source}`)
}
console.log(`${'Total'.padEnd(width)}  ${''.padStart(10)}  ${kb(total).padStart(10)}`)
console.log(`Cross-check: ${htmlNote}`)
console.log(verdict)

if (process.env.GITHUB_STEP_SUMMARY) {
  const lines = [
    `### First-load JS for \`${ROUTE}\` ${over ? ':x:' : ':white_check_mark:'}`,
    '',
    `${verdict} (SPEC §8). App page \`${page}\`; ${htmlNote}.`,
    '',
    '| File | Raw | Gzip | Source |',
    '| --- | ---: | ---: | --- |',
    ...rows.map((r) => `| \`${r.rel}\` | ${kb(r.raw)} | ${kb(r.gz)} | \`${r.source}\` |`),
    `| **Total** | | **${kb(total)}** | |`,
    '',
  ]
  appendFileSync(process.env.GITHUB_STEP_SUMMARY, lines.join('\n') + '\n')
}

if (over) {
  console.error(`::error::First-load JS for "${ROUTE}" is ${kb(total)} gzipped, over the ${kb(BUDGET_BYTES)} budget (SPEC §8)`)
  process.exit(1)
}
