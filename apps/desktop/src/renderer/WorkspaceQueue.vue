<script setup lang="ts">
import OperationFeedback from './OperationFeedback.vue'
import { computed, nextTick, onUnmounted, ref, shallowRef, watch } from 'vue'
import { ArrowDown, ArrowUp, ChevronDown, ChevronRight, File, FileImage, FileMinus, FileVideo, Folder, FolderOpen, ListPlus, RotateCw } from '@lucide/vue'
import { isControlA } from '../shared/keyboard'
import type { Asset } from '../shared/workspace'
import { useQueueTree, type QueueRow } from './useQueueTree'
import QueueThumbnail from './QueueThumbnail.vue'
import { compareQueueItems, type QueueSortKey, type QueueSorting } from './queueSort'
import { mediaLayouts as layouts, type MediaLayout as Layout } from './mediaLayouts'
import { queueAssetDragType } from './queueDrag'
import { folderAssetIds, queuePathKey, queueOpenTarget, type QueueOpenTarget } from './queueSelection'
import { filterQueueAssets, filterQueueRows, type QueueMediaType } from './queueTypeFilter'

const props = defineProps<{
  items: Asset[]; roots: string[]; selectedId?: string; loadingPhotoId?: string; importing: boolean; disabled: boolean
}>()
const emit = defineEmits<{
  select: [assetId: string]; remove: [assetIds: string[]]; removeFolder: [root: string, path: string]
  addBatch: [assetIds: string[]]
  dragStart: [assetIds: string[]]; dragEnd: []
  removeSelection: [assetIds: string[], folders: Array<{ root: string; path: string }>]
}>()
const selection = ref<Set<string> | null>(null)
const includeCollapsed = ref(false)
const selected = computed(() => selection.value ?? new Set(
  props.selectedId && props.items.some((item) => item.assetId === props.selectedId) ? [props.selectedId] : []
))
type MenuTarget = { ids: string[]; assetId: string; folders?: QueueFolder[]; openFolder?: QueueOpenTarget } | { root: string; path: string } | { path: string }
const menu = shallowRef<{ x: number; y: number; target: MenuTarget }>()
const menuPanel = ref<HTMLElement>()
let menuSource: HTMLButtonElement | undefined
let anchor: string | undefined
const columns = [
  { key: 'name', label: '名称' },
  { key: 'modified', label: '修改日期' },
  { key: 'type', label: '类型' },
  { key: 'size', label: '大小' },
  { key: 'status', label: '状态' }
] as const
const sorting = ref<QueueSorting>()
const layoutKey = 'workspace.queue.layout'
function savedLayout(): Layout {
  try {
    const value = localStorage.getItem(layoutKey)
    if (value === 'large' || value === 'medium') return value
  } catch { /* Storage can be unavailable. */ }
  return 'list'
}
const layout = ref(savedLayout())
const mediaType = ref<QueueMediaType>('all')
const filteredItems = computed(() => filterQueueAssets(props.items, mediaType.value))
function changeLayout(value: Layout) {
  menu.value = undefined
  layout.value = value
  try { localStorage.setItem(layoutKey, value) } catch { /* Keep the layout for this session. */ }
}
const collator = new Intl.Collator('zh-CN', { numeric: true, sensitivity: 'base' })
const dates = new Intl.DateTimeFormat('zh-CN', { dateStyle: 'short', timeStyle: 'short' })
const numbers = new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 1 })

function sortBy(key: QueueSortKey) {
  sorting.value = {
    key,
    direction: sorting.value?.key === key && sorting.value.direction === 'ascending' ? 'descending' : 'ascending'
  }
}

const rootModifiedDates = ref(new Map<string, string>())
const { rows: treeRows, toggle, refresh } = useQueueTree(props, window.mediaLibrary.browseFolder,
  (left, right) => sorting.value ? compareQueueItems(
    { ...left, name: left.originalName }, { ...right, name: right.originalName }, sorting.value) : 0,
  (left, right) => collator.compare(left, right),
  (left, right, directory) => {
    if (!sorting.value) return directory ? collator.compare(left.name, right.name) : 0
    // Directories have no file size; keep their natural name order when sorting files by size.
    if (directory && sorting.value.key === 'size') return collator.compare(left.name, right.name)
    const withDate = (item: typeof left) => ({ ...item,
      modifiedAt: item.modifiedAt ?? ('key' in item ? rootModifiedDates.value.get(String(item.key)) : undefined) })
    return compareQueueItems(withDate(left), withDate(right), sorting.value)
  })
