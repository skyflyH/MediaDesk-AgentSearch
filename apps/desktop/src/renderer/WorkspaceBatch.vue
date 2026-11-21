<script setup lang="ts">
import OperationFeedback from './OperationFeedback.vue'
import { computed, nextTick, onUnmounted, reactive, ref, watch } from 'vue'
import { Check, Pencil, Save, Sparkles, Trash2, X } from '@lucide/vue'
import type { ArchiveDirectory } from '../shared/archive'
import type { Asset, DraftChanges } from '../shared/workspace'
import { isControlA } from '../shared/keyboard'

const props = defineProps<{
  items: Array<Asset & { draftName?: string }>; directories: ArchiveDirectory[]; disabled: boolean; saving: boolean
  errors: Map<string, string>; message: string; blocked: boolean
  renameItem: (id: string, name: string) => Promise<boolean>
}>()
const emit = defineEmits<{
  remove: [ids: string[]]; clear: []; apply: [changes: DraftChanges, names: Record<string, string>]
  copy: [directoryId: string, changes: DraftChanges, names: Record<string, string>]; chooseDirectory: []
  preview: [id: string]
}>()

const editRemark = ref(false)
const editNames = ref(false)
const emptyValues = () => ({ name: '', remark: '', directoryId: '' })
const values = reactive(emptyValues())
const validation = ref('')
const suggestingRemark = ref(false)
const remarkSuggestion = ref('')
const remarkFeedback = ref('')
const remarkError = ref(false)
const selected = ref(new Set<string>())
const list = ref<HTMLElement>()
const nameInput = ref<HTMLInputElement>()
const editingId = ref('')
const individualName = ref('')
const nameError = ref('')
const savingName = ref(false)
const hasChanges = computed(() => editRemark.value || editNames.value)
let anchor = ''
let remarkRequest = 0

function extension(item: Asset): string {
  return item.extension.startsWith('.') ? item.extension : `.${item.extension}`
}

function baseName(item: typeof props.items[number]): string {
  if (item.draftName !== undefined) return item.draftName
  const suffix = extension(item)
  return item.originalName.toLowerCase().endsWith(suffix.toLowerCase())
    ? item.originalName.slice(0, -suffix.length) : item.originalName
}

function validName(name: string, suffix: string): boolean {
  return Boolean(name.trim()) && name.length + suffix.length <= 255 && !/[<>:"/\\|?*\x00-\x1f]|[. ]$/.test(name) &&
    !/^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[1-9¹²³])(?:\.|$)/i.test(name)
}

function selectItem(event: MouseEvent, id: string, toggle = false) {
  if (props.disabled) return
  if (event.shiftKey) {
    const ids = props.items.map((item) => item.assetId)
    const from = Math.max(0, ids.indexOf(anchor || id))
    const to = ids.indexOf(id)
    const range = ids.slice(Math.min(from, to), Math.max(from, to) + 1)
    selected.value = new Set(event.ctrlKey || event.metaKey ? [...selected.value, ...range] : range)
  } else {
    anchor = id
    const next = new Set(event.ctrlKey || event.metaKey || toggle ? selected.value : [])
    if (next.has(id)) next.delete(id)
    else next.add(id)
    selected.value = next
  }
  list.value?.focus()
}

function previewItem(event: MouseEvent, id: string) {
  if (props.disabled || (event.target as HTMLElement).closest('input, .workspace-batch-name-editor, .workspace-field-icon')) return
  emit('preview', id)
}

function selectionKeys(event: KeyboardEvent) {
  const target = event.target as HTMLElement
  if (target.closest('input:not([type="checkbox"]), textarea')) return
  if (isControlA(event)) {
    event.preventDefault()
    if (!props.disabled) selected.value = new Set(props.items.map((item) => item.assetId))
  } else if (event.key === 'Delete' && !props.disabled && selected.value.size) {
    event.preventDefault()
    emit('remove', [...selected.value])
  }
}

async function startRename(item: typeof props.items[number], event: MouseEvent) {
  if (event.ctrlKey || event.metaKey || event.shiftKey) {
    selectItem(event, item.assetId)
    return
  }
  if (props.disabled || savingName.value) return
  editingId.value = item.assetId
  individualName.value = baseName(item)
  nameError.value = ''
  await nextTick()
  nameInput.value?.focus()
  nameInput.value?.select()
}

async function saveName(item: typeof props.items[number]) {
  if (savingName.value || props.disabled) return
  if (!validName(individualName.value, extension(item))) {
    nameError.value = '请输入合法文件名，名称连同扩展名最多 255 个字符。'
    return
  }
  savingName.value = true
  try {
    if (await props.renameItem(item.assetId, individualName.value)) editingId.value = ''
  } catch {
    nameError.value = '名称保存失败，请重试。'
  } finally { savingName.value = false }
}

