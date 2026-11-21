<script setup lang="ts">
import { onUnmounted } from 'vue'
import { Check, CircleAlert, X } from '@lucide/vue'
import { clearNotices, dismissNotice, notices, type Notice } from './notices'

onUnmounted(clearNotices)
function act(notice: Notice) {
  dismissNotice(notice.id)
  notice.action?.()
}
</script>

<template>
  <div class="workspace-notice-stack" aria-live="polite" aria-relevant="additions text">
    <TransitionGroup name="workspace-import-notice">
      <div v-for="notice in notices" :key="notice.id" class="workspace-import-notice"
        :class="{ 'workspace-import-notice-warning': notice.warning }" role="status">
        <component :is="notice.warning ? CircleAlert : Check" :size="17" aria-hidden="true" />
        <span>{{ notice.message }}</span>
        <button v-if="notice.actionLabel" type="button" class="workspace-text-button" @click="act(notice)">{{ notice.actionLabel }}</button>
        <button type="button" class="workspace-notice-close" aria-label="关闭提示" @click="dismissNotice(notice.id)"><X :size="14" aria-hidden="true" /></button>
      </div>
    </TransitionGroup>
  </div>
</template>
