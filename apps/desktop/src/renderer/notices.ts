import { shallowRef } from 'vue'

export interface Notice {
  id: number
  message: string
  warning: boolean
  actionLabel?: string
  action?: () => void
}
export const notices = shallowRef<Notice[]>([])
let sequence = 0
const timers = new Map<number, ReturnType<typeof setTimeout>>()

export function dismissNotice(id: number) {
  clearTimeout(timers.get(id))
  timers.delete(id)
  notices.value = notices.value.filter(item => item.id !== id)
}

export function notify(message: string, warning = false, actionLabel?: string, action?: () => void) {
  if (!message.trim()) return
  const existing = notices.value.find(item => item.message === message)
  if (existing) dismissNotice(existing.id)
  const notice = { id: ++sequence, message, warning, actionLabel, action }
  notices.value = [...notices.value, notice]
  if (notices.value.length > 4) dismissNotice(notices.value[0]!.id)
  timers.set(notice.id, setTimeout(() => dismissNotice(notice.id), warning || action ? 6000 : 3000))
}

export function clearNotices() {
  for (const id of timers.keys()) dismissNotice(id)
}
