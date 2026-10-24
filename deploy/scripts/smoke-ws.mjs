// FR-111 helper for smoke-remote.sh: opens the SignalR WebSocket at <base>/api/hubs/projects through the web server
// with the signed-in session cookie, completes the SignalR JSON handshake, then closes. Node 24 (global WebSocket,
// which accepts a `headers` init option). Inputs come from the environment so nothing secret appears in argv:
//   SMOKE_WS_BASE_URL  the web base URL (https://… or http://…)
//   SMOKE_WS_COOKIE    the Cookie header value (never printed)
// Exit 0 when the hub answers the handshake without an error, 1 otherwise (reason on stderr).
const RS = '\u001e' // SignalR record separator
const base = process.env.SMOKE_WS_BASE_URL ?? ''
const cookie = process.env.SMOKE_WS_COOKIE ?? ''
const timeoutMs = Number(process.env.SMOKE_WS_TIMEOUT_MS ?? '15000')

function fail(reason) {
  process.stderr.write(`${reason}\n`)
  process.exit(1)
}

let origin
let url
try {
  const parsed = new URL(base)
  origin = parsed.origin
  url = new URL('/api/hubs/projects', parsed)
  url.protocol = parsed.protocol === 'https:' ? 'wss:' : 'ws:'
} catch {
  fail('SMOKE_WS_BASE_URL is not a valid URL')
}
if (!cookie) fail('SMOKE_WS_COOKIE is empty (not signed in?)')

// Origin must be the web origin: the API's Live origin check refuses cookie WebSockets without a matching Origin.
const ws = new WebSocket(url, { headers: { Cookie: cookie, Origin: origin } })
let connected = false
const timer = setTimeout(() => fail(`no SignalR handshake response within ${timeoutMs} ms`), timeoutMs)

ws.addEventListener('open', () => {
  ws.send(`${JSON.stringify({ protocol: 'json', version: 1 })}${RS}`)
})
ws.addEventListener('message', (event) => {
  const text = typeof event.data === 'string' ? event.data : ''
  const first = text.split(RS)[0] ?? ''
  let handshake
  try {
    handshake = JSON.parse(first)
  } catch {
    fail('unexpected first frame from the hub (not a SignalR handshake response)')
  }
  clearTimeout(timer)
  if (handshake && typeof handshake.error === 'string') fail(`hub refused the handshake: ${handshake.error}`)
  connected = true
  process.stdout.write('connected\n')
  ws.close(1000)
})
ws.addEventListener('error', () => {
  // Upgrade refused (401/403 from the API, 502 from the proxy) or network error; details aren't exposed by the API.
  fail('WebSocket upgrade failed (refused, unauthenticated or unreachable)')
})
ws.addEventListener('close', (event) => {
  clearTimeout(timer)
  if (!connected) fail(`hub closed the connection before the handshake (code ${event.code})`)
  process.exit(0)
})
