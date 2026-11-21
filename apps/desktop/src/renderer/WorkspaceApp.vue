<script setup lang="ts">
import OperationFeedback from './OperationFeedback.vue'
import NoticeHost from './NoticeHost.vue'
import { notify } from './notices'
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { FilePlus, FolderPlus, Inbox, RefreshCw, Search, Settings } from '@lucide/vue'
import type { Health } from '../shared/health'
import type { AssetDraft, DraftChanges } from '../shared/workspace'
import MediaPreview from './MediaPreview.vue'
import WorkspaceQueue from './WorkspaceQueue.vue'
import { queueAssetDragType } from './queueDrag'
import { queuePathKey } from './queueSelection'
import WorkspaceBatch from './WorkspaceBatch.vue'
import LibrarySearch from './LibrarySearch.vue'
import { useModelSuggestions } from './useModelSuggestions'
import WorkspaceEditor from './WorkspaceEditor.vue'
import { useWorkspace } from './useWorkspace'
import { useWorkspaceSplit } from './useWorkspaceSplit'
import { useArchiveOperations } from './useArchiveOperations'
import WorkspaceSettings from './WorkspaceSettings.vue'
import { useShortcutRecording } from './useShortcutRecording'
import { useWorkspacePreferences } from './useWorkspacePreferences'
import { type SettingsSection } from './workspaceSettings'

const api = window.mediaLibrary
const preview = ref<InstanceType<typeof MediaPreview>>()
const loadingPhotoId = ref<string>()
const {
  items, importFolders, asset, draft, edited, importResults, archiveLog, loading, importing, busy, exiting, saveError, workspaceError, saveState,
  reload, removeQueueAssets, removeQueueFolder, select, choose, edit, flush, operate, refreshDraft, writeSourceProperties, saveBatch, retrySave
} = useWorkspace(api, () => preview.value?.savePosition())
const health = ref<Health>()
const healthState = ref('loading')
const pages = { workspace: '素材整理', search: '素材搜索', settings: '设置' }
type Page = keyof typeof pages
const page = ref<Page>('workspace')
const settingsSection = ref<SettingsSection>('general')
const shortcuts = useShortcutRecording(api, computed(() => page.value === 'settings' && settingsSection.value === 'shortcuts'))
const { hotkeys } = shortcuts
const preferences = useWorkspacePreferences(api, flush, operate, saveError)

const { layout, layoutStyle, activePane, dividerValues, startResize, moveResize, stopResize, resizeWithKey } = useWorkspaceSplit()
const batchOpen = ref(false)
const draggedQueueIds = ref<string[]>([])
const batchDragOver = ref(false)
const singleEditName = ref(false)
const singleEditRemark = ref(false)
const batchIds = ref<string[]>([])
const batchDrafts = ref(new Map<string, AssetDraft>())
const batchItems = computed(() => {
  const byId = new Map(items.value.map((item) => [item.assetId, item]))
  return batchIds.value.flatMap((id) => {
    const item = byId.get(id)
    const cached = batchDrafts.value.get(id)
    const current = draft.value?.assetId === id ? draft.value : undefined
    const saved = current && (!cached || current.revision >= cached.revision) ? current : cached
    return item ? [{ ...item, draftName: saved?.data.name }] : []
  })
})
const batchErrors = ref(new Map<string, string>())
const batchMessage = ref('')
const batchBlocked = ref(false)
const batchSaving = ref(false)
const content = ref<HTMLElement>()
const sourcePropertyMessage = ref('')
const sourcePropertyError = ref(false)
const savingSourceProperties = ref(false)
const errorDialog = ref<HTMLDialogElement>()
const errorMessage = ref('')
const dialogTitle = ref('操作未完成')

async function showSaveError(message: string) {
  dialogTitle.value = '操作未完成'
  errorMessage.value = message
  await nextTick()
  if (!errorDialog.value?.open) errorDialog.value?.showModal()
}

async function showSaveSuccess(message = '保存成功') {
  dialogTitle.value = '保存成功'
  errorMessage.value = message
  await nextTick()
  if (!errorDialog.value?.open) errorDialog.value?.showModal()
}

