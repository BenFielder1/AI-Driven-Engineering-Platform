/**
 * FR-98: the same-origin `/api` proxy of the web server (replaces the v2 nginx `location /api/` blocks), including
 * WebSocket upgrades for the SignalR hubs under `/api/hubs/`. Hand-rolled on node:http so the rules are explicit:
 * - method, path, query, body (≤ 10 MB, else 413), Cookie/Authorization/X-XSRF-TOKEN and the upstream status, headers
 *   and Set-Cookie pass through unchanged (no cookie path/domain rewriting; Host is kept, like nginx `$http_host`)
 * - X-Forwarded-For gets the peer address appended; X-Forwarded-Proto/Host are set from this connection and its Host,
 *   unless `trustEdgeForwarded` (env `TRUST_EDGE_FORWARDED=true`, Railway) keeps the values the edge set
 * - a path that resolves outside `/api` (dot segments) → 400 problem+json VALIDATION
 * - API down → 502 problem+json with errorCode UPSTREAM_ERROR; responses gain `X-Content-Type-Options: nosniff`
 * - hub connections (WebSocket, SSE, long polling) may idle for 1 hour
 * Run by Node's type stripping: erasable TypeScript only.
 */
import {
  request as httpRequest,
  type IncomingHttpHeaders,
  type IncomingMessage,
  type OutgoingHttpHeaders,
  type ServerResponse,
} from 'node:http'
import { request as httpsRequest } from 'node:https'
import type { Socket } from 'node:net'
import type { Duplex } from 'node:stream'

export const MAX_BODY_BYTES = 10 * 1024 * 1024
export const HUB_IDLE_TIMEOUT_MS = 60 * 60 * 1000
export const API_IDLE_TIMEOUT_MS = 5 * 60 * 1000
/** A refused upgrade's body is small (a ProblemDetails); anything beyond this is cut off. */
const MAX_REFUSAL_BODY_BYTES = 64 * 1024

export interface ApiProxyOptions {
  /** Upstream origin, e.g. `http://api:8080` (no trailing slash). */
  backendUrl: string
  maxBodyBytes?: number
  /** Keep client-supplied X-Forwarded-Proto/Host. Only behind an edge that always sets them (Railway). Default false. */
  trustEdgeForwarded?: boolean
  /** Called when the upstream can't be reached (message only: never the URL, which may carry an approval token). */
  onUpstreamError?: (error: Error) => void
}

const HOP_BY_HOP = new Set([
  'connection',
  'keep-alive',
  'proxy-authenticate',
  'proxy-authorization',
  'proxy-connection',
  'te',
  'trailer',
  'transfer-encoding',
  'upgrade',
  'http2-settings',
])

/** `/api`, `/api/…` or `/api?…`. */
export function isApiPath(url: string): boolean {
  return url === '/api' || url.startsWith('/api/') || url.startsWith('/api?')
}

export function isHubPath(url: string): boolean {
  return url.startsWith('/api/hubs/')
}

/** The socket's peer address without the IPv4-mapped IPv6 prefix. */
export function peerAddress(address: string | undefined): string {
  if (!address) {
    return 'unknown'
  }
  return address.startsWith('::ffff:') ? address.slice('::ffff:'.length) : address
}

/**
 * Request headers for the upstream: hop-by-hop headers dropped, forwarded headers set (FR-98, FR-105).
 * X-Forwarded-Proto/Host come from this connection and its Host header, overwriting client values, unless
 * `trustEdgeForwarded` is set and the edge supplied them.
 */
