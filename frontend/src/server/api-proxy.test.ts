import { once } from 'node:events'
import {
  createServer,
  request,
  type IncomingHttpHeaders,
  type IncomingMessage,
  type OutgoingHttpHeaders,
  type Server,
} from 'node:http'
import { connect, type AddressInfo, type Socket } from 'node:net'
import type { Duplex } from 'node:stream'

import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { isApiPath, isHubPath, MAX_BODY_BYTES, proxyHttp, proxyUpgrade, type ApiProxyOptions } from './api-proxy'

/**
 * FR-98: the /api proxy over real sockets. A stub upstream (the "API") and the proxy (wired like server.ts) listen on
 * ephemeral ports on 127.0.0.1. (Runs in the default jsdom environment: src/test/setup.ts needs DOM globals; node:http is unaffected.)
 */

interface Seen {
  method: string | undefined
  url: string | undefined
  headers: IncomingHttpHeaders
  bodyBytes: number
  body: string
}

let upstream: Server
let proxy: Server
let downProxy: Server
let proxyPort = 0
let downProxyPort = 0
const seen: Seen[] = []
const upstreamErrors: Error[] = []
let slowUpstreamSocket: Duplex | undefined
let slowUpstreamClosed = false

function port(server: Server): number {
  return (server.address() as AddressInfo).port
}

async function listen(server: Server): Promise<void> {
  server.listen(0, '127.0.0.1')
  await once(server, 'listening')
}

function proxyServer(options: ApiProxyOptions): Server {
  const server = createServer((req, res) => {
    if (isApiPath(req.url ?? '/')) {
      proxyHttp(req, res, options)
    } else {
      res.writeHead(404).end()
    }
  })
  server.on('upgrade', (req: IncomingMessage, socket: Duplex, head: Buffer) => {
    if (isHubPath(req.url ?? '/')) {
      proxyUpgrade(req, socket, head, options)
    } else {
      socket.destroy()
    }
  })
  return server
}

beforeAll(async () => {
  upstream = createServer((req, res) => {
    const chunks: Buffer[] = []
    req.on('data', (c: Buffer) => chunks.push(c))
    req.on('end', () => {
      const body = Buffer.concat(chunks)
      seen.push({ method: req.method, url: req.url, headers: req.headers, bodyBytes: body.length, body: body.toString('utf8') })
      res.writeHead(201, 'Created', {
        'content-type': 'application/json',
        'set-cookie': ['aide.auth=abc; Path=/; HttpOnly; SameSite=Lax', 'XSRF-TOKEN=x1; Path=/'],
        'x-api': '1',
      })
      res.end(JSON.stringify({ ok: true }))
    })
    req.on('error', () => undefined)
  })
  upstream.on('upgrade', (req: IncomingMessage, socket: Duplex) => {
    if (req.url?.startsWith('/api/hubs/slow')) {
      // Upgrades only after a delay, by which time the test's client has gone away.
      slowUpstreamSocket = socket
      socket.once('end', () => {
        slowUpstreamClosed = true
      })
      socket.once('close', () => {
        slowUpstreamClosed = true
      })
      setTimeout(() => {
        if (!socket.destroyed) {
          socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n')
        }
      }, 300)
      socket.on('error', () => undefined)
      return
    }
    seen.push({ method: req.method, url: req.url, headers: req.headers, bodyBytes: 0, body: '' })
    if (req.url?.startsWith('/api/hubs/refuse')) {
      // Not upgraded: a chunked 401 with keep-alive, which the proxy must turn into a complete Content-Length response.
      const body = '{"status":401,"errorCode":"UNAUTHENTICATED"}'
      socket.end(
        `HTTP/1.1 401 Unauthorized\r\nContent-Type: application/problem+json\r\nTransfer-Encoding: chunked\r\nConnection: keep-alive\r\nKeep-Alive: timeout=5\r\n\r\n${body.length.toString(16)}\r\n${body}\r\n0\r\n\r\n`,
      )
      return
    }
    socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: stub\r\n\r\n')
    socket.on('data', (data: Buffer) => socket.write(`echo:${data.toString('utf8')}`))
    socket.on('error', () => undefined)
  })
  await listen(upstream)

  proxy = proxyServer({ backendUrl: `http://127.0.0.1:${String(port(upstream))}` })
  await listen(proxy)
  proxyPort = port(proxy)

  // A backend port with nothing listening on it.
  const closed = createServer()
  await listen(closed)
  const closedPort = port(closed)
  closed.close()
  await once(closed, 'close')
  downProxy = proxyServer({
    backendUrl: `http://127.0.0.1:${String(closedPort)}`,
    onUpstreamError: (error) => upstreamErrors.push(error),
  })
  await listen(downProxy)
  downProxyPort = port(downProxy)
})

afterAll(() => {
  for (const server of [proxy, downProxy, upstream]) {
    server.closeAllConnections()
    server.close()
  }
})

interface Reply {
  status: number
  headers: IncomingHttpHeaders
  body: string
}