watch(sourcePropertyMessage, (message) => {
  if (message && sourcePropertyError.value) void showSaveError(message)
})
watch([importResults, importing], ([results, active]) => {
  if (active || !results.length) return
  const imported = results.filter(item => item.status === 'imported').length
  const duplicate = results.filter(item => item.status === 'duplicate').length
  const skipped = results.filter(item => item.status === 'unsupported').length
  const failed = results.filter(item => item.status === 'unavailable').length
  const message = [
    imported ? `导入成功 · ${imported} 项素材` : duplicate ? '素材已在队列中' : '未导入新素材',
    imported && duplicate ? `${duplicate} 项重复` : '',
    skipped ? `${skipped} 项不支持，已跳过` : '',
    failed ? `${failed} 项导入失败` : ''
  ].filter(Boolean).join('，')
  notify(message, failed > 0)
})
const disabled = computed(() => loading.value || busy.value || exiting.value || copyingBatch.value || batchSaving.value)
const { directories, archiveError, activeArchive, copyingBatch, selectDirectory, addDirectory, saveAs, copyBatch,
  invalidatePlan } = useArchiveOperations(api, {
  asset, draft, archiveLog, saveState, saveError, disabled, flush, operate, edit, reload, refreshDraft,
  directoryDraftId: () => page.value === 'workspace' && !batchOpen.value ? draft.value?.assetId : undefined,
  getPreview: () => {
    const player = preview.value
    return player && { closePreview: () => player.closePreview(), reopen: () => player.reopen() }
  },
  showSaveError, showSaveSuccess
}, { batchItems, batchIds, batchDrafts, batchMessage, batchBlocked, batchErrors, rememberBatchDraft })
const { modelConnection, checkingModel, modelConnectionState, modelConnectionLabel, checkModelConnection,
  suggestions, generating, generate, suggestionMessage } = useModelSuggestions(api, { draft, edited, activeArchive, flush, refreshDraft })
watch(archiveError, (message) => { if (message) void showSaveError(message) })
const dateFormatter = new Intl.DateTimeFormat('zh-CN', { dateStyle: 'long' })
let stopHealth: (() => void) | undefined
let stopMainPage: (() => void) | undefined
let healthRequest = 0
watch(() => draft.value?.assetId, () => {
  sourcePropertyMessage.value = ''
})
watch(() => edited.value?.remark, () => {
  if (!savingSourceProperties.value) sourcePropertyMessage.value = ''
}, { flush: 'sync' })
watch(items, () => {
  batchIds.value = batchItems.value.map((item) => item.assetId)
  const remaining = new Set(batchIds.value)
  for (const id of batchErrors.value.keys()) {
    if (!remaining.has(id)) batchErrors.value.delete(id)
  }
  for (const id of batchDrafts.value.keys()) {
    if (!remaining.has(id)) batchDrafts.value.delete(id)
  }
})
watch(page, () => { if (content.value) content.value.scrollTop = 0 }, { flush: 'post' })

async function saveSourceProperties() {
  if ((!singleEditName.value && !singleEditRemark.value) || disabled.value || activeArchive.value || !asset.value?.available) return
  preview.value?.savePosition()
  await operate(async () => {
    if (!await flush()) {
      void showSaveError(saveError.value || '草稿保存失败，请重试。')
      return
    }
    savingSourceProperties.value = true
    sourcePropertyMessage.value = ''
    sourcePropertyError.value = false
    const player = preview.value
    try {
      await player?.closePreview()
      const result = await writeSourceProperties()
      if (!result) {
        void showSaveError(saveError.value || '无法保存属性，请重试。')
        return
      }
      sourcePropertyError.value = result.remark === 'write_failed'
      const messages = []
      if (result.renamedPath) messages.push('原件已重命名')
      if (result.remark === 'written') messages.push('原件的「详细信息 → 备注」已保存')
      if (sourcePropertyError.value) messages.push(result.remarkFailure ?? '无法保存原件备注，草稿已保留')
      sourcePropertyMessage.value = messages.join('。') || '没有需要保存的原件属性变更'
      if (!sourcePropertyError.value) void showSaveSuccess()
      invalidatePlan()
    } catch (error) {
      sourcePropertyError.value = true
      sourcePropertyMessage.value = error instanceof Error ? error.message : '无法保存原件属性。'
    } finally {
      savingSourceProperties.value = false
      void player?.reopen()
    }
  })
}

