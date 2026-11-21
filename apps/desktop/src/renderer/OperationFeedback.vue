<script setup lang="ts">
import { onUnmounted, watch } from 'vue'
import { notify } from './notices'

const props = defineProps<{ message?: string; warning?: boolean; actionLabel?: string }>()
const emit = defineEmits<{ action: [] }>()
let active = true
onUnmounted(() => { active = false })
watch(() => props.message, message => {
  if (message) notify(message, props.warning, props.actionLabel, props.actionLabel ? () => { if (active) emit('action') } : undefined)
}, { immediate: true, flush: 'post' })
</script>

<template></template>
