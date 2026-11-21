<script setup lang="ts">
import OperationFeedback from './OperationFeedback.vue'
import { onUnmounted, ref } from 'vue'
import { background, backgroundOptions, importBackground, removeBackground, updateBackgroundOption, saveBackgroundOptions } from './background'
import { notify } from './notices'

const picker = ref<HTMLInputElement>()
const busy = ref(false)
const message = ref('')
const failed = ref(false)
let saveTimer: ReturnType<typeof setTimeout> | undefined
let pendingSave = false

function flushOptions() {
  clearTimeout(saveTimer)
  saveTimer = undefined
  if (!pendingSave) return
  saveBackgroundOptions()
  pendingSave = false
}

function finishChange() {
  try { flushOptions() }
  catch (error) { failed.value = true; message.value = (error as Error).message }
}

onUnmounted(() => {
  try { flushOptions() }
  catch (error) { notify((error as Error).message, true) }
})

async function selectImage(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  busy.value = true; message.value = ''; failed.value = false
  try {
    await importBackground(file)
    message.value = '背景已保存，界面已随图片主色调调整。'
  } catch (error) { failed.value = true; message.value = error instanceof Error ? error.message : '无法导入背景图片。' }
  finally { busy.value = false; input.value = '' }
}

function change(key: 'blur' | 'transparency', event: Event) {
  try {
    updateBackgroundOption(key, Number((event.target as HTMLInputElement).value), false)
    pendingSave = true
    clearTimeout(saveTimer)
    saveTimer = setTimeout(finishChange, 200)
    message.value = ''; failed.value = false
  } catch (error) { failed.value = true; message.value = (error as Error).message }
}

function clear() {
  try { removeBackground(); failed.value = false; message.value = '背景已移除，已恢复默认主题配色。' }
  catch (error) { failed.value = true; message.value = (error as Error).message }
}
</script>

<template>
  <div class="background-settings" aria-labelledby="background-settings-title">
    <h3 id="background-settings-title">背景图片</h3>
    <input ref="picker" type="file" accept="image/jpeg,image/png,image/webp,image/bmp,image/gif" hidden @change="selectImage">
    <div class="workspace-actions workspace-view-actions">
      <button type="button" :disabled="busy" @click="picker?.click()">{{ busy ? '正在导入…' : background ? '更换背景图片' : '导入背景图片' }}</button>
      <button v-if="background" type="button" :disabled="busy" @click="clear">移除背景</button>
    </div>
    <p class="workspace-muted">图片仅保存在本机，自动提取主色调并应用到界面。支持 JPG、PNG、WebP、BMP、GIF，最大 30 MB。</p>
    <div v-if="background" class="background-settings-controls">
      <img class="background-settings-preview" :src="background.image" alt="当前背景图片预览">
      <div class="background-settings-sliders">
        <p class="background-settings-name" :title="background.name">{{ background.name }}</p>
        <label for="background-blur">模糊度 <output>{{ backgroundOptions.blur }} px</output></label>
        <input id="background-blur" type="range" min="0" max="40" step="1" :value="backgroundOptions.blur" :disabled="busy" @input="change('blur', $event)" @change="finishChange">
        <label for="background-transparency">透明度 <output>{{ backgroundOptions.transparency }}%</output></label>
        <input id="background-transparency" type="range" min="0" max="100" step="1" :value="backgroundOptions.transparency" :disabled="busy" @input="change('transparency', $event)" @change="finishChange">
        <p class="workspace-muted">透明度越高，背景越淡。调整会实时生效并自动保存。</p>
      </div>
    </div>
    <OperationFeedback :message="message" :warning="failed" />
  </div>
</template>
