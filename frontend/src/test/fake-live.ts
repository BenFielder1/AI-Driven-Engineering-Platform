import { act } from '@testing-library/react'

import type { LiveConnection, LiveConnectionFactory, ProjectEventMessage } from '../features/live/live-connection'

type Handler<T extends unknown[] = []> = (...args: T) => void

/** One fake hub connection: records Join/Leave calls and lets the test push events and reconnects. */
export class FakeLiveConnection implements LiveConnection {
  readonly calls: { method: 'Join' | 'Leave'; projectId: string }[] = []
  started = false
  stopped = false
  /** Make `Join` fail (e.g. FORBIDDEN for a non-member). */
  failJoin = false
  private eventHandlers: Handler<[ProjectEventMessage]>[] = []
  private reconnectingHandlers: Handler[] = []
  private reconnectedHandlers: Handler[] = []
  private closeHandlers: Handler[] = []

  start() {
    this.started = true
    return Promise.resolve()
  }

  stop() {
    this.stopped = true
    return Promise.resolve()
  }

  invoke(method: 'Join' | 'Leave', projectId: string) {
    this.calls.push({ method, projectId })
    return method === 'Join' && this.failJoin ? Promise.reject(new Error('FORBIDDEN')) : Promise.resolve()
  }

  onProjectEvent(handler: Handler<[ProjectEventMessage]>) {
    this.eventHandlers.push(handler)
  }

  onReconnecting(handler: Handler) {
    this.reconnectingHandlers.push(handler)
  }

  onReconnected(handler: Handler) {
    this.reconnectedHandlers.push(handler)
  }

  onClose(handler: Handler) {
    this.closeHandlers.push(handler)
  }

  joined(projectId: string) {
    return this.calls.filter((c) => c.projectId === projectId && c.method === 'Join').length
  }

  emit(message: ProjectEventMessage) {
    act(() => {
      for (const h of this.eventHandlers) {
        h(message)
      }
    })
  }

  /** The connection closed for good (automatic reconnect gave up). */
  close() {
    act(() => {
      for (const h of this.closeHandlers) {
        h()
      }
    })
  }

  dropAndReconnect() {
    act(() => {
      for (const h of this.reconnectingHandlers) {
        h()
      }
    })
    act(() => {
      for (const h of this.reconnectedHandlers) {
        h()
      }
    })
  }
}

let eventSeq = 0

/** A `projectEvent` message as the hub sends it. */
export function projectEvent(
  projectId: string,
  category: ProjectEventMessage['category'],
  overrides: Partial<ProjectEventMessage> = {},
): ProjectEventMessage {
  eventSeq += 1
  return {
    id: `00000000-0000-7000-8000-${String(eventSeq).padStart(12, '0')}`,
    projectId,
    type: 'EVENT',
    category,
    payload: {},
    createdAt: new Date().toISOString(),
    ...overrides,
  }
}

/** A fake hub: every connection the app opens is recorded; `latest` is the one in use. */
export class FakeLiveHub {
  readonly connections: FakeLiveConnection[] = []
  /** New connections fail `Join` (e.g. FORBIDDEN). */
  failJoins = false

  readonly factory: LiveConnectionFactory = () => {
    const connection = new FakeLiveConnection()
    connection.failJoin = this.failJoins
    this.connections.push(connection)
    return connection
  }

  get latest(): FakeLiveConnection {
    const connection = this.connections.at(-1)
    if (!connection) {
      throw new Error('No live connection was opened')
    }
    return connection
  }
}
