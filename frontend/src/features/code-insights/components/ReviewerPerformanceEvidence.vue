<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->
<template>
  <section
    v-if="evidence"
    class="performance-evidence"
    aria-label="Selected point evidence"
  >
    <header>
      <strong>{{ evidence.windowFrom }} → {{ evidence.windowTo }}</strong>
    </header>
    <div class="performance-evidence__metrics">
      <span
        v-for="metric in evidence.metrics"
        :key="metric.id"
      >
        <strong>{{ metric.label }}</strong>
        {{ metric.range }}
      </span>
    </div>
    <ul
      v-if="evidence.unavailableReasons.length"
      class="evidence-reasons"
    >
      <li
        v-for="reason in evidence.unavailableReasons"
        :key="reason.id"
      >
        {{ reason.label }}
      </li>
    </ul>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { PerformancePoint } from '@/services/reviewerPerformanceService'
import { prepareEvidence } from '../reviewerPerformanceEvidence'

const props = defineProps<{ point?: PerformancePoint }>()
const evidence = computed(() => prepareEvidence(props.point))
</script>

<style scoped>
.performance-evidence {
  border-top: 1px solid var(--color-border);
  padding-top: 0.9rem;
  margin-top: 1rem;
  font-size: 0.8rem;
}

header {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 0.5rem;
  color: var(--color-text-muted);
}

header strong {
  color: var(--color-text);
}

.performance-evidence__metrics {
  display: flex;
  flex-wrap: wrap;
  gap: 1.5rem;
  margin: 0.8rem 0;
}

.performance-evidence__metrics span {
  display: grid;
  gap: 0.3rem;
  font-variant-numeric: tabular-nums;
}

.performance-evidence__metrics strong {
  font-size: 0.72rem;
  color: var(--color-text-muted);
}

.evidence-reasons {
  margin: 0.75rem 0 0;
  padding-left: 1.2rem;
  color: var(--color-warning);
  line-height: 1.6;
}
</style>