function readReply(res: IncomingMessage): Promise<Reply> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = []
    res.on('data', (c: Buffer) => chunks.push(c))
    res.on('end', () => {
      resolve({ status: res.statusCode ?? 0, headers: res.headers, body: Buffer.concat(chunks).toString('utf8') })
    })
    res.on('error', reject)
  })
}

/** One request through the proxy; `body` may be a string or a generator of chunks (sent chunked). */
function send(
  options: { method?: string; path: string; headers?: OutgoingHttpHeaders; port?: number },
  body?: string | (() => Iterable<Buffer>),
): Promise<Reply> {
  return new Promise((resolve, reject) => {
    const req = request(
      {
        host: '127.0.0.1',
        port: options.port ?? proxyPort,
        method: options.method ?? 'GET',
        path: options.path,
        headers: options.headers,
        agent: false,
      },
      (res) => {
        answered = true
        readReply(res).then(resolve, reject)
      },
    )
    let answered = false
    // Once the reply has arrived, a later socket error (the proxy closing after its 413) doesn't matter. Before that,
    // it means the reply was lost (e.g. an RST from closing with unread body bytes): fail at once, with the reason.
    req.on('error', (error: NodeJS.ErrnoException) => {
      if (!answered) {
        reject(new Error(`the connection failed before a response arrived (${error.code ?? error.message})`))
      }
    })
    if (typeof body === 'string') {
      req.end(body)
    } else if (body) {
      const write = async () => {
        for (const chunk of body()) {
          if (req.destroyed) {
            return
          }
          if (!req.write(chunk)) {
            await new Promise<void>((resolve) => {
              const done = () => {
                req.off('drain', done)
                req.off('close', done)
                resolve()
              }
              req.on('drain', done)
              req.on('close', done)
            })
          }
        }
        req.end()
      }
      void write()
    } else if (options.headers?.['content-length']) {
      req.flushHeaders()
    } else {
      req.end()
    }
  })
}

describe('FR-98 api proxy (http)', () => {
  it('FR-98: method, path, query, body, Cookie, Authorization and X-XSRF-TOKEN pass through; status and Set-Cookie come back', async () => {
    const reply = await send(
      {
        method: 'PUT',
        path: '/api/projects/1?x=1&y=%2F',
        headers: {
          'content-type': 'application/json',
          cookie: 'aide.auth=abc; XSRF-TOKEN=x1',
          authorization: 'Bearer aide_pat_1',
          'x-xsrf-token': 'x1',
          'x-forwarded-for': '203.0.113.9',
        },
      },
      '{"name":"A"}',
    )
    const got = seen.at(-1)
    expect(got).toMatchObject({ method: 'PUT', url: '/api/projects/1?x=1&y=%2F', body: '{"name":"A"}' })
    expect(got?.headers).toMatchObject({
      cookie: 'aide.auth=abc; XSRF-TOKEN=x1',
      authorization: 'Bearer aide_pat_1',
      'x-xsrf-token': 'x1',
      'x-forwarded-for': '203.0.113.9, 127.0.0.1',
      'x-forwarded-proto': 'http',
      'x-forwarded-host': `127.0.0.1:${String(proxyPort)}`,
      host: `127.0.0.1:${String(proxyPort)}`,
    })
    expect(reply.status).toBe(201)
    expect(reply.body).toBe('{"ok":true}')
    expect(reply.headers['set-cookie']).toEqual(['aide.auth=abc; Path=/; HttpOnly; SameSite=Lax', 'XSRF-TOKEN=x1; Path=/'])
    expect(reply.headers['x-api']).toBe('1')
    expect(reply.headers['x-content-type-options']).toBe('nosniff')
  })

  it('FR-98: a declared Content-Length over 10 MB → 413 problem+json without contacting the API', async () => {
    const before = seen.length
    const reply = await send({ method: 'POST', path: '/api/imports', headers: { 'content-length': String(MAX_BODY_BYTES + 1) } })
    expect(reply.status).toBe(413)
    expect(reply.headers['content-type']).toBe('application/problem+json')
    expect(JSON.parse(reply.body)).toMatchObject({ status: 413, errorCode: 'VALIDATION' })
    expect(seen.length).toBe(before)
  })

  it('FR-98: a streamed (chunked) body over 10 MB → 413 problem+json, sent after the rest of the body is drained', async () => {
    const mb = Buffer.alloc(1024 * 1024, 0x61)
    // send() fails at once (not by timing out) if the connection resets before the reply arrives.
    const reply = await send({ method: 'POST', path: '/api/imports' }, function* () {
      for (let i = 0; i < 11; i++) {
        yield mb
      }
    })
    expect(reply.status).toBe(413)
    expect(reply.headers.connection).toBe('close')
    expect(JSON.parse(reply.body)).toMatchObject({ status: 413, errorCode: 'VALIDATION' })
  })

  it('FR-98: a streamed body up to 10 MB passes through', async () => {
    const mb = Buffer.alloc(1024 * 1024, 0x62)
    const reply = await send({ method: 'POST', path: '/api/imports' }, function* () {
      for (let i = 0; i < 2; i++) {
        yield mb
      }
    })
    expect(reply.status).toBe(201)
    expect(seen.at(-1)?.bodyBytes).toBe(2 * 1024 * 1024)
  })

  it('FR-98: a path that resolves outside /api → 400 problem+json', async () => {
    const before = seen.length
    const reply = await send({ path: '/api/../admin' })
    expect(reply.status).toBe(400)
    expect(JSON.parse(reply.body)).toMatchObject({ status: 400, errorCode: 'VALIDATION' })
    expect(seen.length).toBe(before)
  })

  it('FR-98: API down → 502 problem+json UPSTREAM_ERROR (the error callback never gets the URL)', async () => {
    const reply = await send({ path: '/api/approvals/secret-token', port: downProxyPort })
    expect(reply.status).toBe(502)
    expect(reply.headers['content-type']).toBe('application/problem+json')
    expect(JSON.parse(reply.body)).toMatchObject({ status: 502, errorCode: 'UPSTREAM_ERROR' })
    expect(upstreamErrors.length).toBeGreaterThan(0)
    expect(upstreamErrors.map((e) => e.message).join(' ')).not.toContain('secret-token')
  })
})

