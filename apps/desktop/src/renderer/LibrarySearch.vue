<script setup lang="ts">
import OperationFeedback from './OperationFeedback.vue'
import { ChevronDown, ChevronRight, FolderOpen, Plus, Search, Sparkles, X } from '@lucide/vue'
import { computed, ref, shallowRef, watch, onMounted, onUnmounted } from 'vue'
import { searchDirectoryPaths, searchFilterSchema, type SearchDirectoryItem, type SearchFilter, type SearchItem, type DirectoryIndexProgress } from '../shared/search'
import { mediaLayouts, type MediaLayout } from './mediaLayouts'
import SearchFileList from './SearchFileList.vue'
import AgentSearchProgress from './AgentSearchProgress.vue'
import { directoryGroupKey, groupSearchResults } from './searchGroups'

const props = defineProps<{ defaultDirectory?: string; compact?: boolean }>()
const api = window.mediaLibrary
const directoryKey = 'workspace.search.directories'
const layoutKey = 'workspace.search.layout'
const pathKey = (path: string) => path.replaceAll('/', '\\').replace(/\\+$/, '').toUpperCase()

function savedDirectories() {
  try {
    const parsed = searchDirectoryPaths.min(0).safeParse(JSON.parse(localStorage.getItem(directoryKey) ?? '[]'))
    if (parsed.success) return parsed.data
  } catch { /* Use the draft destination when storage is unavailable. */ }
  return []
}

function hasSavedDirectories() {
  try { return localStorage.getItem(directoryKey) !== null }
  catch { return false }
}

function savedLayout(): MediaLayout {
  try {
    const value = localStorage.getItem(layoutKey)
    if (value === 'medium' || value === 'large') return value
  } catch { /* Use the list when storage is unavailable. */ }
  return 'list'
}

const manualDirectories = ref(savedDirectories())
const explicitDirectories = ref(hasSavedDirectories())
const excludedDefault = ref('')
const directories = computed(() => {
  const path = props.defaultDirectory
  if (explicitDirectories.value || manualDirectories.value.length || !path || excludedDefault.value === pathKey(path)) return manualDirectories.value
  return [path]
})
const layout = ref(props.compact ? 'list' : savedLayout())
const picking = ref(false)
const directoryError = ref('')
const indexing = ref(false)
const indexError = ref('')
const indexProgress = ref<DirectoryIndexProgress>({ phase: 'queued', processed: 0, discovered: 0 })
let indexRequestId = ''
const stopIndexProgress = api.onSearchIndexProgress?.((requestId, progress) => {
  if (requestId === indexRequestId && indexing.value) indexProgress.value = progress
})
const indexPercent = computed(() => indexProgress.value.total && indexProgress.value.total > 0
  ? Math.min(100, Math.floor(indexProgress.value.processed / indexProgress.value.total * 100)) : undefined)
const indexLabel = computed(() => indexProgress.value.phase === 'queued' ? '等待加载目录索引…'
  : indexProgress.value.phase === 'scanning' ? `正在扫描目录，已发现 ${indexProgress.value.discovered} 个素材…`
  : `正在加载目录索引 ${indexProgress.value.processed} / ${indexProgress.value.total ?? 0}${indexPercent.value === undefined ? '' : `（${indexPercent.value}%）`}`)
let indexVersion = 0
let indexWork: Promise<void> = Promise.resolve()

async function indexDirectories(paths: string[]) {
  const version = ++indexVersion
  indexError.value = ''
  indexing.value = paths.length > 0
  indexRequestId = paths.length ? crypto.randomUUID() : ''
  indexProgress.value = { phase: 'queued', processed: 0, discovered: 0 }
  if (!paths.length) return
  try {
    const reply = await api.indexSearchDirectories([...paths], indexRequestId)
    if (version !== indexVersion) return
    if ('error' in reply) throw Error(reply.error.message)
  } catch (error) {
    if (version === indexVersion) indexError.value = error instanceof Error ? error.message : '目录索引加载失败，请重试。'
  } finally {
    if (version === indexVersion) indexing.value = false
  }
}

