<script setup lang="ts">
import { Check, Save, Sparkles } from '@lucide/vue'
import OperationFeedback from './OperationFeedback.vue'
import type { Asset, DraftData, DraftChanges, AssetDraft } from '../shared/workspace'
import type { ArchiveDirectory } from '../shared/archive'

defineProps<{
  asset?: Asset; edited: DraftData | null; loading: boolean; disabled: boolean; activeArchive: boolean
  saveError: string; singleEditName: boolean; singleEditRemark: boolean; savingSourceProperties: boolean
  generatingName: boolean; generatingRemark: boolean; nameMessage: string; remarkMessage: string
  suggestions: NonNullable<AssetDraft['data']['suggestions']>; directories: ArchiveDirectory[]
}>()
const emit = defineEmits<{
  edit: [changes: DraftChanges]; 'update:singleEditName': [value: boolean]; 'update:singleEditRemark': [value: boolean]
  generate: [kind: 'name' | 'remark']; 'retry-save': []; 'save-properties': []; archive: []
  'choose-directory': []; 'select-directory': [event: Event]
}>()
function inputValue(event: Event) {
  return (event.target as HTMLInputElement | HTMLTextAreaElement).value
}
function editRemark(event: Event) {
  const value = inputValue(event)
  emit('edit', { remark: { intent: value.length ? 'set' : 'clear', value } })
}
</script>

<template>
  <template v-if="asset && edited">
    <p v-if="!asset.available" class="workspace-alert">原件当前不可用，仍可编辑草稿。</p>
    <OperationFeedback :message="saveError" warning action-label="重试保存" @action="emit('retry-save')" />
    <fieldset :disabled="disabled || !!activeArchive">
      <div class="workspace-field-heading">
        <label class="workspace-batch-option"><input :checked="singleEditName" @change="emit('update:singleEditName', ($event.target as HTMLInputElement).checked)" type="checkbox"
          aria-label="修改文件名" :aria-expanded="singleEditName" aria-controls="workspace-name-fields">文件名</label>
        <button type="button" data-generate-name class="workspace-field-icon"
          :title="generatingName ? '正在生成名称建议…' : '生成名称建议'"
          :aria-label="generatingName ? '正在生成名称建议' : '生成名称建议'"
          :aria-busy="generatingName" :disabled="!singleEditName || generatingName"
          @click="emit('generate', 'name')"><Sparkles :size="16" aria-hidden="true" /></button>
      </div>
      <div v-if="singleEditName" id="workspace-name-fields">
        <div class="workspace-name">
          <input id="workspace-name" aria-label="文件名" :value="edited.name" maxlength="255" @input="emit('edit', { name: inputValue($event) })">
          <span aria-label="扩展名">{{ asset.extension }}</span>
        </div>
        <ul v-if="suggestions.name?.names?.length" class="workspace-name-suggestions" aria-label="建议名称">
          <li v-for="name in suggestions.name.names.slice(0, 3)" :key="name" class="workspace-suggestion-row">
            <span>{{ name }}</span>
            <button type="button" class="workspace-field-icon" title="采纳此名称"
              :aria-label="`采纳名称 ${name}`" @click="emit('edit', { name })">
              <Check :size="16" aria-hidden="true" />
            </button>
          </li>
        </ul>
        <OperationFeedback :message="nameMessage" :warning="true" />
        <p v-if="suggestions.name?.status === 'ready' && !suggestions.name.names?.length" class="workspace-muted">暂无名称建议。</p>
        </div>
      <div class="workspace-field-heading">
        <label class="workspace-batch-option"><input :checked="singleEditRemark" @change="emit('update:singleEditRemark', ($event.target as HTMLInputElement).checked)" type="checkbox"
          aria-label="修改备注" :aria-expanded="singleEditRemark" aria-controls="workspace-remark-fields">备注</label>
        <button type="button" data-generate-remark class="workspace-field-icon"
          :aria-label="generatingRemark ? '正在生成备注建议' : '生成备注建议'"
          :title="generatingRemark ? '正在生成备注建议…' : '用当前备注生成建议（发送给已配置的模型）'"
          :aria-busy="generatingRemark" :disabled="!singleEditRemark || generatingRemark || !edited.remark.value.trim()"
          @click="emit('generate', 'remark')"><Sparkles :size="16" aria-hidden="true" /></button>
      </div>
      <div v-if="singleEditRemark" id="workspace-remark-fields">
        <textarea id="workspace-remark" aria-label="备注" :value="edited.remark.value" rows="4" maxlength="4000" @input="editRemark"></textarea>
        <div v-if="suggestions.remark?.remark" class="workspace-suggestion-row">
          <span data-remark-suggestion>{{ suggestions.remark.remark }}</span>
          <button type="button" data-apply-remark class="workspace-field-icon" title="采纳此备注" aria-label="采纳备注"
            @click="emit('edit', { remark: { intent: 'set', value: suggestions.remark.remark } })">
            <Check :size="16" aria-hidden="true" />
          </button>
        </div>
        <OperationFeedback :message="remarkMessage" :warning="true" />
        <p v-if="suggestions.remark?.status === 'ready' && !suggestions.remark.remark" class="workspace-muted">暂无备注建议。</p>
        </div>
      <div class="workspace-actions">
        <button type="button" data-save-source-properties class="workspace-primary workspace-folder-button"
          :disabled="!asset.available || (!singleEditName && !singleEditRemark)"
          @click="emit('save-properties')">
          <Save :size="16" aria-hidden="true" /> {{ savingSourceProperties ? '正在保存属性…' : '保存属性' }}
        </button>
      </div>
      <label for="workspace-directory">另存为</label>
      <select id="workspace-directory" :value="edited.directoryId ?? ''" @change="emit('select-directory', $event)">
        <option value="">请选择目录</option>
        <option v-for="directory in directories" :key="directory.directoryId" :value="directory.directoryId"
          :disabled="!directory.available">{{ directory.fullPath }}{{ directory.available ? '' : '（不可用）' }}</option>
      </select>
      <div class="workspace-actions">
        <button type="button" class="workspace-text-button" @click="emit('choose-directory')">选择其它目录...</button>
      </div>
      <div class="workspace-actions">
        <button type="button" data-save-as class="workspace-primary workspace-folder-button"
          :disabled="!asset.available || !directories.some((item) => item.directoryId === edited?.directoryId && item.available)"
          @click="emit('archive')"><Save :size="16" aria-hidden="true" />保存</button>
      </div>
    </fieldset>
  </template>
  <p v-else class="workspace-muted">{{ loading ? '正在读取队列…' : '选择一项素材开始整理' }}</p>
</template>
