import type * as SignalRModule from '@microsoft/signalr'
import type { HttpRequest, HttpResponse, HubConnection, ILogger } from '@microsoft/signalr'
import { createContext } from 'react'

import { readXsrfToken, refreshAntiforgeryToken, XSRF_HEADER, type Schemas } from '../../api/client'

export type ProjectEventMessage = Schemas['ProjectEventMessage']

/** §6 `WS /api/hubs/projects`: same origin, so the web server (server.ts) forwards it (WebSocket upgrade, FR-98). */
export const PROJECTS_HUB_PATH = '/api/hubs/projects'

/**
 * The slice of a SignalR hub connection the live-updates hook uses (FR-46). Tests inject a fake through
 * `LiveConnectionContext`, so no test opens a real socket.
 */
export interface LiveConnection {
  start(): Promise<void>
  stop(): Promise<void>
  /** Hub methods: `Join(projectId)` (FORBIDDEN hub error for non-members) and `Leave(projectId)`. */
  invoke(method: 'Join' | 'Leave', projectId: string): Promise<void>
  onProjectEvent(handler: (message: ProjectEventMessage) => void): void
  /** Automatic reconnect lost the connection and is retrying. */
  onReconnecting(handler: () => void): void
  /** Automatic reconnect succeeded: the server forgot our groups, so Join again. */
  onReconnected(handler: () => void): void
  /** The connection closed for good (reconnect gave up, or `stop`). */
  onClose(handler: () => void): void
}

export type LiveConnectionFactory = () => LiveConnection

type SignalR = typeof SignalRModule

/**
 * Adds the antiforgery header (NFR-SEC) to SignalR's own HTTP requests (negotiate, and the long-polling/SSE fallbacks),
 * read per request so a refreshed `XSRF-TOKEN` cookie is picked up. Browsers can't set headers on the WebSocket
 * upgrade itself; it's authenticated by the cookie and the negotiated connection token.
 */
function antiforgeryHttpClient(signalr: SignalR, logger: ILogger) {
  class AntiforgeryHttpClient extends signalr.DefaultHttpClient {
    override async send(request: HttpRequest): Promise<HttpResponse> {
      if (!readXsrfToken()) {
        await refreshAntiforgeryToken()
      }
      const token = readXsrfToken()
      return super.send(token ? { ...request, headers: { ...request.headers, [XSRF_HEADER]: token } } : request)
    }
  }
  return new AntiforgeryHttpClient(logger)
}

function buildConnection(signalr: SignalR): HubConnection {
  const logger: ILogger =
    process.env.NODE_ENV === 'development'
      ? {
          log: (level, message) => {
            if (level >= signalr.LogLevel.Warning) {
              console.warn(`[live] ${message}`)
            }
          },
        }
      : signalr.NullLogger.instance
  const { HttpTransportType } = signalr
  return new signalr.HubConnectionBuilder()
    .withUrl(PROJECTS_HUB_PATH, {
      httpClient: antiforgeryHttpClient(signalr, logger),
      transport: HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
      withCredentials: true,
    })
    .withAutomaticReconnect()
    .configureLogging(logger)
    .build()
}

/**
 * The production factory: a SignalR connection to the same-origin projects hub, reconnecting automatically.
 * `@microsoft/signalr` is imported on first use (SPEC §8 bundle budget: only project pages need it), so handlers
 * registered before that are applied once the connection exists.
 */
export const createSignalRConnection: LiveConnectionFactory = () => {
  let connection: HubConnection | undefined
  let building: Promise<HubConnection> | undefined
  const pending: ((c: HubConnection) => void)[] = []
  const ready = () =>
    (building ??= import('@microsoft/signalr').then((signalr) => {
      const built = buildConnection(signalr)
      connection = built
      for (const apply of pending.splice(0)) {
        apply(built)
      }
      return built
    }))
  const register = (apply: (c: HubConnection) => void) => {
    if (connection) {
      apply(connection)
    } else {
      pending.push(apply)
    }
  }

  return {
    start: async () => {
      await (await ready()).start()
    },
    stop: async () => {
      if (building) {
        await (await building).stop()
      }
    },
    invoke: async (method, projectId) => {
      await (await ready()).invoke(method, projectId)
    },
    onProjectEvent: (handler) => {
      register((c) => {
        c.on('projectEvent', handler)
      })
    },
    onReconnecting: (handler) => {
      register((c) => {
        c.onreconnecting(() => {
          handler()
        })
      })
    },
    onReconnected: (handler) => {
      register((c) => {
        c.onreconnected(() => {
          handler()
        })
      })
    },
    onClose: (handler) => {
      register((c) => {
        c.onclose(() => {
          handler()
        })
      })
    },
  }
}

/** Which factory `useProjectLive` uses: SignalR in the app, a fake in tests. */
export const LiveConnectionContext = createContext<LiveConnectionFactory>(createSignalRConnection)