/** Opens an upgrade request through the proxy and resolves with the upgraded socket or the plain response. */
function upgrade(path: string, targetPort = proxyPort): Promise<{ socket: Socket; res: IncomingMessage } | { reply: Reply }> {
  return new Promise((resolve, reject) => {
    const req = request({
      host: '127.0.0.1',
      port: targetPort,
      path,
      agent: false,
      headers: {
        connection: 'Upgrade',
        upgrade: 'websocket',
        'sec-websocket-key': 'dGhlIHNhbXBsZSBub25jZQ==',
        'sec-websocket-version': '13',
        cookie: 'aide.auth=abc',
      },
    })
    req.on('upgrade', (res, socket) => {
      resolve({ socket, res })
    })
    req.on('response', (res) => {
      readReply(res).then((reply) => {
        resolve({ reply })
      }, reject)
    })
    req.on('error', reject)
    req.end()
  })
}

describe('FR-98 api proxy (WebSocket upgrades on /api/hubs/)', () => {
  it('FR-98: the upgrade is relayed (101) and bytes are piped both ways', async () => {
    const result = await upgrade('/api/hubs/projects?id=abc')
    if (!('socket' in result)) {
      throw new Error(`expected an upgrade, got ${String(result.reply.status)}`)
    }
    const { socket, res } = result
    expect(res.statusCode).toBe(101)
    expect(res.headers['sec-websocket-accept']).toBe('stub')
    expect(seen.at(-1)?.headers).toMatchObject({ upgrade: 'websocket', connection: 'Upgrade', cookie: 'aide.auth=abc' })
    expect(seen.at(-1)?.url).toBe('/api/hubs/projects?id=abc')
    socket.write('ping')
    const [data] = (await once(socket, 'data')) as [Buffer]
    expect(data.toString('utf8')).toBe('echo:ping')
    socket.destroy()
  })

  it('FR-98: a refused upgrade is relayed as a well-formed response (Content-Length, no hop-by-hop headers)', async () => {
    const result = await upgrade('/api/hubs/refuse')
    if (!('reply' in result)) {
      result.socket.destroy()
      throw new Error('expected a refusal')
    }
    const { reply } = result
    expect(reply.status).toBe(401)
    expect(reply.body).toBe('{"status":401,"errorCode":"UNAUTHENTICATED"}')
    expect(reply.headers['content-length']).toBe(String(Buffer.byteLength(reply.body)))
    expect(reply.headers).not.toHaveProperty('transfer-encoding')
    expect(reply.headers).not.toHaveProperty('keep-alive')
    expect(reply.headers.connection).toBe('close')
    expect(reply.headers['content-type']).toBe('application/problem+json')
  })

  it('FR-98: a client that leaves before the API answers aborts the upstream request (no lingering hub socket)', async () => {
    const client = connect(proxyPort, '127.0.0.1')
    await once(client, 'connect')
    client.write(
      'GET /api/hubs/slow HTTP/1.1\r\nHost: x\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n',
    )
    await expect.poll(() => slowUpstreamSocket, { timeout: 2_000 }).toBeDefined()
    client.destroy()
    // The API sees its connection closed well before it would have upgraded (300 ms), let alone the 1 h idle timeout.
    await expect.poll(() => slowUpstreamClosed, { timeout: 250 }).toBe(true)
  })

  it('FR-98: API down → the upgrade gets a 502 problem+json', async () => {
    const result = await upgrade('/api/hubs/projects', downProxyPort)
    if (!('reply' in result)) {
      result.socket.destroy()
      throw new Error('expected a 502')
    }
    expect(result.reply.status).toBe(502)
    expect(JSON.parse(result.reply.body)).toMatchObject({ errorCode: 'UPSTREAM_ERROR' })
  })
})