const rows = computed(() => filterQueueRows(treeRows.value, mediaType.value))
type QueueFolder = Extract<QueueRow, { kind: 'folder' }>['folder']
const folderTargets = ref(new Map<string, QueueFolder>())
const selectedFolders = computed(() => [...selected.value].flatMap(key => {
  const folder = folderTargets.value.get(key)
  return folder ? [folder] : []
}))
const visibleKeys = computed(() => rows.value.flatMap(row => row.kind === 'folder' || row.kind === 'asset' ? [row.key] : []))

function folderDate(folder: QueueFolder) {
  const date = folder.modifiedAt ?? rootModifiedDates.value.get(folder.key)
  return date ? dates.format(new Date(date)) : '—'
}

watch(() => props.roots, async (roots, _, onCleanup) => {
  let cancelled = false
  onCleanup(() => { cancelled = true })
  for (const path of roots) {
    const key = path.replaceAll('/', '\\').replace(/\\+$/, '').toLowerCase()
    try {
      const reply = await window.mediaLibrary.folderDetails(path, path)
      if (cancelled) return
      if ('result' in reply) rootModifiedDates.value.set(key, reply.result.modifiedAt)
    } catch { if (cancelled) return }
  }
}, { immediate: true })
const visibleIds = computed(() => rows.value.flatMap((row) => row.kind === 'asset' ? [row.asset.assetId] : []))
type AssetRow = Extract<QueueRow, { kind: 'asset' }>
type GalleryRow = Exclude<QueueRow, AssetRow> | { kind: 'cards'; key: string; depth: number; items: AssetRow[] }
const galleryRows = computed(() => {
  const groups: GalleryRow[] = []
  for (const row of rows.value) {
    const previous = groups.at(-1)
    if (row.kind !== 'asset') groups.push(row)
    else if (previous?.kind === 'cards' && previous.depth === row.depth) previous.items.push(row)
    else groups.push({ kind: 'cards', key: row.key, depth: row.depth, items: [row] })
  }
  return groups
})
const queueScroll = ref<HTMLElement>()
const pinnedDirectory = ref<QueueFolder>()
const directoryContext = ref<HTMLElement>()
let directoryFrame = 0
let measureDirectoryRows = true
let directoryRows: Array<{ key: string; top: number; bottom: number; folder: boolean }> = []
const folderPositions = new Map<string, number>()
let nextDirectoryTops: Array<number | undefined> = []
let tableHeaderHeight = 0
const parentDirectories = computed(() => {
  const parents = new Map<string, QueueFolder & { header: string }>()
  const ancestors: Array<Extract<QueueRow, { kind: 'folder' }>> = []
  for (const row of rows.value) {
    while (ancestors.length && ancestors.at(-1)!.depth >= row.depth) ancestors.pop()
    if (row.kind === 'folder') ancestors.push(row)
    else if (row.kind === 'asset' || row.kind === 'file') {
      const parent = ancestors.at(-1)
      if (parent) parents.set(row.key, { ...parent.folder, header: parent.key })
    }
  }
  return parents
})