function retryIndex() { indexWork = indexDirectories(directories.value) }
const locationError = ref('')
const mediaKind = ref<'' | SearchItem['mediaKind']>('')
const query = ref('')
const compactQuery = ref<HTMLInputElement>()
const compactControls = ref<HTMLElement>()
const intelligentMode = ref(false)
const agentSteps = ref<string[]>([])
const agentReasons = ref<Record<string, string>>({})
let agentRequestId = ''
const stopAgentSteps = api.onAgentSearchStep((id, step) => {
  if (id === agentRequestId && searching.value) agentSteps.value.push(step)
})
const results = shallowRef<SearchItem[]>([])
const folderResults = shallowRef<SearchDirectoryItem[]>([])
const intelligentResults = ref(false)
const expandedGroups = ref(new Set<string>())
const folderContents = ref(new Map<string, { items: SearchItem[]; next: string | null; loading: boolean; error: string; loaded: boolean }>())
const matchesMediaKind = (item: SearchItem) => !mediaKind.value || item.mediaKind === mediaKind.value
const visibleFiles = computed(() => results.value.filter(matchesMediaKind))
const resultGroups = computed(() => groupSearchResults([], folderResults.value,
  [...folderContents.value.values()].flatMap((folder) => folder.items.filter(matchesMediaKind))))
const hasResults = computed(() => visibleFiles.value.length > 0 || resultGroups.value.length > 0)
const lastFilter = shallowRef<SearchFilter>({})
const next = ref<string | null>(null)
const searching = ref(false)
const showCompactResults = computed(() => searching.value || hasResults.value ||
  intelligentResults.value && didSearch.value)
const canSearch = computed(() => directories.value.length > 0 && !searching.value &&
  (intelligentMode.value ? Boolean(query.value.trim()) : Boolean(terms(query.value))))
const didSearch = ref(false)
const searchError = ref('')
const unverifiedResults = ref(false)
const showCompactEmpty = computed(() => didSearch.value && !showCompactResults.value && !searchError.value)
let queryVersion = 0
let searchTimer: ReturnType<typeof setTimeout> | undefined
let controlsObserver: ResizeObserver | undefined

function cancelScheduledSearch() {
  clearTimeout(searchTimer)
}

function changeLayout(value: MediaLayout) {
  layout.value = value
  try { localStorage.setItem(layoutKey, value) } catch { /* Keep this layout for the session. */ }
}

function saveDirectories(paths: string[]) {
  explicitDirectories.value = true
  manualDirectories.value = paths
  try { localStorage.setItem(directoryKey, JSON.stringify(paths)) } catch { /* Keep these directories for the session. */ }
}

async function addDirectories() {
  picking.value = true
  directoryError.value = ''
  try {
    const reply = await api.chooseSearchDirectories()
    if ('error' in reply) throw Error(reply.error.message)
    const combined = new Map(manualDirectories.value.map((path) => [pathKey(path), path]))
    for (const path of reply.result) combined.set(pathKey(path), path)
    const defaultPath = props.defaultDirectory && pathKey(props.defaultDirectory)
    const addsDefault = defaultPath && combined.has(defaultPath)
    if (combined.size > 50) throw Error('最多可添加 50 个搜索目录。')
    if (addsDefault) excludedDefault.value = ''
    if (reply.result.length) saveDirectories([...combined.values()])
  } catch (error) {
    directoryError.value = error instanceof Error ? error.message : '无法添加搜索目录。'
  } finally {
    picking.value = false
  }
}

function removeDirectory(path: string) {
  const key = pathKey(path)
  if (props.defaultDirectory && key === pathKey(props.defaultDirectory)) excludedDefault.value = key
  saveDirectories(directories.value.filter((item) => pathKey(item) !== key))
  directoryError.value = ''
}

