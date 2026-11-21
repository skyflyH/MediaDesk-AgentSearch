import { onMounted, ref } from 'vue'
import type { DesktopApi } from '../shared/health'
import type { ViewAction } from '../shared/desktop-controls'

export function useWorkspacePreferences(api: DesktopApi, flush: () => Promise<boolean>,
  operate: (task: () => Promise<void>) => Promise<void>, saveError: Readonly<{ value: string }>) {
  const viewMessage = ref('')
  const cacheMessage = ref('')
  const cacheDirectory = ref('')
  const cacheBusy = ref(false)
  const cacheError = ref(false)

  async function loadCacheDirectory() {
    try {
      const reply = await api.getPreviewCacheDirectory()
      if ('error' in reply) throw new Error(reply.error.message)
      cacheDirectory.value = reply.result
    } catch (error) {
      cacheError.value = true
      cacheMessage.value = error instanceof Error ? error.message : '无法读取预览缓存路径。'
    }
  }

  async function chooseCacheDirectory() {
    cacheBusy.value = true
    cacheMessage.value = ''
    cacheError.value = false
    try {
      const reply = await api.choosePreviewCacheDirectory()
      if ('error' in reply) throw new Error(reply.error.message)
      if (reply.result !== null) {
        cacheDirectory.value = reply.result
        cacheMessage.value = '缓存路径已修改并保存，即刻生效。'
      }
    } catch (error) {
      cacheError.value = true
      cacheMessage.value = error instanceof Error ? error.message : '无法修改预览缓存路径。'
    } finally { cacheBusy.value = false }
  }
  async function changeView(action: ViewAction) {
    viewMessage.value = ''
    const execute = async () => {
      try {
        const reply = await api.viewAction(action)
        if ('error' in reply) throw new Error(reply.error.message)
      } catch {
        viewMessage.value = '无法执行该视图操作。'
      }
    }
    if (action === 'reload' || action === 'forceReload') {
      await operate(async () => {
        if (await flush()) await execute()
        else viewMessage.value = saveError.value
      })
    } else {
      await execute()
    }
  }

  async function clearPreviewCache() {
    cacheBusy.value = true
    cacheError.value = false
    cacheMessage.value = ''
    try {
      const reply = await api.clearPreviewCache()
      if ('error' in reply) throw new Error(reply.error.message)
      cacheMessage.value = `已清理 ${reply.result} 个预览缓存文件。`
    } catch (error) {
      cacheError.value = true
      cacheMessage.value = error instanceof Error ? error.message : '无法清理预览缓存。'
    } finally { cacheBusy.value = false }
  }

  onMounted(() => { void loadCacheDirectory() })
  return { cacheMessage, cacheDirectory, cacheBusy, cacheError, viewMessage, chooseCacheDirectory, clearPreviewCache, changeView }
}
