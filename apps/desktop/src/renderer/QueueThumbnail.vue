<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { FileImage, FileVideo } from '@lucide/vue'
import type { Asset } from '../shared/workspace'

const props = defineProps<{
  asset: Pick<Asset, 'assetId' | 'originalName' | 'mediaKind' | 'available' | 'modifiedAt'>
  previewLoading?: boolean
}>()
const host = ref<HTMLElement>()
const url = ref('')
const loading = ref(false)
let observer: IntersectionObserver | undefined
let visible = false
let requested: string | undefined
let generation = 0

function release() {
  ++generation
  url.value = ''
  loading.value = false
  if (requested) void window.mediaLibrary.releasePreview(requested, true)
  requested = undefined
}

async function load() {
  release()
  if (!visible || !props.asset.available) return
  const current = generation
  requested = props.asset.assetId
  loading.value = props.asset.mediaKind === 'photo'
  try {
    const reply = await window.mediaLibrary.preview(requested, 'thumbnail')
    if (current !== generation) return
    if ('result' in reply) url.value = reply.result
    else loading.value = false
  } catch {
    if (current === generation) loading.value = false
  }
}

function thumbnailLoaded(event: Event) {
  if ((event.currentTarget as HTMLImageElement).getAttribute('src') === url.value) loading.value = false
}

function thumbnailFailed(event: Event) {
  if ((event.currentTarget as HTMLImageElement).getAttribute('src') === url.value) release()
}

onMounted(() => {
  if (!host.value) return
  observer = new IntersectionObserver(([entry]) => {
    const nextVisible = Boolean(entry?.isIntersecting)
    if (visible === nextVisible) return
    visible = nextVisible
    if (visible) void load()
    else release()
  }, { root: host.value.closest('.workspace-queue-scroll'), rootMargin: '120px' })
  observer.observe(host.value)
})

watch(() => [props.asset.assetId, props.asset.available, props.asset.modifiedAt], () => void load())
onBeforeUnmount(() => {
  observer?.disconnect()
  release()
})
</script>

<template>
  <span ref="host" class="queue-card-preview">
    <img v-if="url" :key="generation" :src="url" alt="" decoding="async" draggable="false"
      @load="thumbnailLoaded" @error="thumbnailFailed" />
    <component v-else :is="asset.mediaKind === 'video' ? FileVideo : FileImage" :size="30" aria-hidden="true" />
    <span v-if="asset.mediaKind === 'video' && url" class="queue-card-video" aria-hidden="true"><FileVideo :size="15" /></span>
    <span v-if="!asset.available" class="queue-card-unavailable">原件不可用</span>
    <progress v-if="loading || previewLoading" class="queue-image-progress"
      :aria-label="`正在加载 ${asset.originalName} 的图片`" />
  </span>
</template>