function terms(value: string) {
  const tokens = value.split(/[,，、\r\n]+/).map((term) => term.trim()).filter(Boolean)
  return tokens.length ? tokens : undefined
}

function readFilter(intelligent: boolean, keyword?: string) {
  const keywordTerms = keyword && terms(keyword)
  return searchFilterSchema.safeParse({
    resultKind: intelligent ? 'files' : 'all',
    directoryPaths: directories.value,
    includeSubdirectories: true,
    ...(keywordTerms ? { keywordTerms } : {})
  })
}

function clearResults() {
  agentRequestId = ''
  agentSteps.value = []
  agentReasons.value = {}
  results.value = []
  folderResults.value = []
  expandedGroups.value = new Set()
  folderContents.value = new Map()
  next.value = null
  unverifiedResults.value = false
  locationError.value = ''
}

async function runSearch(filter: SearchFilter, intelligent = false) {
  const version = ++queryVersion
  const append = Boolean(filter.cursor)
  if (!append) {
    clearResults()
    intelligentResults.value = intelligent
  }
  didSearch.value = true
  searching.value = true
  searchError.value = ''
  try {
    await indexWork
    if (version !== queryVersion) return
    if (intelligent) {
      agentRequestId = crypto.randomUUID()
      const reply = await api.intelligentSearch(query.value.trim(), { ...filter, resultKind: 'files' }, agentRequestId)
      if (version !== queryVersion) return
      if ('error' in reply) throw Error(reply.error.message)
      results.value = reply.result.items
      agentReasons.value = Object.fromEntries((reply.result.reasons ?? []).map((item) => [item.assetId, item.reason]))
      unverifiedResults.value = Boolean(reply.result.warning)
    } else {
      const reply = await api.filterAssets(filter)
      if (version !== queryVersion) return
      if ('error' in reply) throw Error(reply.error.message)
      results.value = append ? [...results.value, ...reply.result.items] : reply.result.items
      if (!append) folderResults.value = reply.result.directories ?? []
      next.value = reply.result.nextCursor
      if (!append) lastFilter.value = filter
    }
  } catch (error) {
    if (version === queryVersion) searchError.value = error instanceof Error ? error.message : '无法搜索素材，请重试。'
  } finally {
    if (version === queryVersion) searching.value = false
  }
}

function submit() {
  cancelScheduledSearch()
  if (!canSearch.value) return
  const intelligent = intelligentMode.value
  const filter = readFilter(intelligent, intelligent ? undefined : query.value)
  if (!filter.success) {
    searchError.value = '最多可输入 12 个关键词，每个词最多 240 个字符。'
    return
  }
  void runSearch(filter.data, intelligent)
}

function submitIntelligentOnEnter(event: KeyboardEvent) {
  if (!intelligentMode.value || event.isComposing || event.keyCode === 229 || event.shiftKey) return
  event.preventDefault()
  if (!event.repeat) submit()
}

function resetSearch() {
  cancelScheduledSearch()
  ++queryVersion
  searching.value = false
  didSearch.value = false
  searchError.value = ''
  clearResults()
}

async function openDirectory(path: string) {
  const version = queryVersion
  locationError.value = ''
  try {
    const reply = await api.openSearchDirectory(path)
    if ('error' in reply) throw Error(reply.error.message)
  } catch (error) {
    if (version === queryVersion) locationError.value = error instanceof Error ? error.message : '无法打开目录。'
  }
}

function toggleGroup(group: ReturnType<typeof groupSearchResults>[number]) {
  if (expandedGroups.value.has(group.key)) expandedGroups.value.delete(group.key)
  else {
    expandedGroups.value.add(group.key)
    if (group.matchedDirectory && !group.items.length && !folderContents.value.get(group.key)?.loaded)
      void loadFolder(group.path)
  }
}