export function upstreamRequestHeaders(
  incoming: IncomingHttpHeaders,
  peer: string,
  encrypted: boolean,
  upgrade = false,
  trustEdgeForwarded = false,
): OutgoingHttpHeaders {
  const listed = new Set(
    (typeof incoming.connection === 'string' ? incoming.connection : '')
      .split(',')
      .map((h) => h.trim().toLowerCase())
      .filter(Boolean),
  )
  const headers: OutgoingHttpHeaders = {}
  for (const [name, value] of Object.entries(incoming)) {
    if (value === undefined || HOP_BY_HOP.has(name) || listed.has(name)) {
      continue
    }
    headers[name] = value
  }
  const prior = incoming['x-forwarded-for']
  const priorText = Array.isArray(prior) ? prior.join(', ') : prior
  headers['x-forwarded-for'] = priorText ? `${priorText}, ${peer}` : peer
  if (!trustEdgeForwarded || !incoming['x-forwarded-proto']) {
    headers['x-forwarded-proto'] = encrypted ? 'https' : 'http'
  }
  if (!trustEdgeForwarded || !incoming['x-forwarded-host']) {
    if (incoming.host) {
      headers['x-forwarded-host'] = incoming.host
    } else {
      delete headers['x-forwarded-host']
    }
  }
  if (upgrade && incoming.upgrade) {
    headers.connection = 'Upgrade'
    headers.upgrade = incoming.upgrade
  }
  return headers
}

/** Upstream response headers for the client: hop-by-hop dropped, Set-Cookie untouched, nosniff added if absent. */
export function clientResponseHeaders(upstream: IncomingHttpHeaders): OutgoingHttpHeaders {
  const headers: OutgoingHttpHeaders = {}
  for (const [name, value] of Object.entries(upstream)) {
    if (value !== undefined && !HOP_BY_HOP.has(name)) {
      headers[name] = value
    }
  }
  headers['x-content-type-options'] ??= 'nosniff'
  return headers
}

export interface Problem {
  status: number
  title: string
  detail: string
  errorCode: string
}

export const upstreamProblem: Problem = {
  status: 502,
  title: 'Bad Gateway',
  detail: "The AIDE API couldn't be reached. Try again in a moment.",
  errorCode: 'UPSTREAM_ERROR',
}

export const tooLargeProblem: Problem = {
  status: 413,
  title: 'Payload Too Large',
  detail: 'The request body is larger than 10 MB.',
  errorCode: 'VALIDATION',
}

export const badPathProblem: Problem = {
  status: 400,
  title: 'Bad Request',
  detail: 'The request path is not under /api/.',
  errorCode: 'VALIDATION',
}

export function problemBody(problem: Problem): string {
  return JSON.stringify({ type: 'about:blank', ...problem })
}

/** Writes an RFC 9457 problem response (if nothing was sent yet). */
export function sendProblem(res: ServerResponse, problem: Problem): void {
  if (res.headersSent) {
    res.destroy()
    return
  }
  const body = problemBody(problem)
  res.writeHead(problem.status, {
    'content-type': 'application/problem+json',
    'content-length': Buffer.byteLength(body),
    'x-content-type-options': 'nosniff',
    'cache-control': 'no-store',
  })
  res.end(body)
}

/**
 * The upstream URL for a request target, or undefined when it resolves outside the backend's `/api` (dot segments such
 * as `/api/../admin`, `/api/%2e%2e/x` or `/api\..\x`, which URL parsing normalises).
 */
export function upstreamTarget(url: string, backendUrl: string): URL | undefined {
  let target: URL
  let backend: URL
  try {
    backend = new URL(`${backendUrl}/`)
    target = new URL(url, backend)
  } catch {
    return undefined
  }
  if (target.origin !== backend.origin || !(target.pathname === '/api' || target.pathname.startsWith('/api/'))) {
    return undefined
  }
  return target
}

function requestFor(target: URL) {
  return target.protocol === 'https:' ? httpsRequest : httpRequest
}

function isEncrypted(req: IncomingMessage): boolean {
  return (req.socket as Socket & { encrypted?: boolean }).encrypted === true
}

