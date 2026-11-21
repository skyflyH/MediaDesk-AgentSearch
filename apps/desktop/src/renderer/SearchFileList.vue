<script setup lang="ts">
import { FolderOpen } from '@lucide/vue'
import type { SearchItem } from '../shared/search'
import type { MediaLayout } from './mediaLayouts'
import QueueThumbnail from './QueueThumbnail.vue'

defineProps<{ items: SearchItem[]; layout: MediaLayout; reasons?: Record<string, string> }>()
const emit = defineEmits<{ reveal: [item: SearchItem] }>()
const dateFormatter = new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium' })

function focusNeighbor(event: KeyboardEvent) {
  if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return
  const buttons = Array.from((event.currentTarget as HTMLElement).querySelectorAll('button'))
  const index = buttons.indexOf(event.target as HTMLButtonElement)
  if (index < 0) return
  event.preventDefault()
  buttons[index + (event.key === 'ArrowDown' ? 1 : -1)]?.focus()
}
</script>

<template>
  <ul class="library-result-list" :class="`library-results-${layout}`" @keydown="focusNeighbor">
    <li v-for="item in items" :key="item.assetId" class="library-result" :data-library-result="item.assetId">
      <QueueThumbnail :asset="{ ...item, originalName: item.fileName }" />
      <div class="library-result-metadata">
        <strong :title="item.fileName">{{ item.fileName }}</strong>
        <p v-if="reasons?.[item.assetId]" class="library-match-reason">{{ reasons[item.assetId] }}</p>
        <span v-if="item.remarkSummary" :title="item.remarkSummary">备注：{{ item.remarkSummary }}</span>
        <time :datetime="item.createdAt">{{ dateFormatter.format(new Date(item.createdAt)) }}</time>
        <span data-library-path :title="item.sourcePath">{{ item.sourcePath || '来源路径未记录' }}</span>
      </div>
      <button type="button" class="library-icon-button" :aria-label="`打开 ${item.fileName} 所在文件夹`"
        title="打开所在文件夹" data-library-reveal @click="emit('reveal', item)">
        <FolderOpen :size="16" aria-hidden="true" />
      </button>
    </li>
  </ul>
</template>