async function loadFolder(path: string, append = false) {
  const key = directoryGroupKey(path)
  const previous = folderContents.value.get(key)
  if (previous?.loading || (append && !previous?.next)) return
  const version = queryVersion
  folderContents.value.set(key, { items: previous?.items ?? [], next: previous?.next ?? null, loading: true, error: '', loaded: false })
  try {
    const reply = await api.filterAssets({ directoryPaths: [path], resultKind: 'files', includeSubdirectories: false,
      ...(append && previous?.next ? { cursor: previous.next } : {}) })
    if (version !== queryVersion) return
    if ('error' in reply) throw Error(reply.error.message)
    folderContents.value.set(key, { items: [...(append ? previous?.items ?? [] : []), ...reply.result.items],
      next: reply.result.nextCursor, loading: false, error: '', loaded: true })
  } catch (error) {
    if (version === queryVersion) folderContents.value.set(key, { items: previous?.items ?? [], next: previous?.next ?? null,
      loading: false, error: error instanceof Error ? error.message : '无法读取目录内素材。', loaded: false })
  }
}

function more() {
  if (next.value && !searching.value) void runSearch({ ...lastFilter.value, cursor: next.value })
}

async function reveal(item: SearchItem) {
  const version = queryVersion
  locationError.value = ''
  try {
    const reply = await api.revealAsset(item.assetId)
    if ('error' in reply && version === queryVersion) {
      if (reply.error.code === 'SOURCE_UNAVAILABLE') {
        results.value = results.value.map((result) => result.assetId === item.assetId ? { ...result, available: false } : result)
        for (const folder of folderContents.value.values()) {
          folder.items = folder.items.map((result) => result.assetId === item.assetId ? { ...result, available: false } : result)
        }
      }
      throw Error(reply.error.message)
    }
  } catch (error) {
    if (version === queryVersion) locationError.value = error instanceof Error ? error.message : '无法定位素材。'
  }
}

function scheduleKeywordSearch(keyword: string) {
  if (intelligentMode.value) return
  resetSearch()
  if (directories.value.length && terms(keyword)) searchTimer = setTimeout(submit, 300)
}

watch(() => query.value.trim(), scheduleKeywordSearch)
watch(intelligentMode, cancelScheduledSearch)
watch(directories, (paths, previous) => {
  if (previous && previous.length === paths.length && previous.every((path, index) => path === paths[index])) return
  resetSearch()
  indexWork = indexDirectories(paths)
}, { immediate: true })
function syncDirectories(event: StorageEvent) {
  if (event.key === directoryKey || event.key === null) {
    explicitDirectories.value = hasSavedDirectories()
    manualDirectories.value = savedDirectories()
  }
}

function focusQuery() {
  compactQuery.value?.focus()
}

function resizeCompactWindow() {
  if (props.compact && compactControls.value) {
    const height = Math.ceil(compactControls.value.getBoundingClientRect().height)
    if (height > 0) api.resizeQuickSearch(height, showCompactResults.value)
  }
}

watch(showCompactResults, resizeCompactWindow, { flush: 'post' })
onMounted(() => {
  window.addEventListener('storage', syncDirectories)
  if (props.compact) {
    if (compactControls.value) {
      controlsObserver = new ResizeObserver(resizeCompactWindow)
      controlsObserver.observe(compactControls.value)
    }
    window.addEventListener('focus', focusQuery)
    focusQuery()
  }
})
onUnmounted(() => {
  stopIndexProgress?.()
  ++indexVersion
  controlsObserver?.disconnect()
  cancelScheduledSearch()
  ++queryVersion
  stopAgentSteps()
  window.removeEventListener('storage', syncDirectories)
  window.removeEventListener('focus', focusQuery)
})
</script>