/** Proxies one HTTP request under /api to the backend. */
export function proxyHttp(req: IncomingMessage, res: ServerResponse, options: ApiProxyOptions): void {
  const url = req.url ?? '/'
  const max = options.maxBodyBytes ?? MAX_BODY_BYTES
  const declared = Number(req.headers['content-length'] ?? '0')
  if (Number.isFinite(declared) && declared > max) {
    // Answered before any body byte is read; `connection: close` (unbounded draining would read up to `declared`).
    res.setHeader('connection', 'close')
    sendProblem(res, tooLargeProblem)
    req.resume()
    return
  }
  const target = upstreamTarget(url, options.backendUrl)
  if (!target) {
    sendProblem(res, badPathProblem)
    req.resume()
    return
  }
  let tooLarge = false
  const upstream = requestFor(target)(
    {
      protocol: target.protocol,
      hostname: target.hostname,
      port: target.port,
      method: req.method,
      path: `${target.pathname}${target.search}`,
      headers: upstreamRequestHeaders(
        req.headers,
        peerAddress(req.socket.remoteAddress),
        isEncrypted(req),
        false,
        options.trustEdgeForwarded,
      ),
    },
    (upstreamRes) => {
      res.writeHead(upstreamRes.statusCode ?? 502, upstreamRes.statusMessage, clientResponseHeaders(upstreamRes.headers))
      upstreamRes.pipe(res)
      upstreamRes.on('error', () => res.destroy())
    },
  )
  upstream.setTimeout(isHubPath(url) ? HUB_IDLE_TIMEOUT_MS : API_IDLE_TIMEOUT_MS, () => {
    upstream.destroy(new Error('Upstream idle timeout'))
  })
  upstream.on('error', (error) => {
    if (tooLarge) {
      return
    }
    options.onUpstreamError?.(error)
    sendProblem(res, upstreamProblem)
  })
  res.on('close', () => {
    if (!res.writableFinished) {
      upstream.destroy()
    }
  })

  let received = 0
  req.on('data', (chunk: Buffer) => {
    received += chunk.length
    if (received > max && !tooLarge) {
      tooLarge = true
      req.unpipe(upstream)
      upstream.destroy()
      rejectAfterDrain(req, res, tooLargeProblem)
    }
  })
  req.pipe(upstream)
}

/** After a 413 on a streamed body, at most this much more is read (and discarded) before the connection closes. */
export const DRAIN_LIMIT_BYTES = 4 * 1024 * 1024
export const DRAIN_TIMEOUT_MS = 5_000

/**
 * Sends `problem` with `connection: close`, but finishes the response only once the rest of the request body has been
 * read and discarded (bounded by DRAIN_LIMIT_BYTES / DRAIN_TIMEOUT_MS). Closing with unread bytes in the kernel buffer
 * makes it send RST, and the client may then see ECONNRESET instead of the reply.
 */
function rejectAfterDrain(req: IncomingMessage, res: ServerResponse, problem: Problem): void {
  if (res.headersSent) {
    res.destroy()
    req.resume()
    return
  }
  const body = problemBody(problem)
  res.writeHead(problem.status, {
    'content-type': 'application/problem+json',
    'content-length': Buffer.byteLength(body),
    'x-content-type-options': 'nosniff',
    'cache-control': 'no-store',
    connection: 'close',
  })
  res.write(body)
  let drained = 0
  const finish = () => {
    clearTimeout(timer)
    req.off('data', onData)
    if (!res.writableEnded) {
      res.end()
    }
  }
  const onData = (chunk: Buffer) => {
    drained += chunk.length
    if (drained > DRAIN_LIMIT_BYTES) {
      finish()
    }
  }
  const timer = setTimeout(finish, DRAIN_TIMEOUT_MS)
  req.on('data', onData)
  req.once('end', finish)
  req.once('error', finish)
  req.once('close', finish)
  req.resume()
}

/** An HTTP/1.1 status line plus the given raw headers (without the blank line), minus the skipped names. */
function rawHead(status: number, message: string, rawHeaders: string[], skip: (lowerName: string) => boolean = () => false): string {
  let head = `HTTP/1.1 ${String(status)} ${message}\r\n`
  for (let i = 0; i + 1 < rawHeaders.length; i += 2) {
    const name = rawHeaders[i] ?? ''
    if (!skip(name.toLowerCase())) {
      head += `${name}: ${rawHeaders[i + 1] ?? ''}\r\n`
    }
  }
  return head
}