function updatePinnedDirectory() {
  directoryFrame = 0
  const scroll = queueScroll.value
  if (!scroll) {
    pinnedDirectory.value = undefined
    return
  }
  if (measureDirectoryRows) {
    const origin = scroll.getBoundingClientRect().top + scroll.clientTop - scroll.scrollTop
    tableHeaderHeight = scroll.querySelector('thead')?.getBoundingClientRect().height ?? 0
    directoryRows = Array.from(scroll.querySelectorAll<HTMLElement>('[data-queue-row]'), (entry) => {
      const bounds = entry.getBoundingClientRect()
      return {
        key: entry.dataset.queueRow!, top: bounds.top - origin, bottom: bounds.bottom - origin,
        folder: entry.classList.contains('queue-folder') || entry.classList.contains('queue-gallery-folder')
      }
    })
    folderPositions.clear()
    nextDirectoryTops = new Array(directoryRows.length)
    let nextTop: number | undefined
    for (let index = directoryRows.length - 1; index >= 0; index--) {
      const entry = directoryRows[index]!
      nextDirectoryTops[index] = nextTop
      if (entry.folder) {
        folderPositions.set(entry.key, entry.top)
        nextTop = entry.top
      }
    }
    scroll.style.setProperty('--queue-header-height', `${tableHeaderHeight}px`)
    measureDirectoryRows = false
  }
  const top = scroll.scrollTop + tableHeaderHeight
  let low = 0
  let high = directoryRows.length
  while (low < high) {
    const middle = (low + high) >>> 1
    if (directoryRows[middle]!.bottom <= top) low = middle + 1
    else high = middle
  }
  const first = directoryRows[low]
  let parent = first && parentDirectories.value.get(first.key)
  if (first?.folder && first.top <= top) {
    parent = [...parentDirectories.value.values()].find((entry) => entry.header === first.key)
  }
  const headerTop = parent && folderPositions.get(parent.header)
  const nextTop = nextDirectoryTops[low]
  const label = directoryContext.value?.firstElementChild as HTMLElement | null
  const height = label?.offsetHeight ?? 50
  const offset = nextTop === undefined ? 0 : Math.min(0, nextTop - top - height)
  directoryContext.value?.style.setProperty('--queue-directory-offset', `${offset}px`)
  const next = parent && scroll.scrollTop > 0 && headerTop !== undefined && headerTop <= top ? parent : undefined
  if (pinnedDirectory.value?.path !== next?.path) pinnedDirectory.value = next
}

function scheduleDirectoryUpdate() {
  if (!directoryFrame) directoryFrame = requestAnimationFrame(updatePinnedDirectory)
}

function remeasureDirectoryRows() {
  measureDirectoryRows = true
  scheduleDirectoryUpdate()
}

watch([rows, layout, queueScroll], () => { void nextTick(remeasureDirectoryRows) }, { flush: 'post' })
watch([queueScroll, layout], ([element], _, onCleanup) => {
  if (!element) return
  const observer = new ResizeObserver(remeasureDirectoryRows)
  observer.observe(element)
  const content = element.querySelector('table, .workspace-queue-gallery')
  if (content) observer.observe(content)
  onCleanup(() => observer.disconnect())
})
onUnmounted(() => cancelAnimationFrame(directoryFrame))
const selectedForAction = computed(() => {
  const ids = new Set(visibleIds.value)
  const matching = new Set(filteredItems.value.map(item => item.assetId))
  const direct = [...selected.value].filter(id => matching.has(id) && !folderTargets.value.has(id) && (includeCollapsed.value || ids.has(id)))
  return [...new Set([...direct, ...folderAssetIds(filteredItems.value, selectedFolders.value.map(folder => folder.path))])]
})
const highlightedAssets = computed(() => new Set(selectedForAction.value))

function startAssetDrag(event: DragEvent, id: string) {
  if (props.disabled || !event.dataTransfer) {
    event.preventDefault()
    return
  }
  menu.value = undefined
  const folder = folderTargets.value.get(id)
  const ids = selected.value.has(id) || selectedForAction.value.includes(id) ? selectedForAction.value
    : folder ? folderAssetIds(filteredItems.value, [folder.path]) : [id]
  if (!ids.length) { event.preventDefault(); return }
  event.dataTransfer.effectAllowed = 'copy'
  event.dataTransfer.setData(queueAssetDragType, JSON.stringify(ids))
  emit('dragStart', [...ids])
}

function formatSize(bytes: number | null | undefined) {
  if (bytes == null) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']
  const level = bytes < 1024 ? 0 : Math.min(Math.floor(Math.log2(bytes) / 10), units.length - 1)
  return `${numbers.format(bytes / 1024 ** level)} ${units[level]}`
}