async function checkHealth() {
  const request = ++healthRequest
  healthState.value = 'loading'
  try {
    const reply = await api.health()
    if (request !== healthRequest) return
    if ('error' in reply) throw new Error(reply.error.message)
    health.value = reply.result
    healthState.value = 'ready'
  } catch {
    if (request !== healthRequest) return
    health.value = undefined
    healthState.value = 'error'
  }
}

function selectAsset(id: string) {
  preview.value?.savePosition()
  return select(id)
}

async function removeQueueSelection(ids: string[], folders: Array<{ root: string; path: string }>) {
  if (disabled.value) return
  const unique = [...new Map(folders.map(folder => [queuePathKey(folder.path), folder])).values()]
  const parents = unique.filter(folder => !unique.some(other => other !== folder &&
    queuePathKey(folder.path).startsWith(`${queuePathKey(other.path)}\\`)))
  for (const folder of parents) {
    await removeQueueFolder(folder.root, folder.path)
    if (workspaceError.value) return
  }
  const remaining = ids.filter(id => items.value.some(item => item.assetId === id))
  if (remaining.length) await removeQueueAssets(remaining)
}

function addToBatch(ids: string[]) {
  if (disabled.value) return
  const existing = new Set(items.value.map(item => item.assetId))
  ids = ids.filter(id => existing.has(id))
  if (!ids.length) return
  const combined = [...new Set([...batchIds.value, ...ids])]
  const added = combined.length - batchIds.value.length
  batchIds.value = combined
  batchMessage.value = added ? `已加入 ${added} 项素材。` : '所选素材已在批次中。'
  batchBlocked.value = false
  batchOpen.value = true
  for (const id of ids) {
    void api.getDraft(id).then((reply) => {
      if ('result' in reply && batchIds.value.includes(id)) rememberBatchDraft(reply.result)
    }).catch(() => { /* Names fall back to the original until a draft is available. */ })
  }
}

function endQueueDrag() {
  draggedQueueIds.value = []
  batchDragOver.value = false
}

function canDropQueue(event: DragEvent) {
  return !disabled.value && draggedQueueIds.value.length > 0 && event.dataTransfer?.types.includes(queueAssetDragType)
}

function overBatch(event: DragEvent) {
  if (!canDropQueue(event)) return
  if (!batchOpen.value && !(event.target instanceof Element && event.target.closest('[data-batch-tab]'))) return
  event.preventDefault()
  event.stopPropagation()
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy'
  batchOpen.value = true
  batchDragOver.value = true
}

function leaveBatch(event: DragEvent) {
  if (event.relatedTarget instanceof Node && (event.currentTarget as HTMLElement).contains(event.relatedTarget)) return
  batchDragOver.value = false
}

function dropBatch(event: DragEvent) {
  if (!canDropQueue(event) || !batchOpen.value) return
  event.preventDefault()
  event.stopPropagation()
  const ids = [...draggedQueueIds.value]
  endQueueDrag()
  addToBatch(ids)
}

function rememberBatchDraft(value: AssetDraft) {
  const previous = batchDrafts.value.get(value.assetId)
  if (!previous || value.revision >= previous.revision) batchDrafts.value.set(value.assetId, value)
}

function removeFromBatch(ids: string[]) {
  const removed = new Set(ids)
  batchIds.value = batchIds.value.filter((entry) => !removed.has(entry))
  for (const id of ids) {
    batchErrors.value.delete(id)
    batchDrafts.value.delete(id)
  }
  batchMessage.value = ''
  batchBlocked.value = false
}

function clearBatch() {
  batchIds.value = []
  batchDrafts.value.clear()
  batchErrors.value.clear()
  batchMessage.value = ''
  batchBlocked.value = false
}

