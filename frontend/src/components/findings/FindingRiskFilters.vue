<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="risk-filters" role="group" aria-label="Finding risk filters">
    <label v-for="dimension in RISK_DIMENSIONS" :key="dimension.key">
      {{ dimension.label }}
      <select
        :value="modelValue[dimension.key] ?? ''"
        :aria-label="`Filter finding ${dimension.label.toLowerCase()}`"
        :disabled="disabled"
        @change="patch(dimension.key, ($event.target as HTMLSelectElement).value)"
      >
        <option value="">All</option>
        <option v-for="option in RISK_OPTIONS[dimension.key]" :key="option" :value="option">{{ riskLabel(option) }}</option>
      </select>
    </label>
  </div>
</template>

<script setup lang="ts">
import { RISK_DIMENSIONS, RISK_OPTIONS, riskLabel, type FindingRiskFilters, type RiskDimension } from '@/features/reviews/findingRisk'
const props = withDefaults(defineProps<{ modelValue?: FindingRiskFilters; disabled?: boolean }>(), { modelValue: () => ({}) })
const emit = defineEmits<{ 'update:modelValue': [filters: FindingRiskFilters] }>()
function patch(key: RiskDimension, value: string) { emit('update:modelValue', { ...props.modelValue, [key]: value }) }
</script>

<style scoped>
.risk-filters { display: flex; flex-wrap: wrap; gap: 0.65rem; margin-block: 0.6rem; }
.risk-filters label { display: flex; flex: 1 1 8rem; min-width: 0; flex-direction: column; gap: 0.3rem; font-size: 0.78rem; color: var(--color-text-muted); }
.risk-filters select { width: 100%; min-height: 2rem; border: 1px solid var(--color-border); border-radius: var(--radius-xs); padding: 0.3rem 0.5rem; background: var(--color-surface); color: var(--color-text); font: inherit; }
.risk-filters select:focus-visible { outline: 2px solid var(--color-accent); outline-offset: 2px; }
.risk-filters select:disabled { opacity: 0.6; }
</style>