watch([() => props.items, () => props.roots], ([items, roots]) => {
  menu.value = undefined
  const rootKeys = new Set(roots.map(queuePathKey))
  for (const [key, folder] of folderTargets.value) if (!rootKeys.has(queuePathKey(folder.root))) folderTargets.value.delete(key)
  const visible = new Set([...items.map((item) => item.assetId), ...folderTargets.value.keys()])
  if (selection.value) {
    const remaining = [...selection.value].filter((id) => visible.has(id))
    selection.value = remaining.length ? new Set(remaining) : null
    if (!remaining.length) includeCollapsed.value = false
  }
  if (anchor && !visible.has(anchor)) anchor = undefined
})

watch(() => props.disabled, (disabled) => { if (disabled) menu.value = undefined })
watch(mediaType, () => {
  menu.value = undefined
  selection.value = new Set()
  includeCollapsed.value = false
  anchor = undefined
  if (queueScroll.value) queueScroll.value.scrollTop = 0
})

function select(event: MouseEvent, id: string) {
  if (props.disabled) return
  menu.value = undefined
  if (!event.ctrlKey) includeCollapsed.value = false
  if (event.shiftKey) {
    const ids = visibleKeys.value
    const from = ids.indexOf(anchor ?? props.selectedId ?? id)
    const to = ids.indexOf(id)
    const range = from < 0 ? [id] : ids.slice(Math.min(from, to), Math.max(from, to) + 1)
    rememberFolders()
    selection.value = new Set(event.ctrlKey || event.metaKey ? [...selected.value, ...range] : range)
  } else {
    anchor = id
    const next = new Set(event.ctrlKey || event.metaKey ? selected.value : [])
    if (next.has(id)) next.delete(id)
    else next.add(id)
    selection.value = next
    if (!event.ctrlKey && !event.metaKey && !folderTargets.value.has(id) && id !== props.selectedId) emit('select', id)
  }
}

function selectAll(event: KeyboardEvent) {
  if (!isControlA(event)) return
  event.preventDefault()
  if (props.disabled || !rows.value.length || event.repeat) return
  if (menu.value) closeMenu()
  includeCollapsed.value = true
  rememberFolders()
  selection.value = new Set([...filteredItems.value.map((item) => item.assetId), ...visibleKeys.value.filter(key => folderTargets.value.has(key))])
}

function rememberFolders() {
  for (const row of rows.value) if (row.kind === 'folder') folderTargets.value.set(row.key, row.folder)
}

function selectFolder(event: MouseEvent, folder: QueueFolder) {
  const key = `folder:${folder.key}`
  folderTargets.value.set(key, folder)
  select(event, key)
}

function dragFolder(event: DragEvent, folder: QueueFolder) {
  const key = `folder:${folder.key}`
  folderTargets.value.set(key, folder)
  startAssetDrag(event, key)
}

function selectionTarget(): Extract<MenuTarget, { ids: string[] }> {
  const openFolder = queueOpenTarget([
    ...selectedFolders.value.map(folder => ({ kind: 'folder' as const, path: folder.path })),
    ...props.items.filter(item => selected.value.has(item.assetId))
      .map(item => ({ kind: 'file' as const, assetId: item.assetId, path: item.sourcePath }))
  ])
  return { ids: selectedForAction.value, assetId: selectedForAction.value[0] ?? '',
    folders: mediaType.value === 'all' ? selectedFolders.value : [], openFolder }
}

function openFolderMenu(event: MouseEvent | KeyboardEvent, folder: QueueFolder) {
  if (props.disabled) return
  const key = `folder:${folder.key}`
  folderTargets.value.set(key, folder)
  if (!selected.value.has(key)) { selection.value = new Set([key]); anchor = key }
  void openMenu(event, selectionTarget())
}

function openAssetMenu(event: MouseEvent | KeyboardEvent, id: string) {
  if (props.disabled) return
  if (!highlightedAssets.value.has(id)) {
    includeCollapsed.value = false
    selection.value = new Set([id])
    anchor = id
  }
  void openMenu(event, { ...selectionTarget(), assetId: id })
}

