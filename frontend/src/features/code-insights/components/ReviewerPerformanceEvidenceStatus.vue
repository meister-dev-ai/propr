<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->
<template>
  <p
    class="performance-evidence-status"
    role="status"
    :aria-label="viewLabel ? `${viewLabel} evidence updates` : 'Evidence updates'"
  >
    <span v-if="viewLabel">{{ viewLabel }}:</span>
    <span v-if="updatedAt">
      Evidence last updated
      <time
        :datetime="updatedAt.timestamp"
        :title="updatedAt.timestamp"
      >{{ updatedAt.label }}</time>.
    </span>
    <span v-else>Evidence update time unavailable.</span>
    <span
      v-if="pendingMessage"
      class="performance-evidence-status__pending"
    >{{ pendingMessage }}</span>
  </p>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { PerformanceView } from '@/services/reviewerPerformanceService'

const props = withDefaults(defineProps<{
  evidence?: PerformanceView['evidence']
  frozen?: boolean
  viewLabel?: string
}>(), { frozen: false })

const dateFormatter = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
  timeZone: 'UTC',
  timeZoneName: 'short',
})
const updatedAt = computed(() => {
  const timestamp = props.evidence?.newestProjectionAt
  if (!timestamp) return undefined

  const date = new Date(timestamp)
  if (!Number.isFinite(date.getTime())) return undefined

  return { timestamp, label: dateFormatter.format(date) }
})
const pendingMessage = computed(() => {
  if ((props.evidence?.pendingSourceAggregates ?? 0) <= 0) return undefined

  return props.frozen
    ? 'Evidence updates were pending at capture. Saved scores remain unchanged.'
    : 'Evidence updates are pending. Scores may change as updates are processed.'
})
</script>

<style scoped>
.performance-evidence-status {
  display: flex;
  flex-wrap: wrap;
  gap: 0.25rem 0.75rem;
  margin: 0.5rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.75rem;
  line-height: 1.6;
  overflow-wrap: anywhere;
}

.performance-evidence-status__pending {
  color: var(--color-warning);
}
</style>
