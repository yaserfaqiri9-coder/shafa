import { onBeforeUnmount, onMounted, ref } from 'vue'
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'

/** The payload is the event's identifiers as JSON text, never patient data. */
type Listener = (eventType: string, payload?: string) => void

const RECONNECTED = '*'
const RESTART_DELAY_MS = 5_000
const SAFETY_REFRESH_MS = 60_000

const listeners = new Set<Listener>()
let connection: HubConnection | null = null
let restartTimer: ReturnType<typeof setTimeout> | undefined

export const liveConnected = ref(false)

function notify(type: string, payload?: string) {
  for (const listener of listeners) listener(type, payload)
}

async function start(current: HubConnection) {
  if (connection !== current) return
  try {
    await current.start()
    if (connection !== current) return
    liveConnected.value = true
    // Anything missed while disconnected is read back from the database.
    notify(RECONNECTED)
  } catch {
    if (connection !== current) return
    liveConnected.value = false
    scheduleRestart(current)
  }
}

function scheduleRestart(current: HubConnection) {
  if (connection !== current) return
  clearTimeout(restartTimer)
  restartTimer = setTimeout(() => start(current), RESTART_DELAY_MS)
}

export function connectRealtime() {
  if (connection) return
  connection = new HubConnectionBuilder()
    .withUrl('/hubs/events')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.None)
    .build()

  const current = connection
  connection.on('event', (event: { type: string; payload?: string }) => {
    if (connection === current) notify(event.type, event.payload)
  })
  connection.on('chatMessage', (message: { senderUserId: number; recipientUserId: number }) =>
    connection === current && notify('chatMessage', JSON.stringify(message)),
  )
  connection.onreconnecting(() => {
    if (connection === current) liveConnected.value = false
  })
  connection.onreconnected(() => {
    if (connection !== current) return
    liveConnected.value = true
    notify(RECONNECTED)
  })
  connection.onclose(() => {
    if (connection !== current) return
    liveConnected.value = false
    scheduleRestart(current)
  })
  void start(current)
}

export function disconnectRealtime() {
  clearTimeout(restartTimer)
  const current = connection
  connection = null
  liveConnected.value = false
  if (current && current.state !== HubConnectionState.Disconnected) void current.stop().catch(() => {})
}

/**
 * Reloads from the API when one of the events arrives; events only signal, the API stays the source of truth.
 * accept narrows to the events a page cares about by their identifiers (one visit, say).
 * refreshMs 0 turns off the timed safety refresh, for a read that is costly or audited each time.
 */
export function useLiveRefresh(eventTypes: string[], load: () => unknown, refreshMs = SAFETY_REFRESH_MS, accept?: (type: string, ids: Record<string, unknown>) => boolean) {
  let debounce: ReturnType<typeof setTimeout> | undefined
  let timer: ReturnType<typeof setInterval> | undefined
  let running = false
  let again = false
  let disposed = false
  let hiddenChange = false

  // One load at a time: a burst of events during a load causes one more load after it, never an overlapping one
  // whose older answer could land last and show stale rows.
  async function run() {
    if (disposed) return
    if (document.hidden) {
      hiddenChange = true
      return
    }
    if (running) {
      again = true
      return
    }
    running = true
    try {
      await load()
    } catch {
      // The page shows its own error.
    } finally {
      running = false
    }
    if (again && !disposed) {
      again = false
      run()
    }
  }

  const listener: Listener = (type, payload) => {
    if (type !== RECONNECTED && (!eventTypes.includes(type) || (accept && !accept(type, parseIds(payload))))) return
    if (document.hidden) {
      hiddenChange = true
      return
    }
    clearTimeout(debounce)
    debounce = setTimeout(run, 200)
  }

  // A hidden tab skips the safety refresh and catches up as soon as it is shown again.
  const onVisible = () => {
    if (!document.hidden && (hiddenChange || refreshMs > 0)) {
      hiddenChange = false
      void run()
    }
  }

  onMounted(() => {
    listeners.add(listener)
    document.addEventListener('visibilitychange', onVisible)
    if (refreshMs <= 0) return
    // Safety net: a lost event can delay a screen but never leave it stale for long.
    timer = setInterval(() => {
      if (!document.hidden) run()
    }, refreshMs)
  })

  onBeforeUnmount(() => {
    disposed = true
    again = false
    listeners.delete(listener)
    clearTimeout(debounce)
    clearInterval(timer)
    document.removeEventListener('visibilitychange', onVisible)
  })
}

function parseIds(payload?: string): Record<string, unknown> {
  try {
    const parsed: unknown = payload ? JSON.parse(payload) : null
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, unknown>) : {}
  } catch {
    return {}
  }
}