/** Ends an upgrade socket with a complete, non-upgraded response: hop-by-hop headers dropped, exact Content-Length. */
function endRawResponse(socket: Duplex, status: number, message: string, rawHeaders: string[], body: Buffer): void {
  const head = rawHead(status, message, rawHeaders, (name) => HOP_BY_HOP.has(name) || name === 'content-length')
  socket.end(Buffer.concat([Buffer.from(`${head}content-length: ${String(body.length)}\r\nconnection: close\r\n\r\n`), body]))
}

function endWithProblem(socket: Duplex, problem: Problem): void {
  const body = Buffer.from(problemBody(problem))
  endRawResponse(
    socket,
    problem.status,
    problem.title,
    ['content-type', 'application/problem+json', 'x-content-type-options', 'nosniff'],
    body,
  )
}

/** Pipes a WebSocket upgrade under /api/hubs/ to the backend (1 hour idle timeout). */
export function proxyUpgrade(req: IncomingMessage, socket: Duplex, head: Buffer, options: ApiProxyOptions): void {
  socket.on('error', () => socket.destroy())
  const target = upstreamTarget(req.url ?? '/', options.backendUrl)
  if (!target) {
    endWithProblem(socket, badPathProblem)
    return
  }
  const upstream = requestFor(target)({
    protocol: target.protocol,
    hostname: target.hostname,
    port: target.port,
    method: req.method,
    path: `${target.pathname}${target.search}`,
    headers: upstreamRequestHeaders(
      req.headers,
      peerAddress(req.socket.remoteAddress),
      isEncrypted(req),
      true,
      options.trustEdgeForwarded,
    ),
  })
  // The browser went away before the API answered: abort the upstream request (and any upgrade it would have made).
  // The server's sockets allow half-open connections, so a client FIN shows up as 'end' without 'close'.
  let clientGone = false
  const abortUpstream = () => {
    clientGone = true
    upstream.destroy()
    socket.destroy()
  }
  socket.once('end', abortUpstream)
  socket.once('close', abortUpstream)
  upstream.on('upgrade', (upstreamRes, upstreamSocket, upstreamHead) => {
    socket.off('end', abortUpstream)
    socket.off('close', abortUpstream)
    if (clientGone || socket.destroyed) {
      upstreamSocket.destroy()
      return
    }
    socket.write(`${rawHead(101, 'Switching Protocols', upstreamRes.rawHeaders)}\r\n`)
    if (upstreamHead.length > 0) {
      socket.write(upstreamHead)
    }
    if (head.length > 0) {
      upstreamSocket.write(head)
    }
    const close = () => {
      socket.destroy()
      upstreamSocket.destroy()
    }
    for (const s of [socket, upstreamSocket]) {
      ;(s as Partial<Socket>).setTimeout?.(HUB_IDLE_TIMEOUT_MS)
      s.on('timeout', close)
      s.on('error', close)
      s.on('close', close)
    }
    upstreamSocket.pipe(socket).pipe(upstreamSocket)
  })
  // The API answered without upgrading (401, 404…): relay it as a complete HTTP/1.1 response and close. The (small)
  // body is buffered, already de-chunked by node:http, so it goes out with an exact Content-Length.
  upstream.on('response', (upstreamRes) => {
    const chunks: Buffer[] = []
    let size = 0
    upstreamRes.on('data', (chunk: Buffer) => {
      if (size < MAX_REFUSAL_BODY_BYTES) {
        chunks.push(chunk)
        size += chunk.length
      }
    })
    upstreamRes.on('end', () => {
      const body = Buffer.concat(chunks).subarray(0, MAX_REFUSAL_BODY_BYTES)
      endRawResponse(socket, upstreamRes.statusCode ?? 502, upstreamRes.statusMessage ?? '', upstreamRes.rawHeaders, body)
    })
    upstreamRes.on('error', () => socket.destroy())
  })
  upstream.on('error', (error) => {
    if (socket.destroyed) {
      return // aborted because the client closed: not an upstream failure
    }
    options.onUpstreamError?.(error)
    endWithProblem(socket, upstreamProblem)
  })
  upstream.end()
}