async function openMenu(event: MouseEvent | KeyboardEvent, target: MenuTarget) {
  if (props.disabled) return
  const row = event.currentTarget as HTMLElement
  menuSource = row instanceof HTMLButtonElement ? row : row.querySelector('button') ?? undefined
  const bounds = menuSource?.getBoundingClientRect() ?? row.getBoundingClientRect()
  const popup = {
    x: event instanceof MouseEvent ? event.clientX : bounds.left,
    y: event instanceof MouseEvent ? event.clientY : bounds.bottom,
    target
  }
  menu.value = popup
  await nextTick()
  const panel = menuPanel.value
  if (!panel || menu.value !== popup) return
  menu.value = {
    ...popup,
    x: Math.max(4, Math.min(popup.x, window.innerWidth - panel.offsetWidth - 4)),
    y: Math.max(4, Math.min(popup.y, window.innerHeight - panel.offsetHeight - 4))
  }
  panel.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus({ preventScroll: true })
}

function menuKey(event: KeyboardEvent, target: string | { root: string; path: string } | { path: string }) {
  if (event.key !== 'ContextMenu' && !(event.shiftKey && event.key === 'F10')) return
  event.preventDefault()
  if (typeof target === 'string') openAssetMenu(event, target)
  else if ('root' in target) {
    const folder = rows.value.find(row => row.kind === 'folder' && row.folder.path === target.path)
    if (folder?.kind === 'folder') openFolderMenu(event, folder.folder)
  } else void openMenu(event, target)
}

function removeSelected() {
  const target = menu.value?.target
  closeMenu()
  if (!target || props.disabled) return
  if ('ids' in target) {
    if (target.folders?.length) emit('removeSelection', target.ids, target.folders)
    else if (target.ids.length) emit('remove', target.ids)
  } else if ('root' in target) emit('removeFolder', target.root, target.path)
}

const folderError = ref('')
function containingFolderTarget(target: MenuTarget): QueueOpenTarget | undefined {
  return 'ids' in target ? target.openFolder : { path: target.path }
}
async function openContainingFolder() {
  const target = menu.value?.target
  const destination = target && containingFolderTarget(target)
  closeMenu()
  if (!destination || props.disabled) return
  folderError.value = ''
  try {
    if ('assetId' in destination) {
      const reply = await window.mediaLibrary.openAssetFolder(destination.assetId)
      if ('error' in reply) throw new Error(reply.error.message)
      if (!reply.result.launched) throw new Error('无法启动 Windows 资源管理器。')
    } else {
      const reply = await window.mediaLibrary.openQueueItemFolder(destination.path)
      if ('error' in reply) throw new Error(reply.error.message)
    }
  } catch (error) {
    folderError.value = error instanceof Error ? error.message : '无法打开所在文件夹。'
  }
}

function addToBatch() {
  const target = menu.value?.target
  closeMenu()
  if (!props.disabled && target && 'ids' in target && target.ids.length) emit('addBatch', target.ids)
}

function closeMenu() {
  menu.value = undefined
  menuSource?.focus({ preventScroll: true })
}

function leaveMenu(event: FocusEvent) {
  if (!menuPanel.value?.contains(event.relatedTarget as Node | null)) menu.value = undefined
}

function toggleFolder(row: Extract<typeof rows.value[number], { kind: 'folder' }>) {
  menu.value = undefined
  toggle(row.folder)
}
</script>

