import { computed, onMounted, onUnmounted, ref, watch, type Ref } from 'vue'
import type { DesktopApi } from '../shared/health'
import type { Asset, AssetDraft, DraftChanges } from '../shared/workspace'
import type { ArchiveDirectory, ArchivePlan, ArchiveStatus } from '../shared/archive'
import { copyAssets } from './copyAssets'

export interface PreviewSession {
  closePreview: () => Promise<void>
  reopen: () => Promise<void>
}

interface ArchiveDependencies {
  asset: Readonly<Ref<Asset | undefined>>
  draft: Readonly<Ref<AssetDraft | null>>
  archiveLog: Ref<ArchiveStatus[]>
  saveState: Readonly<Ref<string>>
  saveError: Readonly<Ref<string>>
  disabled: Readonly<Ref<boolean>>
  flush: () => Promise<boolean>
  operate: (task: () => Promise<void>) => Promise<void>
  edit: (changes: DraftChanges) => void
  reload: () => Promise<void>
  refreshDraft: (id: string) => Promise<void>
  getPreview: () => PreviewSession | undefined
  directoryDraftId: () => string | undefined
  showSaveError: (message: string) => Promise<void>
  showSaveSuccess: (message?: string) => Promise<void>
}

interface ArchiveBatch {
  batchItems: Readonly<Ref<Asset[]>>
  batchIds: Ref<string[]>
  batchDrafts: Ref<Map<string, AssetDraft>>
  batchMessage: Ref<string>
  batchBlocked: Ref<boolean>
  batchErrors: Ref<Map<string, string>>
  rememberBatchDraft: (draft: AssetDraft) => void
}