async function applyBatch(changes: DraftChanges, names: Record<string, string> = {}) {
  if (disabled.value || batchSaving.value || !batchItems.value.length) return
  preview.value?.savePosition()
  batchSaving.value = true
  batchMessage.value = ''
  batchBlocked.value = false
  const player = preview.value
  let closedPreview = false
  try {
    const ids = batchItems.value.map((item) => item.assetId)
    if (ids.includes(draft.value?.assetId ?? '')) {
      await player?.closePreview()
      closedPreview = true
    }
    const results = await saveBatch(ids,
      (id) => ({ ...changes, ...(names[id] !== undefined ? { name: names[id] } : {}) }), { writeSourceProperties: true })
    if (!results) {
      batchBlocked.value = true
      batchMessage.value = saveError.value ? `当前草稿保存失败。请在「单项」中重试保存。${saveError.value}` : '暂时无法应用批量修改，请重试。'
      void showSaveError(batchMessage.value)
      return
    }
    const saved = new Set<string>()
    const failed = new Map<string, string>()
    for (const { id, error, draft: savedDraft } of results) {
      if (savedDraft) rememberBatchDraft(savedDraft)
      if (error !== undefined) failed.set(id, error)
      else {
        saved.add(id)
      }
    }
    batchErrors.value = failed
    batchBlocked.value = failed.size > 0
    batchMessage.value = failed.size
      ? `已保存 ${saved.size} 项，${failed.size} 项失败。可查看原因后重试。`
      : `已保存 ${saved.size} 项原文件属性。`
    if (!failed.size) void showSaveSuccess(`保存成功，已保存 ${saved.size} 项。`)
    else void showSaveError(`${batchMessage.value}\n${[...failed].map(([id, error]) =>
      `${batchItems.value.find((item) => item.assetId === id)?.originalName ?? id}：${error}`).join('\n')}`)
  } catch (error) {
    batchBlocked.value = true
    batchMessage.value = error instanceof Error ? error.message : '保存原文件属性失败，请重试。'
    void showSaveError(batchMessage.value)
  } finally {
    batchSaving.value = false
    if (closedPreview) void player?.reopen()
  }
}

async function renameBatchItem(id: string, name: string): Promise<boolean> {
  if (disabled.value || !batchIds.value.includes(id)) return false
  preview.value?.savePosition()
  batchSaving.value = true
  try {
    const results = await saveBatch([id], { name })
    const saved = results?.[0]
    if (!saved?.draft) {
      batchErrors.value.set(id, saved?.error ?? '无法保存名称，请重试。')
      return false
    }
    rememberBatchDraft(saved.draft)
    batchErrors.value.delete(id)
    batchMessage.value = '名称已保存。'
    batchBlocked.value = false
    return true
  } finally { batchSaving.value = false }
}

async function showPage(next: Page) {
  if (page.value === next) return
  if (page.value !== 'workspace') {
    page.value = next
    return
  }
  preview.value?.savePosition()
  await operate(async () => {
    if (!await flush()) return
    page.value = next
  })
}

async function openSettings(section: SettingsSection) {
  await showPage('settings')
  if (page.value === 'settings') settingsSection.value = section
}

onMounted(() => {
  stopHealth = api.onLocalServiceExited(() => {
    ++healthRequest
    health.value = undefined
    healthState.value = 'error'
  })
  void checkHealth()
  stopMainPage = api.onShowMainPage(() => { void showPage('workspace') })
})
onUnmounted(() => {
  stopHealth?.()
  stopMainPage?.()
})
</script>

