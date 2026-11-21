import { computed, onMounted, onUnmounted, ref, watch, type Ref } from 'vue'
import type { DesktopApi } from '../shared/health'
import type { AssetDraft, DraftData } from '../shared/workspace'
import type { ArchiveStatus } from '../shared/archive'
import { modelStatus, type ModelConnection, type ModelSettingsView, type SuggestionKind } from '../shared/model'

interface ModelDependencies {
  draft: Readonly<Ref<AssetDraft | null>>
  edited: Readonly<Ref<DraftData | null>>
  activeArchive: Readonly<Ref<ArchiveStatus | undefined>>
  flush: () => Promise<boolean>
  refreshDraft: (id: string) => Promise<void>
}

export function useModelSuggestions(api: DesktopApi, dependencies: ModelDependencies) {
  const { draft, edited, activeArchive, flush, refreshDraft } = dependencies
  let disposed = false
  const modelConnection = ref<ModelConnection>()
  const checkingModel = ref(false)
  let modelCheck = 0
  const modelConnectionState = computed(() => checkingModel.value ? 'checking' : modelConnection.value?.state ?? 'checking')
  const modelConnectionLabel = computed(() => ({
    checking: '正在检测', connected: '连接正常', disabled: '未启用', unconfigured: '待配置', error: '连接异常'
  })[modelConnectionState.value])
  const modelMessages = ref<Partial<Record<SuggestionKind, string>>>({})
  const runningSuggestions = ref<Partial<Record<SuggestionKind, string>>>({})
  const suggestions = computed(() => {
    const saved = draft.value?.data
    const live = edited.value
    if (!saved || !live) return {}
    const result = { ...saved.suggestions }
    for (const kind of ['name', 'remark'] as const) {
      const item = result[kind]
      if (!item) continue
      if ((kind !== 'remark' && saved.name !== live.name) ||
          (kind === 'remark' && saved.keywords !== live.keywords) ||
          (item.usesRemark && saved.remark.value !== live.remark.value) ||
          (item.usesAuthors && JSON.stringify(saved.authors.value) !== JSON.stringify(live.authors.value))) delete result[kind]
    }
    return result
  })

  async function checkModelConnection(settings?: ModelSettingsView) {
    const request = ++modelCheck
    checkingModel.value = true
    try {
      if (!settings && !modelConnection.value) {
        const reply = await api.getModelSettings()
        if (request !== modelCheck) return
        if ('result' in reply) settings = reply.result
      }
      if (settings) modelConnection.value = { model: settings.model, state: 'disabled' }
      const reply = await api.checkModelConnection()
      if (request !== modelCheck) return
      if ('error' in reply) throw new Error(reply.error.message)
      modelConnection.value = reply.result
    } catch {
      if (request === modelCheck) modelConnection.value = {
        model: modelConnection.value?.model ?? '', state: 'error', message: '无法读取模型连接状态，请重试。'
      }
    } finally {
      if (request === modelCheck) checkingModel.value = false
    }
  }

  function generating(kind: 'name' | 'remark') {
    return !!draft.value && runningSuggestions.value[kind] === draft.value.assetId
  }

  async function generate(kind: 'name' | 'remark') {
    if (!draft.value || activeArchive.value || generating(kind)) return
    const id = draft.value.assetId
    if (!await flush() || disposed || draft.value?.assetId !== id) return
    runningSuggestions.value[kind] = id
    modelMessages.value[kind] = ''
    try {
      const result = await api.modelSuggest(id, kind)
      if (disposed) return
      await refreshDraft(id)
      if (!disposed && draft.value?.assetId === id && 'error' in result) modelMessages.value[kind] = result.error.message
    } catch {
      if (!disposed && draft.value?.assetId === id) modelMessages.value[kind] = modelStatus('MODEL_UNAVAILABLE')
    } finally {
      if (runningSuggestions.value[kind] === id) delete runningSuggestions.value[kind]
    }
  }

  function suggestionMessage(kind: 'name' | 'remark') {
    const current = suggestions.value[kind]
    return modelMessages.value[kind] || (current?.status === 'retry' && current.errorCode ? modelStatus(current.errorCode) : '')
  }

  watch(() => draft.value?.assetId, () => { modelMessages.value = {} })
  onMounted(() => { void checkModelConnection() })
  onUnmounted(() => { disposed = true; ++modelCheck })
  return { modelConnection, checkingModel, modelConnectionState, modelConnectionLabel, checkModelConnection,
    suggestions, generating, generate, suggestionMessage }
}