<template>
  <div class="workspace-queue-layouts" role="group" aria-label="素材队列查看布局" @keydown="selectAll">
    <button v-for="option in layouts" :key="option.key" type="button" :aria-pressed="layout === option.key"
      @click="changeLayout(option.key)">
      <component :is="option.icon" :size="15" aria-hidden="true" />{{ option.label }}
    </button>
    <select v-model="mediaType" class="workspace-queue-type-filter" aria-label="按素材类型筛选" :disabled="disabled">
      <option value="all">全部类型</option>
      <option value="photo">照片</option>
      <option value="video">视频</option>
    </select>
    <span v-if="selectedForAction.length > 1 || selectedFolders.length" class="workspace-queue-selected-count" role="status">
      <template v-if="selectedFolders.length">已选 {{ selectedFolders.length }} 个目录 · {{ selectedForAction.length }} 项素材</template>
      <template v-else>已选 {{ selectedForAction.length }} 项</template>
    </span>
  </div>
  <p v-if="!rows.length && !importing" class="workspace-muted" role="status">
    {{ mediaType === 'all' ? '队列为空' : mediaType === 'photo' ? '暂无照片素材' : '暂无视频素材' }}
  </p>
  <p v-else-if="mediaType !== 'all' && !filteredItems.length && !importing" class="workspace-muted" role="status">
    {{ mediaType === 'photo' ? '暂无照片素材' : '暂无视频素材' }}
  </p>
  <OperationFeedback :message="folderError" :warning="true" />
  <div v-if="rows.length" ref="queueScroll" class="workspace-queue-scroll" tabindex="0" role="region" aria-labelledby="workspace-files"
    aria-keyshortcuts="Control+A" @keydown="selectAll" @scroll.passive="scheduleDirectoryUpdate">
    <div ref="directoryContext" class="queue-directory-context">
      <button v-if="pinnedDirectory" type="button" class="queue-directory-context-label"
        :title="pinnedDirectory.path" aria-expanded="true" :aria-label="`收起目录 ${pinnedDirectory.name}`"
        @click="toggle(pinnedDirectory)"
        @contextmenu.prevent="openFolderMenu($event, pinnedDirectory)" @keydown="menuKey($event, pinnedDirectory)">
        <ChevronDown :size="14" aria-hidden="true" />
        <FolderOpen :size="17" aria-hidden="true" />
        <span class="queue-folder-label"><strong>{{ pinnedDirectory.name }}</strong><small>{{ pinnedDirectory.path }}</small></span>
      </button>
    </div>
    <table v-if="layout === 'list'" class="workspace-queue" aria-labelledby="workspace-files">
      <thead>
        <tr>
          <th v-for="column in columns" :key="column.key" scope="col" :class="`queue-${column.key}`"
            :aria-sort="sorting?.key === column.key ? sorting.direction : undefined">
            <button v-if="column.key === 'name' || column.key === 'modified' || column.key === 'size'" type="button" @click="sortBy(column.key)">
              {{ column.label }}
              <component :is="sorting.direction === 'ascending' ? ArrowUp : ArrowDown"
                v-if="sorting?.key === column.key" :size="13" aria-hidden="true" />
            </button>
            <span v-else class="queue-column-label">{{ column.label }}</span>
          </th>
        </tr>
      </thead>
      <tbody>
        <template v-for="row in rows" :key="row.key">
          <tr v-if="row.kind === 'folder'" :data-queue-row="row.key" class="queue-folder" :style="{ '--queue-depth': row.depth }"
            :class="{ 'queue-selected': selected.has(row.key) }" :draggable="!disabled"
            @dragstart="dragFolder($event, row.folder)" @dragend="emit('dragEnd')"
            @click="selectFolder($event, row.folder)" @mousedown.shift.prevent
            @contextmenu.prevent="openFolderMenu($event, row.folder)" @keydown="menuKey($event, row.folder)">
            <td class="queue-name">
              <div class="queue-folder-entry">
              <button type="button" class="queue-folder-toggle" :disabled="disabled" :aria-expanded="row.expanded"
                :aria-label="`${row.expanded ? '收起' : '展开'}目录 ${row.folder.name}`" @click.stop="toggleFolder(row)">
                <component :is="row.expanded ? ChevronDown : ChevronRight" :size="14" aria-hidden="true" />
              </button>
              <button type="button" class="queue-entry" :disabled="disabled" :aria-pressed="selected.has(row.key)" :aria-busy="row.loading"
                :title="row.folder.path" @dblclick.stop="toggleFolder(row)">
                <component :is="row.expanded ? FolderOpen : Folder" :size="17" aria-hidden="true" />
                <span class="queue-folder-label">
                  <span>{{ row.folder.name }}</span>
                  <small :title="row.folder.path">{{ row.folder.path }}</small>
                </span>
              </button>
              </div>
            </td>
            <td>{{ folderDate(row.folder) }}</td>
            <td>文件夹</td>
            <td class="queue-size">—</td>
            <td class="queue-folder-actions">
              <button v-if="row.expanded" type="button" :disabled="row.loading" title="刷新目录"
                :aria-label="`刷新目录 ${row.folder.name}`" @click.stop="refresh(row.folder)">
                <RotateCw :size="14" aria-hidden="true" />
              </button>
            </td>
          </tr>
          <tr v-else-if="row.kind === 'message'" :data-queue-row="row.key" :style="{ '--queue-depth': row.depth }">
            <td colspan="5" class="queue-name">
              <div class="queue-entry queue-leaf" :class="row.error ? 'workspace-alert' : 'workspace-muted'"
                :role="row.error ? 'alert' : 'status'">{{ row.message }}</div>
            </td>
          </tr>
          <tr v-else-if="row.kind === 'file'" :data-queue-row="row.key" class="workspace-muted" :style="{ '--queue-depth': row.depth }"
            tabindex="0" @contextmenu.prevent="openMenu($event, { path: row.file.path })"
            @keydown="menuKey($event, { path: row.file.path })">
            <td class="queue-name">
              <div class="queue-entry queue-leaf" :title="row.file.path">
                <File :size="17" aria-hidden="true" /><span>{{ row.file.name }}</span>
              </div>
            </td>
            <td>{{ row.file.modifiedAt ? dates.format(new Date(row.file.modifiedAt)) : '—' }}</td>
            <td>{{ row.file.name.split('.').slice(1).at(-1)?.toUpperCase() || '文件' }}</td>
            <td class="queue-size">{{ formatSize(row.file.sizeBytes) }}</td>
            <td>未在队列</td>
          </tr>
          <tr v-else :data-queue-row="row.key" :style="{ '--queue-depth': row.depth }" :class="{ 'queue-selected': highlightedAssets.has(row.asset.assetId) }"
            :draggable="!disabled" @dragstart="startAssetDrag($event, row.asset.assetId)" @dragend="emit('dragEnd')"
            @click="select($event, row.asset.assetId)" @contextmenu.prevent="openAssetMenu($event, row.asset.assetId)"
            @keydown="menuKey($event, row.asset.assetId)">
            <td class="queue-name queue-asset-name">
              <button type="button" class="queue-entry queue-leaf" :disabled="disabled"
                :aria-current="row.asset.assetId === selectedId ? 'true' : undefined" :aria-pressed="highlightedAssets.has(row.asset.assetId)"
                :aria-busy="row.asset.assetId === loadingPhotoId || undefined"
                :data-asset-id="row.asset.assetId" :title="row.asset.originalName">
                <component :is="row.asset.mediaKind === 'video' ? FileVideo : FileImage" :size="17" aria-hidden="true" />
                <span>{{ row.asset.originalName }}</span>
              </button>
              <progress v-if="row.asset.assetId === loadingPhotoId" class="queue-image-progress"
                :aria-label="`正在加载 ${row.asset.originalName} 的预览`" />
            </td>
            <td>{{ row.asset.modifiedAt ? dates.format(new Date(row.asset.modifiedAt)) : '—' }}</td>
            <td>{{ row.asset.extension.replace(/^\./, '').toUpperCase() }} {{ row.asset.mediaKind === 'video' ? '视频' : '照片' }}</td>
            <td class="queue-size" :title="row.asset.sizeBytes == null ? undefined : `${numbers.format(row.asset.sizeBytes)} 字节`">
              {{ formatSize(row.asset.sizeBytes) }}
            </td>
            <td :class="{ 'workspace-alert': !row.asset.available }">{{ row.asset.available ? '可用' : '原件不可用' }}</td>
          </tr>
        </template>
      </tbody>
    </table>
    <div v-else class="workspace-queue-gallery" :class="`queue-gallery-${layout}`" aria-labelledby="workspace-files">
      <template v-for="row in galleryRows" :key="row.key">
        <div v-if="row.kind === 'cards'" :data-queue-row="row.items[0]?.key" class="queue-gallery-cards" :style="{ '--queue-depth': row.depth }">
          <button v-for="{ asset } in row.items" :key="asset.assetId" type="button" class="queue-card"
            :draggable="!disabled" @dragstart="startAssetDrag($event, asset.assetId)" @dragend="emit('dragEnd')"
            :disabled="disabled" :aria-current="asset.assetId === selectedId ? 'true' : undefined"
            :aria-pressed="highlightedAssets.has(asset.assetId)" :data-asset-id="asset.assetId" :title="asset.originalName"
            @click="select($event, asset.assetId)" @contextmenu.prevent="openAssetMenu($event, asset.assetId)"
            @keydown="menuKey($event, asset.assetId)">
            <QueueThumbnail :asset="asset" :preview-loading="asset.assetId === loadingPhotoId" />
            <span class="queue-card-name">{{ asset.originalName }}</span>
          </button>
        </div>
        <div v-else-if="row.kind === 'folder'" :data-queue-row="row.key" class="queue-gallery-folder" :style="{ '--queue-depth': row.depth }"
          :class="{ 'queue-selected': selected.has(row.key) }" :draggable="!disabled"
          @dragstart="dragFolder($event, row.folder)" @dragend="emit('dragEnd')"
          @click="selectFolder($event, row.folder)" @mousedown.shift.prevent
          @contextmenu.prevent="openFolderMenu($event, row.folder)" @keydown="menuKey($event, row.folder)">
          <div class="queue-folder-entry">
            <button type="button" class="queue-folder-toggle" :disabled="disabled" :aria-expanded="row.expanded"
              :aria-label="`${row.expanded ? '收起' : '展开'}目录 ${row.folder.name}`" @click.stop="toggleFolder(row)">
              <component :is="row.expanded ? ChevronDown : ChevronRight" :size="14" aria-hidden="true" />
            </button>
            <button type="button" class="queue-entry" :disabled="disabled" :aria-pressed="selected.has(row.key)" :aria-busy="row.loading"
              :title="row.folder.path" @dblclick.stop="toggleFolder(row)">
              <component :is="row.expanded ? FolderOpen : Folder" :size="17" aria-hidden="true" />
              <span class="queue-folder-label">
                <span>{{ row.folder.name }}</span>
                <small :title="row.folder.path">{{ row.folder.path }}</small>
              </span>
            </button>
          </div>
          <span v-if="folderDate(row.folder) !== '—'" class="queue-gallery-folder-date">{{ folderDate(row.folder) }}</span>
          <button v-if="row.expanded" type="button" class="queue-folder-refresh" :disabled="row.loading" title="刷新目录"
            :aria-label="`刷新目录 ${row.folder.name}`" @click.stop="refresh(row.folder)">
            <RotateCw :size="14" aria-hidden="true" />
          </button>
        </div>
        <div v-else-if="row.kind === 'file'" :data-queue-row="row.key" class="queue-gallery-file workspace-muted"
          :style="{ '--queue-depth': row.depth }" :title="row.file.path" tabindex="0"
          @contextmenu.prevent="openMenu($event, { path: row.file.path })" @keydown="menuKey($event, { path: row.file.path })">
          <File :size="16" aria-hidden="true" /><span>{{ row.file.name }}</span><small>未在队列</small>
        </div>
        <div v-else :data-queue-row="row.key" class="queue-gallery-message" :style="{ '--queue-depth': row.depth }"
          :class="row.error ? 'workspace-alert' : 'workspace-muted'" :role="row.error ? 'alert' : 'status'">
          {{ row.message }}
        </div>
      </template>
    </div>
  </div>
  <Teleport to=".workspace-app">
    <div v-if="menu" ref="menuPanel" class="workspace-queue-menu" role="group" aria-label="队列操作"
      :style="{ left: `${menu.x}px`, top: `${menu.y}px` }" @focusout="leaveMenu" @keydown="selectAll"
      @keydown.esc.stop.prevent="closeMenu">
      <button type="button" :disabled="disabled || !containingFolderTarget(menu.target)" @click="openContainingFolder">
        <FolderOpen :size="16" aria-hidden="true" />打开所在文件夹
      </button>
      <button v-if="'ids' in menu.target" type="button" @click="addToBatch">
        <ListPlus :size="16" aria-hidden="true" />添加到批量处理
      </button>
      <button v-if="'ids' in menu.target || 'root' in menu.target" type="button" @click="removeSelected">
        <FileMinus :size="16" aria-hidden="true" />
        {{ 'ids' in menu.target && menu.target.folders?.length ? `从队列移除 ${menu.target.folders.length} 个目录及所选素材`
          : 'ids' in menu.target && menu.target.ids.length > 1 ? `从队列移除 ${menu.target.ids.length} 项` : '从队列移除' }}
      </button>
    </div>
  </Teleport>
</template>
