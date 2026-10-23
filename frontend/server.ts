/**
 * AIDE web server (FR-98, FR-99, FR-100, FR-102, FR-103): a minimal custom Node server around Next.js.
 *
 * Why a custom server: Next.js 16 can proxy `/api` through `rewrites()` (fixed at build time in standalone output) or
 * `proxy.ts` rewrites, but its built-in proxy rewrites Host (changeOrigin), times out after 30 s, answers 500 (not a
 * 502 problem+json) when the API is down, and logs the full upstream URL on failure (approval tokens, FR-100). So this
 * server owns `/api/*` (HTTP and the SignalR WebSocket upgrades under `/api/hubs/`) and hands everything else to Next.
 *
 * - `npm run dev`  → `node server.ts --dev` on http://localhost:3000 (Next dev + HMR), API at BACKEND_URL
 *   (default http://localhost:5080)
 * - `npm start` / the image → production on PORT (default 8080). In the image this file runs from the standalone
 *   output directory and loads Next's build config from `.next/required-server-files.json`.
 * Runs with Node's built-in TypeScript type stripping (Node ≥ 24): erasable syntax only, `.ts` import specifiers.
 */
import { EventEmitter } from 'node:events'
import { existsSync, readFileSync } from 'node:fs'
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http'
import path from 'node:path'
import { performance } from 'node:perf_hooks'
import { fileURLToPath } from 'node:url'

import { parseServerEnv } from './src/lib/env.ts'
import { isApiPath, isHubPath, proxyHttp, proxyUpgrade } from './src/server/api-proxy.ts'
import { errorLogLine, requestLogLine, shouldLogRequest } from './src/server/request-log.ts'
import { createNonce, pageSecurityHeaders } from './src/server/security-headers.ts'

const dev = process.argv.includes('--dev')
Object.assign(process.env, { NODE_ENV: dev ? 'development' : 'production' })

const dir = path.dirname(fileURLToPath(import.meta.url))
const env = parseServerEnv(process.env)
const port = env.PORT ?? (dev ? 3000 : 8080)

function logError(message: string, error: unknown): void {
  process.stderr.write(`${errorLogLine(message, error)}\n`)
}

// Standalone/production: use the config captured at build time (no next.config.ts at runtime).
let conf: Record<string, unknown> | undefined
const requiredServerFiles = path.join(dir, '.next', 'required-server-files.json')
if (!dev && existsSync(requiredServerFiles)) {
  const parsed = JSON.parse(readFileSync(requiredServerFiles, 'utf8')) as { config: Record<string, unknown> }
  conf = parsed.config
  process.env.__NEXT_PRIVATE_STANDALONE_CONFIG = JSON.stringify(conf)
}

const { default: next } = await import('next')

// Next attaches its upgrade handler (dev HMR) to `httpServer`; give it a relay so /api/hubs/ upgrades never reach it.
const nextUpgrades = new EventEmitter()
const app = next({
  dev,
  dir,
  port,
  hostname: 'localhost',
  httpServer: nextUpgrades as unknown as Server,
  ...(conf ? { conf } : {}),
})
const handle = app.getRequestHandler()
await app.prepare()

const proxyOptions = {
  backendUrl: env.BACKEND_URL,
  trustEdgeForwarded: env.TRUST_EDGE_FORWARDED,
  onUpstreamError: (error: Error) => {
    logError('api proxy upstream error', error)
  },
}

function logRequest(req: IncomingMessage, res: ServerResponse, started: number): void {
  if (!shouldLogRequest(req.url)) {
    return
  }
  const line = requestLogLine({
    method: req.method,
    url: req.url,
    status: res.statusCode,
    durationMs: performance.now() - started,
    referer: req.headers.referer,
    aborted: !res.writableFinished,
  })
  process.stdout.write(`${line}\n`)
}

const server = createServer((req, res) => {
  const started = performance.now()
  res.once('close', () => {
    logRequest(req, res, started)
  })
  const url = req.url ?? '/'
  if (isApiPath(url)) {
    proxyHttp(req, res, proxyOptions)
    return
  }
  // FR-99: per-request nonce. Next.js reads it from the request's CSP header for its own inline scripts; the root
  // layout reads `x-nonce`. Client-supplied values are always overwritten.
  const nonce = createNonce()
  const headers = pageSecurityHeaders(nonce, dev, url)
  req.headers['x-nonce'] = nonce
  req.headers['content-security-policy'] = headers['Content-Security-Policy']
  for (const [name, value] of Object.entries(headers)) {
    res.setHeader(name, value)
  }
  handle(req, res).catch((error: unknown) => {
    logError('request handler error', error)
    if (!res.headersSent) {
      res.statusCode = 500
      res.end('Internal Server Error')
    }
  })
})

server.on('upgrade', (req: IncomingMessage, socket, head: Buffer) => {
  const url = req.url ?? '/'
  if (isHubPath(url)) {
    proxyUpgrade(req, socket, head, proxyOptions)
  } else if (isApiPath(url)) {
    socket.destroy()
  } else if (nextUpgrades.listenerCount('upgrade') > 0) {
    // Dev only: Next's HMR WebSocket (/_next/webpack-hmr). Production Next registers no upgrade handler.
    nextUpgrades.emit('upgrade', req, socket, head)
  } else {
    socket.destroy()
  }
})

server.keepAliveTimeout = 65_000

server.on('error', (error) => {
  logError('web server failed to start', error)
  process.exit(1)
})

server.listen(port, () => {
  process.stdout.write(
    `${JSON.stringify({ timestamp: new Date().toISOString(), level: 'info', msg: 'web server listening', port, dev })}\n`,
  )
})

for (const signal of ['SIGTERM', 'SIGINT'] as const) {
  process.once(signal, () => {
    server.close(() => process.exit(0))
    setTimeout(() => process.exit(0), 10_000).unref()
  })
}
