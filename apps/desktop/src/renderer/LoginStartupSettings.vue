<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import OperationFeedback from './OperationFeedback.vue'

const api = window.mediaLibrary
const enabled = ref(false)
const loaded = ref(false)
const busy = ref(false)
const message = ref('')
const failed = ref(false)
let request = 0

async function refresh() {
  if (busy.value) return
  const current = ++request
  try {
    const reply = await api.getLoginStartup()
    if (current !== request) return
    if ('error' in reply) throw Error(reply.error.message)
    enabled.value = reply.result
    loaded.value = true
  } catch (error) {
    if (current !== request) return
    loaded.value = false
    failed.value = true
    message.value = error instanceof Error ? error.message : '无法读取开机自动启动设置。'
  }
}

async function change(event: Event) {
  const select = event.target as HTMLSelectElement
  const next = select.value === 'enabled'
  select.value = enabled.value ? 'enabled' : 'disabled'
  if (busy.value || !loaded.value || next === enabled.value) return
  ++request
  busy.value = true
  message.value = ''
  try {
    const reply = await api.setLoginStartup(next)
    if ('error' in reply) throw Error(reply.error.message)
    enabled.value = reply.result
    failed.value = false
    message.value = enabled.value ? '已开启，下次登录 Windows 时自动启动。' : '已关闭开机自动启动。'
  } catch (error) {
    failed.value = true
    message.value = error instanceof Error ? error.message : '无法修改开机自动启动设置。'
  } finally {
    busy.value = false
    await refresh()
  }
}

onMounted(() => {
  void refresh()
  window.addEventListener('focus', refresh)
})
onUnmounted(() => {
  ++request
  window.removeEventListener('focus', refresh)
})
</script>

<template>
  <div class="theme-setting">
    <label for="login-startup">开机自动启动</label>
    <select id="login-startup" :value="enabled ? 'enabled' : 'disabled'" :disabled="busy || !loaded"
      :aria-busy="busy" @change="change">
      <option value="disabled">关闭</option>
      <option value="enabled">开启</option>
    </select>
    <OperationFeedback :message="message" :warning="failed" />
  </div>
</template>
