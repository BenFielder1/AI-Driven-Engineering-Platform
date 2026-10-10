import {
  DefaultHttpClient,
  HttpTransportType,
  HubConnectionBuilder,
  LogLevel,
  NullLogger,
  type HttpRequest,
  type HttpResponse,
  type ILogger,
} from '@microsoft/signalr'
import { createContext } from 'react'

import { readXsrfToken, refreshAntiforgeryToken, XSRF_HEADER, type Schemas } from '../../api/client'

export type ProjectEventMessage = Schemas['ProjectEventMessage']

/** §6 `WS /api/hubs/projects`: same origin, so the Vite dev proxy / nginx forward it (WebSocket upgrade). */
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

/**
 * Adds the antiforgery header (NFR-SEC) to SignalR's own HTTP requests (negotiate, and the long-polling/SSE fallbacks),
 * read per request so a refreshed `XSRF-TOKEN` cookie is picked up. Browsers can't set headers on the WebSocket
 * upgrade itself; it's authenticated by the cookie and the negotiated connection token.
 */
class AntiforgeryHttpClient extends DefaultHttpClient {
  override async send(request: HttpRequest): Promise<HttpResponse> {
    if (!readXsrfToken()) {
      await refreshAntiforgeryToken()
    }
    const token = readXsrfToken()
    return super.send(token ? { ...request, headers: { ...request.headers, [XSRF_HEADER]: token } } : request)
  }
}

/** The production factory: a SignalR connection to the same-origin projects hub, reconnecting automatically. */
export const createSignalRConnection: LiveConnectionFactory = () => {
  const logger: ILogger = import.meta.env.DEV ? { log: (level, message) => {
    if (level >= LogLevel.Warning) {
      console.warn(`[live] ${message}`)
    }
  } } : NullLogger.instance
  const connection = new HubConnectionBuilder()
    .withUrl(PROJECTS_HUB_PATH, {
      httpClient: new AntiforgeryHttpClient(logger),
      transport: HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
      withCredentials: true,
    })
    .withAutomaticReconnect()
    .configureLogging(logger)
    .build()

  return {
    start: () => connection.start(),
    stop: () => connection.stop(),
    invoke: (method, projectId) => connection.invoke(method, projectId),
    onProjectEvent: (handler) => {
      connection.on('projectEvent', handler)
    },
    onReconnecting: (handler) => {
      connection.onreconnecting(() => {
        handler()
      })
    },
    onReconnected: (handler) => {
      connection.onreconnected(() => {
        handler()
      })
    },
    onClose: (handler) => {
      connection.onclose(() => {
        handler()
      })
    },
  }
}

/** Which factory `useProjectLive` uses: SignalR in the app, a fake in tests. */
export const LiveConnectionContext = createContext<LiveConnectionFactory>(createSignalRConnection)
