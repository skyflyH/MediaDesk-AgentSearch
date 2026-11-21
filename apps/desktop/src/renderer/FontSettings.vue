<script setup lang="ts">
import OperationFeedback from './OperationFeedback.vue'
import { ref, watch } from 'vue'
import { fontFamily, setFont } from './font'

const name = ref(fontFamily.value)
const message = ref('')
const failed = ref(false)
watch(fontFamily, (value) => { name.value = value })

function save(reset = false) {
  try {
    setFont(reset ? '' : name.value)
    name.value = fontFamily.value
    failed.value = false
    message.value = fontFamily.value ? '字体已保存并应用。' : '已恢复应用默认字体。'
  } catch (error) {
    failed.value = true
    message.value = error instanceof Error ? error.message : '无法保存字体设置。'
  }
}
</script>

<template>
  <form class="workspace-font-settings" @submit.prevent="save()">
    <label for="app-font-family">界面字体</label>
    <input id="app-font-family" v-model="name" type="text" maxlength="160" autocomplete="off"
      placeholder="默认：Instrument Sans / Segoe UI / Microsoft YaHei UI" aria-describedby="app-font-note">
    <p id="app-font-note" class="workspace-muted">填写本机已安装的字体名称。留空或字体不可用时，使用应用默认字体。</p>
    <div class="workspace-actions">
      <button type="submit">应用字体</button>
      <button type="button" @click="save(true)">恢复默认</button>
    </div>
    <OperationFeedback :message="message" :warning="failed" />
  </form>
</template>
