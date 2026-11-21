<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import NoticeHost from './NoticeHost.vue'
import LibrarySearch from './LibrarySearch.vue'

const defaultDirectory = ref<string>()
let revision = 0

async function refreshDefaultDirectory() {
  const current = ++revision
  try {
    const reply = await window.mediaLibrary.getDefaultSearchDirectory()
    if (current === revision && 'result' in reply) defaultDirectory.value = reply.result ?? undefined
  } catch {
    // The search directory picker remains available while the local service reconnects.
  }
}

onMounted(() => {
  window.addEventListener('focus', refreshDefaultDirectory)
  void refreshDefaultDirectory()
})
onUnmounted(() => {
  ++revision
  window.removeEventListener('focus', refreshDefaultDirectory)
})
</script>

<template>
  <div class="workspace-app quick-search-window">
    <NoticeHost />
    <main class="workspace-content workspace-content-search" aria-label="快捷搜索">
      <LibrarySearch compact :default-directory="defaultDirectory" />
    </main>
  </div>
</template>