watch(() => props.items.map((item) => item.assetId), (ids) => {
  selected.value = new Set([...selected.value].filter((id) => ids.includes(id)))
  if (!ids.includes(anchor)) anchor = ''
  if (!ids.includes(editingId.value)) editingId.value = ''
})

function clearRemarkSuggestion() {
  ++remarkRequest
  suggestingRemark.value = false
  remarkSuggestion.value = ''
  remarkFeedback.value = ''
  remarkError.value = false
}

watch([() => values.remark, editRemark, () => props.items.map((item) => item.assetId).join(',')],
  clearRemarkSuggestion, { flush: 'sync' })
onUnmounted(clearRemarkSuggestion)

async function generateRemark() {
  if (props.disabled || !editRemark.value || !values.remark.trim() || suggestingRemark.value) return
  clearRemarkSuggestion()
  const request = remarkRequest
  suggestingRemark.value = true
  try {
    const reply = await window.mediaLibrary.suggestBatchRemark(values.remark)
    if (request !== remarkRequest) return
    if ('error' in reply) throw Error(reply.error.message)
    remarkSuggestion.value = reply.result
    if (!reply.result) remarkFeedback.value = '暂无备注建议。'
  } catch (error) {
    if (request === remarkRequest) {
      remarkError.value = true
      remarkFeedback.value = error instanceof Error ? error.message : '无法生成备注建议。'
    }
  } finally {
    if (request === remarkRequest) suggestingRemark.value = false
  }
}

watch(() => props.items.length, (count) => {
  if (count) return
  editRemark.value = false
  editNames.value = false
  Object.assign(values, emptyValues())
  validation.value = ''
})

function apply(copy = false) {
  if (!props.items.length || (!copy && !hasChanges.value) || props.disabled) return
  validation.value = ''
  const changes: DraftChanges = {}
  const names: Record<string, string> = {}
  if (editNames.value) {
    for (const [index, item] of props.items.entries()) {
      const name = `${values.name.trim()}${index + 1}`
      if (!values.name.trim() || !validName(name, extension(item))) {
        validation.value = '请输入合法的批量名称，编号和扩展名计入 255 个字符限制。'
        return
      }
      names[item.assetId] = name
    }
  }
  if (editRemark.value) changes.remark = { intent: values.remark.length ? 'set' : 'clear', value: values.remark }
  if (copy) {
    if (!values.directoryId) {
      validation.value = '请选择保存目录。'
      return
    }
    if (!props.directories.some((directory) =>
      directory.directoryId === values.directoryId && directory.available)) {
      validation.value = '所选保存目录已不可用，请重新选择。'
      return
    }
  }
  if (copy) emit('copy', values.directoryId, changes, names)
  else emit('apply', changes, names)
}
</script>

