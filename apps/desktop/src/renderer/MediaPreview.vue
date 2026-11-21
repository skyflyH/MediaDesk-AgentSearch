<script setup lang="ts">
import OperationFeedback from './OperationFeedback.vue'
import { onBeforeUnmount, ref, watch } from 'vue'
import { Minus, Plus } from '@lucide/vue'
import type { PreviewKind } from '../shared/preview'
import { usePhotoTransform } from './usePhotoTransform'

const props = withDefaults(defineProps<{ assetId: string; kind: 'photo' | 'video'; position: number; showReveal?: boolean }>(),
  { showReveal: true })
const emit = defineEmits<{ position: [milliseconds: number]; photoLoading: [assetId?: string] }>()
const api = window.mediaLibrary
const fileUrl = ref('')
const message = ref('')
const folderError = ref('')
const openingFolder = ref(false)
let folderRequest = 0
const busy = ref(false)
const video = ref<HTMLVideoElement>()
const proxy = ref(false)
const imageSize = ref<{ width: number; height: number }>()
const { zoom, dragging, imageStyle, minZoom, maxZoom, zoomStep, setZoom, reset, wheel, startDrag, moveDrag, endDrag } =
  usePhotoTransform(() => props.kind === 'photo' && !!fileUrl.value && !!imageSize.value)
let generation = 0
let timer: ReturnType<typeof setTimeout> | undefined
let lastSaved = 0

function stopPlayer() {
  video.value?.pause()
  video.value?.removeAttribute('src')
  video.value?.load()
  fileUrl.value = ''
}

async function refresh(kind: PreviewKind, request = ++generation) {
  const id = props.assetId
  message.value = ''
  fileUrl.value = ''
  imageSize.value = undefined
  reset()
  busy.value = true
  emit('photoLoading', kind === 'image' ? id : undefined)
  try {
    const reply = await api.preview(id, kind)
    if (request !== generation) {
      if (id !== props.assetId) void api.releasePreview(id)
      return
    }
    if ('error' in reply) throw new Error(reply.error.message)
    fileUrl.value = reply.result
  } catch (error) {
    if (request === generation) {
      emit('photoLoading', undefined)
      message.value = error instanceof Error ? error.message : '无法预览此文件。'
    }
  } finally {
    if (request === generation) busy.value = false
  }
}

function release(id: string) {
  ++generation
  clearTimeout(timer)
  emit('photoLoading', undefined)
  stopPlayer()
  return api.releasePreview(id)
}

watch(() => props.assetId, (id, previous) => {
  ++folderRequest
  folderError.value = ''
  openingFolder.value = false
  if (previous) {
    void release(previous)
    void api.proxy(previous, 'cancel')
  }
  lastSaved = props.position
  proxy.value = false
  void refresh(props.kind === 'photo' ? 'image' : 'video')
}, { immediate: true })

function restoreVideo() {
  const player = video.value
  if (!player || !Number.isFinite(player.duration)) return
  player.currentTime = Math.min(props.position / 1000, Math.max(0, player.duration - 0.01))
}

function recordPosition(event: Event) {
  const player = event.target as HTMLVideoElement
  if (!fileUrl.value || !Number.isFinite(player.duration)) return
  const value = Math.round(player.currentTime * 1000)
  if (event.type === 'timeupdate' && Math.abs(lastSaved - value) < 750) return
  if (value === lastSaved) return
  lastSaved = value
  emit('position', value)
}

function savePosition() {
  if (!video.value || !fileUrl.value || !Number.isFinite(video.value.duration)) return
  const value = Math.round(video.value.currentTime * 1000)
  if (value !== props.position) emit('position', value)
}

function imageLoaded(event: Event) {
  const image = event.currentTarget as HTMLImageElement
  if (image.getAttribute('src') !== fileUrl.value) return
  imageSize.value = { width: image.naturalWidth, height: image.naturalHeight }
  emit('photoLoading', undefined)
}

function failed(event: Event) {
  if (!fileUrl.value || busy.value) return
  if ((event.currentTarget as HTMLMediaElement | HTMLImageElement).getAttribute('src') !== fileUrl.value) return
  emit('photoLoading', undefined)
  message.value = props.kind === 'photo' ? '无法解码此照片。' : '无法直接播放此视频。'
  fileUrl.value = ''
}

async function pollProxy(id: string, request: number, action: 'request' | 'status') {
  try {
    const reply = await api.proxy(id, action)
    if (request !== generation) return
    if ('error' in reply) throw new Error(reply.error.message)
    if (reply.result?.state === 'complete') {
      proxy.value = true
      await refresh('proxy', request)
    } else if (reply.result?.state === 'running' || reply.result?.state === 'queued') {
      message.value = '正在生成 WebM 预览…'
      timer = setTimeout(() => { void pollProxy(id, request, 'status') }, 600)
    } else {
      message.value = reply.result?.message ?? '预览代理不可用。'
      busy.value = false
    }
  } catch (error) {
    if (request !== generation) return
    message.value = error instanceof Error ? error.message : '预览代理不可用。'
    busy.value = false
  }
}

