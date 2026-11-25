import { reactive } from 'vue'

export type NotificationTone = 'success' | 'danger' | 'warning' | 'info'

export interface NotificationItem {
  id: number
  tone: NotificationTone
  message: string
}

const items = reactive<NotificationItem[]>([])
let nextId = 1
const timers = new Map<number, ReturnType<typeof setTimeout>>()

function dismiss(id: number) {
  const index = items.findIndex((item) => item.id === id)
  if (index >= 0) items.splice(index, 1)
  const timer = timers.get(id)
  if (timer) clearTimeout(timer)
  timers.delete(id)
}

function show(tone: NotificationTone, message: string, duration = 3000) {
  const text = message.trim()
  if (!text) return
  const id = nextId++
  items.push({ id, tone, message: text })
  timers.set(id, setTimeout(() => dismiss(id), duration))
}

export function useNotifications() {
  return {
    items,
    dismiss,
    notify: {
      success: (message: string, duration?: number) => show('success', message, duration),
      danger: (message: string, duration?: number) => show('danger', message, duration),
      warning: (message: string, duration?: number) => show('warning', message, duration),
      info: (message: string, duration?: number) => show('info', message, duration),
    },
  }
}