<template>
  <div class="workspace-batch">
    <OperationFeedback :message="blocked || saving || message.startsWith('已保存 ') ? '' : message" />
    <p v-if="!items.length" class="workspace-muted">将素材队列中的文件拖到此处，或右键选择「添加到批量处理」。</p>
    <template v-else>
      <div class="workspace-panel-heading">
        <span>{{ items.length }} 项待处理<template v-if="selected.size"> · 已选 {{ selected.size }} 项</template></span>
        <div class="workspace-actions">
          <button v-if="selected.size" type="button" class="workspace-field-icon" title="从批次移除选中素材" aria-label="批量移除选中素材"
            :disabled="disabled" @click="emit('remove', [...selected])"><Trash2 :size="16" aria-hidden="true" /></button>
          <button type="button" class="workspace-field-icon" title="清空批次" aria-label="清空批次"
            :disabled="disabled" @click="emit('clear')"><X :size="16" aria-hidden="true" /></button>
        </div>
      </div>
      <ul ref="list" class="workspace-batch-items" aria-label="批量处理素材" tabindex="0"
        aria-keyshortcuts="Control+A Delete" @keydown="selectionKeys">
        <li v-for="item in items" :key="item.assetId" :class="{ 'batch-item-selected': selected.has(item.assetId) }"
          :data-batch-id="item.assetId" @click="selectItem($event, item.assetId)"
          @dblclick="previewItem($event, item.assetId)" @mousedown.shift.prevent>
          <input type="checkbox" class="workspace-batch-checkbox" :checked="selected.has(item.assetId)"
            :disabled="disabled" :aria-label="`选择 ${baseName(item)}${extension(item)}`"
            @click.stop="selectItem($event, item.assetId, true)">
          <div class="workspace-batch-item-details">
            <div v-if="editingId === item.assetId" class="workspace-batch-name-editor" @click.stop>
              <input :ref="element => nameInput = element as HTMLInputElement" v-model="individualName" aria-label="修改素材文件名" maxlength="255"
                :disabled="disabled || savingName" @keydown.enter.prevent="saveName(item)" @keydown.esc.prevent="editingId = ''">
              <span>{{ extension(item) }}</span>
              <button type="button" class="workspace-field-icon" title="保存名称" aria-label="保存名称" :disabled="disabled || savingName"
                @click="saveName(item)"><Check :size="16" aria-hidden="true" /></button>
              <button type="button" class="workspace-field-icon" title="取消改名" aria-label="取消改名" :disabled="savingName"
                @click="editingId = ''"><X :size="16" aria-hidden="true" /></button>
            </div>
            <button v-else type="button" class="workspace-batch-name" :title="item.sourcePath ?? item.originalName"
              :disabled="disabled" @click.stop="selectItem($event, item.assetId)">{{ baseName(item) }}{{ extension(item) }}</button>
            <OperationFeedback :message="editingId === item.assetId ? nameError : ''" :warning="true" />
            <small v-if="errors.has(item.assetId)" class="workspace-alert">{{ errors.get(item.assetId) }}</small>
          </div>
          <button type="button" class="workspace-field-icon" :disabled="disabled || savingName" title="修改名称"
            :aria-label="`修改名称 ${baseName(item)}${extension(item)}`" @click.stop="startRename(item, $event)">
            <Pencil :size="16" aria-hidden="true" />
          </button>
          <button type="button" class="workspace-field-icon" :disabled="disabled" title="移出批次"
            :aria-label="`移出批次 ${baseName(item)}${extension(item)}`" @click.stop="emit('remove', [item.assetId])">
            <X :size="16" aria-hidden="true" />
          </button>
        </li>
      </ul>
      <form @submit.prevent="apply()">
        <fieldset :disabled="disabled">
          <label class="workspace-batch-option"><input v-model="editNames" type="checkbox" aria-label="批量修改文件名">文件名</label>
          <template v-if="editNames">
            <input v-model="values.name" aria-label="批量文件名" maxlength="250" placeholder="输入统一名称，例如 旅行">
            <p class="workspace-muted">按列表顺序命名为「{{ values.name.trim() || '旅行' }}1、{{ values.name.trim() || '旅行' }}2…」，保留各自扩展名。</p>
          </template>
          <div class="workspace-field-heading">
            <label class="workspace-batch-option"><input v-model="editRemark" type="checkbox" aria-label="批量修改备注">备注</label>
            <button type="button" class="workspace-field-icon" data-batch-generate-remark
              :title="suggestingRemark ? '正在生成备注建议…' : '用批量备注生成建议（发送给已配置的模型）'"
              :aria-label="suggestingRemark ? '正在生成批量备注建议' : '生成批量备注建议'" :aria-busy="suggestingRemark"
              :disabled="!editRemark || !values.remark.trim() || suggestingRemark" @click="generateRemark">
              <Sparkles :size="16" aria-hidden="true" />
            </button>
          </div>
          <textarea v-if="editRemark" v-model="values.remark" aria-label="批量备注" rows="4"
            maxlength="4000" placeholder="留空将清除备注"></textarea>
          <div v-if="remarkSuggestion" class="workspace-suggestion-row">
            <span data-batch-remark-suggestion>{{ remarkSuggestion }}</span>
            <button type="button" class="workspace-field-icon" title="采纳此备注" aria-label="采纳批量备注建议"
              @click="values.remark = remarkSuggestion"><Check :size="16" aria-hidden="true" /></button>
          </div>
          <OperationFeedback :message="remarkFeedback" :warning="remarkError" />
          <OperationFeedback :message="validation" :warning="true" />
          <div class="workspace-actions">
            <button type="submit" class="workspace-primary workspace-folder-button" :disabled="!hasChanges"
              :title="`保存 ${items.length} 项原文件属性`">
              <Save :size="16" aria-hidden="true" />{{ saving ? '正在保存…' : '保存属性' }}
            </button>
          </div>
          <label for="workspace-batch-destination" class="workspace-batch-destination-label">另存为</label>
          <select id="workspace-batch-destination" v-model="values.directoryId" aria-label="批量保存目录">
            <option value="" disabled>请选择保存目录</option>
            <option v-for="directory in directories" :key="directory.directoryId" :value="directory.directoryId"
              :disabled="!directory.available">{{ directory.fullPath }}{{ directory.available ? '' : '（不可用）' }}</option>
          </select>
          <div class="workspace-actions">
            <button type="button" class="workspace-text-button" @click="emit('chooseDirectory')">
              选择其它目录...
            </button>
          </div>
          <div class="workspace-actions">
            <button type="button" class="workspace-primary workspace-folder-button" :disabled="!values.directoryId"
              :title="`复制 ${items.length} 项到保存目录`" @click="apply(true)">
              <Save :size="16" aria-hidden="true" />{{ saving ? '正在处理…' : '保存' }}
            </button>
          </div>
        </fieldset>
      </form>
    </template>
  </div>
</template>
