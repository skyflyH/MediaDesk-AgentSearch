<script setup lang="ts">
import { Check, ChevronRight, LoaderCircle } from '@lucide/vue'
import { ref, useId } from 'vue'

defineProps<{ steps: string[] }>()
const stepsId = useId()
const open = ref(true)
</script>

<template>
  <div class="library-agent-process">
    <button type="button" class="library-agent-toggle" :aria-controls="stepsId" :aria-expanded="open"
      @click="open = !open">
      <LoaderCircle class="library-agent-spinner" :size="16" aria-hidden="true" />
      <span role="status" class="library-agent-progress-title">Agent 正在检索</span>
      <span v-if="!open && steps.length" class="library-agent-current">{{ steps.at(-1) }}</span>
      <ChevronRight class="library-agent-chevron" :size="16" aria-hidden="true" />
    </button>
    <ol v-show="open" :id="stepsId" class="library-agent-steps" aria-label="检索步骤">
      <li v-if="!steps.length" class="library-agent-step" aria-current="step">
        <LoaderCircle class="library-agent-spinner" :size="14" aria-hidden="true" />
        <span>正在准备检索…</span>
      </li>
      <li v-for="(step, index) in steps" :key="index" class="library-agent-step"
        :aria-current="index === steps.length - 1 ? 'step' : undefined">
        <LoaderCircle v-if="index === steps.length - 1" class="library-agent-spinner" :size="14" aria-hidden="true" />
        <Check v-else :size="14" aria-hidden="true" />
        <span>{{ step }}</span>
      </li>
    </ol>
  </div>
</template>
