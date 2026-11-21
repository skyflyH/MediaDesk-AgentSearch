import { computed, onMounted, onUnmounted, ref, shallowRef } from 'vue'
import type { DesktopApi, Result } from '../shared/health'
import type { Asset, AssetDraft, DraftChanges, ImportItem, SavedSourceProperties } from '../shared/workspace'
import type { ArchiveStatus } from '../shared/archive'

export function useWorkspace(api: DesktopApi, beforeExit: () => void) {
  const items = shallowRef<Asset[]>([])
  const importFolders = shallowRef<string[]>([])
  const draft = shallowRef<AssetDraft | null>(null)
  const pending = shallowRef<DraftChanges>({})
  const importResults = shallowRef<ImportItem[]>([])
  const archiveLog = shallowRef<ArchiveStatus[]>([])
  const saveError = ref('')
  const workspaceError = ref('')
  const loading = ref(true)
  const importCount = ref(0)
  const importing = computed(() => importCount.value > 0)
  const busy = ref(false)
  const exiting = ref(false)
  const saving = ref(false)
  let timer: ReturnType<typeof setTimeout> | undefined
  let inFlight: Promise<boolean> | undefined
  let action: Promise<void> | undefined
  let unsubscribe: Array<() => void> = []

  const asset = computed(() => items.value.find((item) => item.assetId === draft.value?.assetId))
  const edited = computed(() => draft.value && { ...draft.value.data, ...pending.value })
  const dirty = () => Object.keys(pending.value).length > 0
  const saveState = computed(() => saveError.value ? 'error' : saving.value || dirty() ? 'saving' : 'saved')

  function cancelTimer() {
    clearTimeout(timer)
    timer = undefined
  }

  function edit(changes: DraftChanges) {
    if (!draft.value || busy.value || exiting.value) return
    pending.value = { ...pending.value, ...changes }
    cancelTimer()
    timer = setTimeout(() => { void flush() }, 400)
  }

  function failure(error: unknown): string {
    return error instanceof Error ? error.message : '本地服务暂不可用，请重试。'
  }

  async function savePending(allowConflictRetry: boolean): Promise<boolean> {
    const current = draft.value
    if (!current || !dirty()) return true
    const changes = pending.value
    try {
      let reply = await api.saveDraft({ assetId: current.assetId, expectedRevision: current.revision, changes })
      if ('error' in reply && reply.error.code === 'REVISION_CONFLICT') {
        const latest = await api.getDraft(current.assetId)
        if ('error' in latest) throw new Error(latest.error.message)
        const { suggestions: _previous, ...originalData } = current.data
        const { suggestions: _latest, ...latestData } = latest.result.data
        if (!allowConflictRetry && JSON.stringify(originalData) !== JSON.stringify(latestData))
          throw new Error(reply.error.message)
        draft.value = latest.result
        reply = await api.saveDraft({ assetId: current.assetId, expectedRevision: latest.result.revision, changes })
      }
      if ('error' in reply) throw new Error(reply.error.message)
      draft.value = reply.result
      const remaining = { ...pending.value }
      for (const key of Object.keys(changes) as Array<keyof DraftChanges>) {
        if (remaining[key] === changes[key]) delete remaining[key]
      }
      pending.value = remaining
      saveError.value = ''
      return true
    } catch (error) {
      saveError.value = failure(error)
      return false
    }
  }

  async function flush(allowConflictRetry = false): Promise<boolean> {
    cancelTimer()
    while (dirty()) {
      if (!inFlight) {
        saving.value = true
        inFlight = savePending(allowConflictRetry).finally(() => {
          inFlight = undefined
          saving.value = false
        })
      }
      if (!await inFlight) return false
    }
    return true
  }

  async function load() {
    const reply = await api.loadWorkspace()
    if ('error' in reply) throw new Error(reply.error.message)
    items.value = reply.result.items
    importFolders.value = reply.result.importFolders
    archiveLog.value = [...reply.result.incompleteOperations, ...reply.result.recentArchives]
    draft.value = reply.result.draft
    if (!draft.value && items.value[0]) await setCurrent(items.value[0].assetId)
  }

  async function setCurrent(id: string) {
    const reply = await api.setCurrent(id)
    if ('error' in reply) throw new Error(reply.error.message)
    draft.value = reply.result
    saveError.value = ''
  }

  async function operate(task: () => Promise<void>) {
    if (busy.value || exiting.value) return
    busy.value = true
    workspaceError.value = ''
    action = task().catch((error) => { workspaceError.value = failure(error) })
    try { await action } finally {
      busy.value = false
      loading.value = false
      action = undefined
    }
  }

  const reload = async () => {
    while (action) await action
    await operate(async () => { if (await flush()) await load() })
  }
  const removeFromQueue = (remove: () => Promise<Result<number>>) => operate(async () => {
    if (!await flush()) return
    const reply = await remove()
    if ('error' in reply) throw new Error(reply.error.message)
    await load()
  })
  const removeQueueAssets = (ids: string[]) => removeFromQueue(() => api.removeQueueAssets(ids))
  const removeQueueFolder = (root: string, path: string) => removeFromQueue(() => api.removeQueueFolder(root, path))
  async function refreshDraft(id: string) {
    await inFlight
    if (draft.value?.assetId !== id) return
    const reply = await api.getDraft(id)
    if ('result' in reply && draft.value?.assetId === id && reply.result.revision >= draft.value.revision) {
      draft.value = reply.result
    }
  }
  async function writeSourceProperties() {
    const current = draft.value
    if (!current) return
    const reply = await api.saveSourceProperties(current.assetId, current.revision)
    if ('error' in reply) throw new Error(reply.error.message)
    const result = reply.result
    updateSourceProperties(current.assetId, result)
    return result
  }

  function updateSourceProperties(id: string, result: SavedSourceProperties) {
    if (draft.value?.assetId === id && result.draft.revision >= draft.value.revision)
      draft.value = result.draft
    const path = result.renamedPath
    if (path) {
      items.value = items.value.map((item) => item.assetId === id ? {
        ...item, sourcePath: path, originalName: path.split(/[\\/]/).at(-1) ?? item.originalName
      } : item)
    }
  }
  const select = (id: string) => operate(async () => {
    if (draft.value?.assetId !== id && await flush()) await setCurrent(id)
  })

  async function handleImport(reply: Result<ImportItem[]>) {
    if ('error' in reply) throw new Error(reply.error.message)
    if (!reply.result.length) return
    importResults.value = reply.result
    // Preserve the editor and its revision even if a background drop returns while a save is failing.
    const workspace = await api.loadWorkspace()
    if ('error' in workspace) throw new Error(workspace.error.message)
    items.value = workspace.result.items
    importFolders.value = workspace.result.importFolders
    if (!draft.value && items.value[0]) await setCurrent(items.value[0].assetId)
  }

  async function trackImport(task: () => Promise<void>) {
    importCount.value++
    try {
      await task()
    } finally {
      importCount.value--
    }
  }

  const choose = (mode?: 'folder') => operate(async () => {
    const selection = await api.chooseAssetPaths(mode)
    if ('error' in selection) throw new Error(selection.error.message)
    if (selection.result.length) {
      await trackImport(async () => handleImport(await api.importAssets(selection.result)))
    }
  })

  async function handleDrop(paths: string[]) {
    await trackImport(async () => {
      const reply = await api.importAssets(paths).catch(() => ({
        error: { code: 'IMPORT_UNAVAILABLE', message: '无法导入文件，请重试。', retryable: true as const }
      }))
      while (action) await action
      await operate(() => handleImport(reply))
    })
  }

  async function saveBatch(ids: string[], changes: DraftChanges | ((id: string) => DraftChanges),
    options: { writeSourceProperties?: boolean } = {}) {
    let results: Array<{ id: string; error?: string; draft?: AssetDraft }> | undefined
    await operate(async () => {
      if (!await flush()) return
      results = []
      for (const id of ids) {
        let savedDraft: AssetDraft | undefined
        try {
          const current = await api.getDraft(id)
          if ('error' in current) throw new Error(current.error.message)
          const saved = await api.saveDraft({ assetId: id, expectedRevision: current.result.revision,
            changes: typeof changes === 'function' ? changes(id) : changes })
          if ('error' in saved) throw new Error(saved.error.message)
          savedDraft = saved.result
          if (draft.value?.assetId === id && saved.result.revision >= draft.value.revision)
            draft.value = saved.result
          if (options.writeSourceProperties) {
            const written = await api.saveSourceProperties(id, savedDraft.revision)
            if ('error' in written) throw new Error(written.error.message)
            savedDraft = written.result.draft
            updateSourceProperties(id, written.result)
            if (written.result.remark === 'write_failed' || written.result.remark === 'library_only')
              throw new Error(written.result.remarkFailure || '无法将备注写入原文件，草稿已保留。')
          }
          results.push({ id, draft: savedDraft })
        } catch (error) {
          results.push({ id, draft: savedDraft, error: failure(error) })
        }
      }
    })
    return results
  }

  async function prepareExit() {
    beforeExit()
    exiting.value = true
    await action
    const ready = await flush()
    if (!ready) exiting.value = false
    return ready
  }

  onMounted(() => {
    unsubscribe = [
      api.onAssetsDropped((paths) => { void handleDrop(paths) }),
      api.onExitRequested(prepareExit),
      api.onExitCancelled(() => { exiting.value = false })
    ]
    void reload()
  })
  onUnmounted(() => {
    cancelTimer()
    unsubscribe.forEach((remove) => remove())
  })

  return {
    items, importFolders, asset, draft, edited, importResults, archiveLog, loading, importing, busy, exiting, saveError, workspaceError, saveState,
    reload, removeQueueAssets, removeQueueFolder, select, choose, edit, flush, operate, refreshDraft, writeSourceProperties, saveBatch, retrySave: () => flush(true)
  }
}