<template>
  <div class="workspace-app" :data-health-state="healthState" :data-schema-version="health?.database.schemaVersion">
    <NoticeHost />
    <div class="app-titlebar workspace-titlebar">
      <nav class="app-titlebar-nav" aria-label="工作区">
        <button type="button" data-workspace-tab aria-label="素材整理" title="素材整理"
          :aria-current="page === 'workspace' ? 'page' : undefined" :disabled="disabled" @click="showPage('workspace')">
          <Inbox :size="18" aria-hidden="true" />
        </button>
        <button type="button" data-search-tab aria-label="素材搜索" title="素材搜索"
          :aria-current="page === 'search' ? 'page' : undefined" :disabled="disabled" @click="showPage('search')">
          <Search :size="18" aria-hidden="true" />
        </button>
        <button type="button" data-settings-tab aria-label="设置" title="设置"
          :aria-current="page === 'settings' ? 'page' : undefined" :disabled="disabled" @click="showPage('settings')">
          <Settings :size="18" aria-hidden="true" />
        </button>
      </nav>
      <div class="workspace-model-status" :data-model-state="modelConnectionState">
        <div class="workspace-model-status-text" role="status"
          :title="[modelConnection?.model || '大模型', modelConnectionLabel, modelConnection?.message].filter(Boolean).join(' · ')">
          <span class="workspace-status-dot" aria-hidden="true"></span>
          <span class="workspace-model-status-name">{{ modelConnection?.model || '大模型' }}</span>
          <span class="workspace-model-status-label">{{ modelConnectionLabel }}</span>
        </div>
        <button v-if="modelConnectionState === 'disabled' || modelConnectionState === 'unconfigured'"
          type="button" aria-label="配置大模型" title="配置大模型"
          :disabled="disabled" @click="openSettings('model')">
          <Settings :size="14" aria-hidden="true" /><span>配置</span>
        </button>
        <button v-else-if="modelConnectionState !== 'checking'" type="button"
          aria-label="重新检测大模型" title="重新检测大模型" @click="checkModelConnection()">
          <RefreshCw :size="14" aria-hidden="true" /><span>重新检测</span>
        </button>
      </div>
    </div>

    <div class="workspace-body">
      <main ref="content" class="workspace-content" :class="{ 'workspace-content-search': page === 'search' }" :aria-label="pages[page]">
        <p v-if="healthState === 'error'" class="workspace-alert" role="status">本地服务暂不可用
          <button type="button" @click="checkHealth(); reload()">重新连接</button>
        </p>
        <p v-if="page !== 'settings' && hotkeys.search.current && !hotkeys.search.current.registered" class="workspace-alert workspace-hotkey-notice" role="status">
          快捷键 {{ hotkeys.search.text }} 无法注册。仍可从托盘打开快捷搜索。
          <button type="button" :disabled="disabled" @click="openSettings('shortcuts')">修改快捷键</button>
        </p>

        <WorkspaceSettings v-if="page === 'settings'" v-model:section="settingsSection"
          :disabled="disabled" :exiting="exiting" :model-connection="modelConnection" :checking-model="checkingModel"
          :archive-log="archiveLog" :preferences="preferences" :shortcuts="shortcuts"
          @exit="api.requestExit()" @model-saved="checkModelConnection" />

        <LibrarySearch v-else-if="page === 'search'"
          :default-directory="directories.find((directory) => directory.directoryId === edited?.directoryId)?.fullPath" />
        <div v-else ref="layout" class="workspace-layout" :style="layoutStyle" :data-resizing="activePane">
          <section id="workspace-queue-pane" class="workspace-list" aria-labelledby="workspace-files">
            <div class="workspace-panel-heading workspace-queue-heading">
              <h2 id="workspace-files">素材队列</h2>
              <div class="workspace-queue-actions">
                <span>{{ items.length }} 项</span>
                <button type="button" data-import-files :disabled="disabled" title="导入文件" aria-label="导入文件" @click="choose()">
                  <FilePlus :size="17" aria-hidden="true" />
                </button>
                <button type="button" :disabled="disabled" title="导入文件夹" aria-label="导入文件夹" @click="choose('folder')">
                  <FolderPlus :size="17" aria-hidden="true" />
                </button>
              </div>
            </div>
            <div class="workspace-drop" data-import-drop>将照片、视频或文件夹拖到这里</div>
            <div v-if="importing" class="workspace-import-progress" role="status">
              <span class="workspace-import-spinner" aria-hidden="true"></span>
              <span>正在导入素材…</span>
            </div>
            <OperationFeedback :message="workspaceError" warning action-label="重试" @action="reload" />
            <WorkspaceQueue v-if="!loading || items.length" :items="items" :roots="importFolders" :importing="importing"
              :selected-id="draft?.assetId" :loading-photo-id="loadingPhotoId" :disabled="disabled" @select="selectAsset"
              @drag-start="draggedQueueIds = $event" @drag-end="endQueueDrag"
              @remove="removeQueueAssets" @remove-folder="removeQueueFolder" @remove-selection="removeQueueSelection" @add-batch="addToBatch" />
          </section>

          <div class="workspace-divider workspace-divider-queue" role="separator" tabindex="0"
            aria-orientation="vertical" aria-label="素材队列宽度" aria-controls="workspace-queue-pane" title="拖动调整素材队列宽度"
            v-bind="dividerValues('queue')" @keydown="resizeWithKey('queue', $event)"
            @pointerdown="startResize('queue', $event)" @pointermove="moveResize" @pointerup="stopResize"
            @pointercancel="stopResize" @lostpointercapture="stopResize"></div>
          <MediaPreview v-if="asset && (!activeArchive || activeArchive.phase === 'needs_review')"
            id="workspace-preview-pane" ref="preview" :asset-id="asset.assetId" :kind="asset.mediaKind"
            :position="edited?.previewPositionMs ?? 0" @position="!activeArchive && edit({ previewPositionMs: $event })"
            @photo-loading="loadingPhotoId = $event">
            <template #title>
              <h2 class="media-file-name" :title="asset.originalName">{{ asset.originalName }}</h2>
            </template>
            <template #metadata>
              <div class="media-file-details">
                <time :datetime="asset.createdAt">{{ dateFormatter.format(new Date(asset.createdAt)) }}</time>
                <div v-if="asset.sourcePath" class="media-file-path" :title="asset.sourcePath">{{ asset.sourcePath }}</div>
              </div>
            </template>
          </MediaPreview>
          <section v-else id="workspace-preview-pane" class="media-panel" aria-label="素材预览">
            <h2>预览</h2>
            <div class="media-frame">
              <p role="status">{{ activeArchive ? '正在复制素材…' : loading ? '正在读取队列…' : '选择一项素材进行预览' }}</p>
            </div>
          </section>
          <div class="workspace-divider workspace-divider-preview" role="separator" tabindex="0"
            aria-orientation="horizontal" aria-label="素材预览高度" aria-controls="workspace-preview-pane" title="拖动调整素材预览高度"
            v-bind="dividerValues('preview')" @keydown="resizeWithKey('preview', $event)"
            @pointerdown="startResize('preview', $event)" @pointermove="moveResize" @pointerup="stopResize"
            @pointercancel="stopResize" @lostpointercapture="stopResize"></div>
          <section class="workspace-editor" aria-label="草稿编辑" tabindex="0" :data-save-state="saveState"
            :class="{ 'workspace-batch-drop-active': batchDragOver }"
            @dragenter="overBatch" @dragover="overBatch" @dragleave="leaveBatch" @drop="dropBatch">
            <div class="workspace-panel-heading workspace-editor-toolbar">
              <div class="workspace-batch-views" role="group" aria-label="草稿编辑方式">
                <button type="button" :aria-pressed="!batchOpen" @click="batchOpen = false">单项</button>
                <button type="button" data-batch-tab :aria-pressed="batchOpen" @click="batchOpen = true"
                  title="可将素材队列中的文件拖到此处">批量</button>
              </div>
            </div>
            <WorkspaceBatch v-show="batchOpen" :items="batchItems" :directories="directories" :disabled="disabled"
              :rename-item="renameBatchItem"
              :saving="batchSaving || copyingBatch" :errors="batchErrors" :message="batchMessage" :blocked="batchBlocked"
              @preview="selectAsset" @remove="removeFromBatch" @clear="clearBatch" @apply="applyBatch" @copy="copyBatch" @choose-directory="addDirectory(true)" />
            <WorkspaceEditor v-if="!batchOpen" v-model:single-edit-name="singleEditName" v-model:single-edit-remark="singleEditRemark"
              :asset="asset" :edited="edited" :loading="loading" :disabled="disabled" :active-archive="!!activeArchive"
              :save-error="saveError" :saving-source-properties="savingSourceProperties" :directories="directories" :suggestions="suggestions"
              :generating-name="generating('name')" :generating-remark="generating('remark')"
              :name-message="suggestionMessage('name')" :remark-message="suggestionMessage('remark')"
              @edit="edit" @generate="generate" @retry-save="retrySave" @save-properties="saveSourceProperties"
              @archive="saveAs" @choose-directory="addDirectory(true)" @select-directory="selectDirectory" />
          </section>
        </div>
      </main>
    </div>
    <dialog ref="errorDialog" class="workspace-error-dialog" aria-labelledby="workspace-error-title" aria-describedby="workspace-error-message">
      <h2 id="workspace-error-title">{{ dialogTitle }}</h2>
      <p id="workspace-error-message">{{ errorMessage }}</p>
      <form method="dialog"><button type="submit" class="workspace-primary" autofocus>确定</button></form>
    </dialog>
  </div>
</template>