export function useArchiveOperations(api: DesktopApi, dependencies: ArchiveDependencies, batch: ArchiveBatch) {
  const { asset, draft, archiveLog, saveState, saveError, disabled, flush, operate, edit, reload,
    refreshDraft, getPreview, directoryDraftId, showSaveError, showSaveSuccess } = dependencies
  const { batchItems, batchIds, batchDrafts, batchMessage, batchBlocked, batchErrors, rememberBatchDraft } = batch
  let disposed = false
  const copyingBatch = ref(false)
  const reportedCopyErrors = new Set<string>()
  const pendingSaveNotices = new Set<string>()
  const directories = ref<ArchiveDirectory[]>([])
  const archivePlan = ref<(ArchivePlan & { operationId: string; assetId: string; revision: number }) | null>(null)
  const archiveError = ref('')
  let archiveTimer: ReturnType<typeof setTimeout> | undefined
  let archivePolling = false
  const activeArchive = computed(() => archiveLog.value.find((entry) =>
    entry.assetId === asset.value?.assetId && entry.phase !== 'complete' && entry.phase !== 'failed'))
  const currentPlan = computed(() => archivePlan.value?.assetId === draft.value?.assetId &&
    archivePlan.value?.revision === draft.value?.archiveRevision && saveState.value === 'saved' ? archivePlan.value : null)
  async function refreshDirectories() {
    const reply = await api.listDirectories()
    if ('error' in reply) archiveError.value = reply.error.message
    else if (!disposed) directories.value = reply.result
  }

  function selectDirectory(event: Event) {
    edit({ directoryId: (event.target as HTMLSelectElement).value || null })
    archivePlan.value = null
  }

  function copyError(message: string, copied: string[] = []) {
    archiveError.value = copied.length ? `${message}\n已复制的文件：\n${copied.join('\n')}` : message
  }

  async function copyBatch(directoryId: string, changes: DraftChanges, names: Record<string, string> = {}) {
    if (disabled.value || !batchItems.value.length) return
    const selected = [...batchItems.value]
    copyingBatch.value = true
    batchMessage.value = '正在逐项复制…'
    batchBlocked.value = false
    batchErrors.value = new Map()
    archiveError.value = ''
    try {
      await operate(async () => {
        if (!await flush()) {
          batchBlocked.value = true
          batchMessage.value = '当前草稿未保存，复制尚未开始。请先重试保存草稿。'
          return
        }
        if (selected.some((item) => item.assetId === draft.value?.assetId)) await getPreview()?.closePreview()
        if (Object.keys(names).length) {
          for (const item of selected) {
            if (names[item.assetId] === undefined) continue
            try {
              const current = await api.getDraft(item.assetId)
              if ('error' in current) throw Error(current.error.message)
              const saved = await api.saveDraft({ assetId: item.assetId, expectedRevision: current.result.revision,
                changes: { name: names[item.assetId] } })
              if ('error' in saved) throw Error(saved.error.message)
              rememberBatchDraft(saved.result)
              if (draft.value?.assetId === item.assetId) await refreshDraft(item.assetId)
            } catch (error) {
              const message = error instanceof Error ? error.message : '名称保存失败。'
              batchBlocked.value = true
              batchErrors.value.set(item.assetId, message)
              batchMessage.value = message
              return
            }
          }
        }
        const outcome = await copyAssets(api, selected.map((item) => item.assetId), directoryId, changes, updateArchive)
        const copiedIds = new Set(outcome.copied.map((item) => item.assetId))
        batchIds.value = batchIds.value.filter((id) => !copiedIds.has(id))
        for (const id of copiedIds) batchDrafts.value.delete(id)
        batchBlocked.value = !!outcome.failure
        batchMessage.value = outcome.failure
          ? `复制已停止，已复制 ${outcome.copied.length} 项，后续素材未执行。`
          : `已复制 ${outcome.copied.length} 项到保存目录。`
        if (outcome.failure) {
          batchErrors.value = new Map([[outcome.failure.assetId, outcome.failure.message]])
          const name = selected.find((item) => item.assetId === outcome.failure?.assetId)?.originalName ?? ''
          copyError(`${name}\n${outcome.failure.message}`, outcome.copied.map((item) => item.path))
        } else void showSaveSuccess(`保存成功，已保存 ${outcome.copied.length} 项到所选目录。`)
      })
      await reload()
    } catch (error) {
      batchBlocked.value = true
      batchMessage.value = error instanceof Error ? error.message : '批量保存失败。'
      archiveError.value = batchMessage.value
    } finally {
      copyingBatch.value = false
      scheduleArchivePoll()
    }
  }

  async function addDirectory(root: boolean) {
    const draftId = directoryDraftId()
    archiveError.value = ''
    const reply = await api.chooseDirectory(root).catch(() => ({
      error: { message: '无法选择目录，请重试。' }
    }))
    if ('error' in reply) {
      archiveError.value = reply.error.message
      return
    }
    if (!reply.result) return
    await refreshDirectories()
    if (draftId && draft.value?.assetId === draftId && !activeArchive.value) {
      edit({ directoryId: reply.result.directoryId })
      archivePlan.value = null
    }
  }

  const saveAs = () => operate(async () => {
    archiveError.value = ''
    if (!await flush()) {
      void showSaveError(saveError.value || '草稿保存失败，请重试。')
      return
    }
    const current = draft.value
    if (!current?.data.directoryId) return
    let plan = currentPlan.value
    const player = getPreview()
    try {
      if (!plan) {
        const reply = await api.archivePreview(current.assetId, current.archiveRevision, current.data.directoryId)
        if ('error' in reply) {
          copyError(reply.error.message)
          return
        }
        plan = { ...reply.result, operationId: crypto.randomUUID(), assetId: current.assetId, revision: current.archiveRevision }
        archivePlan.value = plan
      }
      await player?.closePreview()
      const reply = await api.archiveCommit(plan.operationId, plan.planToken)
      if ('error' in reply) {
        if (!reply.error.retryable) archivePlan.value = null
        copyError(reply.error.message)
        void player?.reopen()
        return
      }
      archivePlan.value = null
      pendingSaveNotices.add(reply.result.operationId)
      updateArchive(reply.result)
    } catch {
      archiveError.value = '暂未取得保存结果。请再次点击「保存」以核对操作状态。'
      void player?.reopen()
    }
  })

  function updateArchive(status: ArchiveStatus) {
    archiveLog.value = [status, ...archiveLog.value.filter((entry) => entry.operationId !== status.operationId)]
    if (pendingSaveNotices.has(status.operationId)) {
      if (status.phase === 'complete' && !status.errorCode) {
        pendingSaveNotices.delete(status.operationId)
        void showSaveSuccess()
      } else if (status.errorCode || status.phase === 'failed' || status.phase === 'needs_review') {
        pendingSaveNotices.delete(status.operationId)
      }
    }
    if (!copyingBatch.value && status.errorCode && !reportedCopyErrors.has(status.operationId)) {
      reportedCopyErrors.add(status.operationId)
      copyError(status.message || `复制失败：${status.finalPath}`, status.actualPath ? [status.actualPath] : [])
    }
  }

  function inProgress(item: ArchiveStatus) {
    return item.phase !== 'complete' && item.phase !== 'failed' && item.phase !== 'needs_review' &&
      !(item.phase === 'properties_pending' && item.errorCode)
  }

  function scheduleArchivePoll() {
    clearTimeout(archiveTimer)
    if (!disposed && !copyingBatch.value && !archivePolling && document.visibilityState === 'visible' && archiveLog.value.some(inProgress))
      archiveTimer = setTimeout(() => { void pollArchives() }, 450)
  }

  async function pollArchives() {
    if (disposed || archivePolling || document.visibilityState !== 'visible') return
    archivePolling = true
    let advanced = false
    try {
      for (const entry of archiveLog.value.filter(inProgress)) {
        if (disposed || document.visibilityState !== 'visible') break
        const result = await api.archiveStatus(entry.operationId)
        if (disposed) return
        if ('error' in result) {
          archiveError.value = result.error.message
          continue
        }
        updateArchive(result.result)
        if (result.result.actualPath && (asset.value?.assetId === entry.assetId || !entry.actualPath)) advanced = true
      }
      if (advanced && !disposed) await reload()
    } catch {
      if (!disposed) archiveError.value = '无法刷新归档状态。'
    } finally {
      archivePolling = false
      scheduleArchivePoll()
    }
  }

  watch(archiveLog, scheduleArchivePoll)
  onMounted(() => {
    void refreshDirectories()
    document.addEventListener('visibilitychange', scheduleArchivePoll)
  })
  onUnmounted(() => {
    disposed = true
    clearTimeout(archiveTimer)
    document.removeEventListener('visibilitychange', scheduleArchivePoll)
  })
  return { directories, archiveError, activeArchive, copyingBatch, selectDirectory, addDirectory, saveAs, copyBatch,
    invalidatePlan: () => { archivePlan.value = null } }
}