function requestProxy() {
  busy.value = true
  void pollProxy(props.assetId, ++generation, 'request')
}

async function closePreview() {
  await release(props.assetId)
  if (busy.value && props.kind === 'video') await api.proxy(props.assetId, 'cancel')
  busy.value = false
}

async function cancelProxyPreview() {
  await closePreview()
  message.value = '已取消预览代理。'
}

async function openFolder() {
  const request = ++folderRequest
  openingFolder.value = true
  folderError.value = ''
  try {
    const reply = await api.openAssetFolder(props.assetId)
    if (request !== folderRequest) return
    if ('error' in reply) throw new Error(reply.error.message)
    if (!reply.result.launched) folderError.value = '无法启动 Windows 资源管理器。'
  } catch (error) {
    if (request === folderRequest) folderError.value = error instanceof Error ? error.message : '无法打开原件所在文件夹。'
  } finally {
    if (request === folderRequest) openingFolder.value = false
  }
}

onBeforeUnmount(() => { endDrag(); void closePreview() })
defineExpose({ closePreview, savePosition, reopen: () => refresh(props.kind === 'photo' ? 'image' : proxy.value ? 'proxy' : 'video') })
</script>

<template>
  <section class="media-panel" aria-label="素材预览">
    <div class="workspace-panel-heading media-heading">
      <slot name="title"><h2>预览</h2></slot>
      <div v-if="fileUrl && kind === 'photo' && imageSize" class="media-zoom" role="group" aria-label="图片缩放">
        <button type="button" aria-label="缩小图片" title="缩小图片" :disabled="zoom === minZoom" @click="setZoom(zoom - zoomStep)">
          <Minus :size="16" aria-hidden="true" />
        </button>
        <button type="button" :aria-label="`当前缩放 ${zoom}%，恢复适应窗口`" title="恢复适应窗口" @click="reset">
          {{ zoom }}%
        </button>
        <button type="button" aria-label="放大图片" title="放大图片" :disabled="zoom === maxZoom" @click="setZoom(zoom + zoomStep)">
          <Plus :size="16" aria-hidden="true" />
        </button>
      </div>
    </div>
    <div class="media-frame" :class="{ 'media-frame-photo': kind === 'photo' && fileUrl && imageSize, 'media-frame-dragging': dragging }"
      :title="kind === 'photo' ? '滚轮缩放 · 按住鼠标拖动 · 双击恢复适应窗口' : undefined"
      :aria-label="kind === 'photo' ? '图片预览，滚轮缩放，按住鼠标拖动' : undefined"
      @wheel="wheel" @pointerdown="startDrag" @pointermove="moveDrag" @pointerup="endDrag"
      @pointercancel="endDrag" @lostpointercapture="endDrag" @dblclick="kind === 'photo' && reset()">
      <img v-if="fileUrl && kind === 'photo'" :key="generation" :src="fileUrl" alt="原件预览"
        :style="imageSize && { '--image-width': `${imageSize.width}px`, '--image-ratio': imageSize.width / imageSize.height, ...imageStyle }"
        :draggable="false"
        @load="imageLoaded" @error="failed">
      <video v-else-if="fileUrl" ref="video" :src="fileUrl" controls preload="metadata"
        @loadedmetadata="restoreVideo" @timeupdate="recordPosition" @seeked="recordPosition" @pause="recordPosition" @error="failed"></video>
      <p v-else role="status">{{ message || (busy ? '正在加载预览…' : '预览已关闭') }}</p>
    </div>
    <div class="media-footer">
      <p v-if="fileUrl && message" role="status">{{ message }}</p>
      <div class="media-footer-row">
        <div class="workspace-actions">
          <button v-if="!fileUrl && kind === 'video' && !busy" type="button" data-preview-proxy @click="requestProxy">生成兼容预览</button>
          <button v-if="kind === 'video' && busy && message" type="button" @click="cancelProxyPreview">取消预览</button>
          <button v-if="!fileUrl && !busy" type="button" @click="refresh(kind === 'photo' ? 'image' : 'video')">重试预览</button>
          <button v-if="showReveal" type="button" class="workspace-text-button" data-preview-folder
            :disabled="openingFolder" @click="openFolder">
            {{ openingFolder ? '正在打开…' : '打开所在文件夹' }}
          </button>
        </div>
        <slot name="metadata" />
      </div>
      <OperationFeedback :message="folderError" :warning="true" />
    </div>
  </section>
</template>