<template>
  <div class="library-search" :class="{ 'library-search-compact': compact }">
    <OperationFeedback :message="directoryError" warning />
    <OperationFeedback :message="searchError" warning />
    <OperationFeedback :message="locationError" warning />
    <div class="library-workarea">
      <div ref="compactControls" class="library-search-controls">
        <form class="library-query-form" :aria-busy="searching" @submit.prevent="submit()">
          <div class="library-query-box" :class="{ 'library-query-agent': intelligentMode }">
            <textarea v-if="intelligentMode && !compact" id="library-query" v-model="query" rows="2" maxlength="2000"
              aria-label="智能搜索提示词" placeholder="按所选目录内素材的创建日期检索" @keydown.enter="submitIntelligentOnEnter"></textarea>
            <input v-else id="library-query" ref="compactQuery" v-model="query" maxlength="2000"
              :aria-label="intelligentMode ? '智能搜索提示词' : '搜索素材'"
              :placeholder="intelligentMode ? '按所选目录内素材的创建日期检索' : '搜索文件名、目录名、备注或日期'"
              @compositionstart="cancelScheduledSearch" @keydown.enter="submitIntelligentOnEnter">
            <button v-if="!compact || intelligentMode" type="submit" class="library-icon-button"
              :aria-label="intelligentMode ? '开始智能搜索' : '搜索'" :title="intelligentMode ? '开始智能搜索' : '搜索'"
              :disabled="!canSearch">
              <component :is="intelligentMode ? Sparkles : Search" :size="18" aria-hidden="true" />
            </button>
          </div>
          <button type="button" class="library-icon-button library-mode-toggle" :aria-label="intelligentMode ? '普通搜索' : '智能搜索'"
            :title="intelligentMode ? '切换普通搜索模式' : '切换智能搜索模式'" data-library-intelligent
            :aria-pressed="intelligentMode" :disabled="searching" @click="intelligentMode = !intelligentMode; if (compact) focusQuery()">
            <component :is="intelligentMode ? Search : Sparkles" :size="18" aria-hidden="true" /><span>{{ intelligentMode ? '普通搜索' : '智能搜索' }}</span>
          </button>
        </form>

        <section class="library-directories" :aria-labelledby="compact ? undefined : 'library-directories-heading'"
          :aria-label="compact ? '搜索目录' : undefined">
          <div v-if="!compact" class="workspace-panel-heading">
            <h2 id="library-directories-heading">搜索目录</h2>
          </div>
          <div v-if="directories.length" class="library-directory-list">
            <ul>
              <li v-for="path in directories" :key="path">
                <span :title="path">{{ path }}</span>
                <button type="button" class="library-icon-button" :aria-label="`移除目录 ${path}`" title="移除目录"
                  @click="removeDirectory(path)"><X :size="14" aria-hidden="true" /></button>
              </li>
            </ul>
            <button type="button" class="library-icon-button library-directory-add"
              aria-label="添加目录" title="添加目录" data-search-directory-add :disabled="picking" @click="addDirectories">
              <Plus :size="16" aria-hidden="true" />
            </button>
          </div>
          <button v-else type="button" class="workspace-text-button"
            data-search-directory-add :disabled="picking" @click="addDirectories">请添加查找目录</button>
          <div v-if="indexing" class="library-index-status" role="status" aria-live="polite">
            <span>{{ indexLabel }}</span>
            <progress v-if="indexProgress.total != null" class="library-index-progress" :value="indexPercent ?? 0" max="100" aria-label="目录索引加载进度"
              :aria-valuetext="indexLabel" />
          </div>
          <div v-else-if="indexError" class="library-index-status workspace-alert" role="alert">
            {{ indexError }} <button type="button" class="workspace-text-button" @click="retryIndex">重试</button>
          </div>
        </section>
        
        <p v-if="compact && showCompactEmpty" class="library-quick-status" role="status">无匹配结果</p>
      </div>

      <section v-if="!compact || showCompactResults" class="library-results"
        :aria-labelledby="compact ? undefined : 'library-results-heading'" :aria-label="compact ? '搜索结果' : undefined">
        <div v-if="!compact" class="workspace-panel-heading library-results-heading">
          <h2 id="library-results-heading">搜索结果</h2>
          <span role="status">{{ searching ? '正在搜索…' : `${visibleFiles.length} 个文件 · ${resultGroups.length} 个目录` }}</span>
          <select id="library-kind" v-model="mediaKind" aria-label="筛选搜索结果的素材类型">
            <option value="">全部类型</option>
            <option value="photo">照片</option>
            <option value="video">视频</option>
          </select>
          <div v-if="!compact" class="workspace-queue-layouts" role="group" aria-label="搜索结果展示方式">
            <button v-for="option in mediaLayouts" :key="option.key" type="button"
              :aria-pressed="layout === option.key" @click="changeLayout(option.key)">
              <component :is="option.icon" :size="15" aria-hidden="true" />{{ option.label }}
            </button>
          </div>
        </div>
        <div class="library-results-scroll" tabindex="0" role="region" aria-label="搜索结果列表">
        <p v-if="compact && searching && !intelligentResults && !hasResults" class="library-quick-status" role="status">搜索中…</p>
        
        
        <AgentSearchProgress v-if="intelligentResults && searching" :key="agentRequestId" :steps="agentSteps" />
        <SearchFileList v-if="visibleFiles.length" :items="visibleFiles" :layout="layout" :reasons="agentReasons" @reveal="reveal" />
        <p v-else-if="mediaKind && (results.length || next) && !searching && !searchError" class="workspace-muted">
          已加载的结果中没有{{ mediaKind === 'photo' ? '照片' : '视频' }}。
        </p>
        <button v-if="next" type="button" :disabled="searching" @click="more">加载更多</button>
        <ul v-if="resultGroups.length" class="library-directory-groups">
          <li v-for="(group, index) in resultGroups" :key="group.key" class="library-directory-group" :data-library-directory="group.path">
            <div class="library-directory-heading">
              <button type="button" class="library-directory-toggle" :aria-expanded="expandedGroups.has(group.key)"
                :aria-controls="`library-directory-files-${index}`" @click="toggleGroup(group)">
                <component :is="expandedGroups.has(group.key) ? ChevronDown : ChevronRight" :size="16" aria-hidden="true" />
                <FolderOpen :size="18" aria-hidden="true" />
                <span class="library-directory-label"><strong>{{ group.name }}</strong><small :title="group.path">{{ group.path }}</small></span>
                <span class="library-directory-count">{{ group.matchedDirectory && !group.items.length && !folderContents.get(group.key)?.loaded ? '展开查看文件' : `${group.items.length} 个文件` }}</span>
              </button>
              <button type="button" class="library-icon-button" :disabled="!group.path" :aria-label="`打开目录 ${group.name}`" title="打开目录"
                @click="openDirectory(group.path)"><FolderOpen :size="16" aria-hidden="true" /></button>
            </div>
            <div v-if="expandedGroups.has(group.key)" :id="`library-directory-files-${index}`" class="library-directory-files">
              <p v-if="folderContents.get(group.key)?.loading" class="workspace-muted" role="status">正在读取目录内素材…</p>
              <OperationFeedback :message="folderContents.get(group.key)?.error" warning action-label="重试" @action="loadFolder(group.path)" />
              <p v-if="!group.items.length && !folderContents.get(group.key)?.loading && !folderContents.get(group.key)?.error" class="workspace-muted">此目录下没有可显示的素材文件。</p>
              <SearchFileList v-if="group.items.length" :items="group.items" :layout="layout" @reveal="reveal" />
              <button v-if="folderContents.get(group.key)?.next" type="button" :disabled="folderContents.get(group.key)?.loading"
                @click="loadFolder(group.path, true)">加载此目录更多文件</button>
            </div>
          </li>
        </ul>
        <p v-else-if="!results.length && !searching && !searchError && didSearch"
          class="workspace-muted" data-library-empty>没有符合条件的文件或目录。</p>
        </div>
      </section>
    </div>
  </div>
</template>
