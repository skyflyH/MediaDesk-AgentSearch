import { afterEach, expect, it, vi } from 'vitest'
import { clearNotices, dismissNotice, notices, notify } from '../src/renderer/notices'

afterEach(() => { clearNotices(); vi.useRealTimers() })

it('expires normal feedback after three seconds and keeps actionable errors longer', () => {
  vi.useFakeTimers()
  notify('已保存')
  notify('保存失败', true, '重试', vi.fn())
  vi.advanceTimersByTime(3000)
  expect(notices.value.map(item => item.message)).toEqual(['保存失败'])
  vi.advanceTimersByTime(3000)
  expect(notices.value).toEqual([])
})

it('refreshes repeated feedback and limits visible messages without leaving timers behind', () => {
  vi.useFakeTimers()
  notify('相同消息')
  vi.advanceTimersByTime(2000)
  notify('相同消息')
  expect(notices.value).toHaveLength(1)
  vi.advanceTimersByTime(1000)
  expect(notices.value).toHaveLength(1)
  for (let index = 0; index < 5; index++) notify(`消息${index}`)
  expect(notices.value.map(item => item.message)).toEqual(['消息1', '消息2', '消息3', '消息4'])
  expect(vi.getTimerCount()).toBe(4)
  dismissNotice(notices.value[0]!.id)
  expect(vi.getTimerCount()).toBe(3)
  clearNotices()
  expect(vi.getTimerCount()).toBe(0)
})
