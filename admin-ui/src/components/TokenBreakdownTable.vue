<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div v-if="normalizedBreakdown.length > 0" class="token-breakdown">
    <h4 class="breakdown-title">Token Breakdown by Tier and Model</h4>
    <p class="breakdown-caption">Each row shows the routing tier and the exact model used for that portion of the job.</p>
    <table class="breakdown-table">
      <thead>
        <tr>
          <th>Tier</th>
          <th>Model</th>
          <th class="num-col">Input Tokens</th>
          <th class="num-col">Output Tokens</th>
          <th class="num-col">% of Total</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="(entry, idx) in normalizedBreakdown" :key="idx">
          <td><span class="tier-chip" :class="entry.tierClass">{{ entry.tierLabel }}</span></td>
          <td class="monospace-value">{{ entry.modelLabel }}</td>
          <td class="num-col">{{ formatTokens(entry.inputTokens) }}</td>
          <td class="num-col">{{ formatTokens(entry.outputTokens) }}</td>
          <td class="num-col">{{ pct(entry) }}</td>
        </tr>
      </tbody>
      <tfoot v-if="normalizedBreakdown.length > 1">
        <tr class="totals-row">
          <td colspan="2">Total</td>
          <td class="num-col">{{ formatTokens(sumInput) }}</td>
          <td class="num-col">{{ formatTokens(sumOutput) }}</td>
          <td class="num-col">100%</td>
        </tr>
      </tfoot>
    </table>
    <p v-if="breakdownConsistent === false" class="breakdown-warning">
      <i class="fi fi-rr-triangle-warning"></i>
      Breakdown sum does not match job aggregates — some calls may be missing tier info.
    </p>
  </div>
  <p v-else class="breakdown-empty">No per-tier breakdown available for this job.</p>
</template>

<script setup lang="ts">
import { computed } from 'vue'

interface TokenBreakdownEntry {
  connectionCategory?: number | null
  tier?: string | null
  modelId?: string | null
  aiModel?: string | null
  totalInputTokens?: number | null
  totalOutputTokens?: number | null
  inputTokens?: number | null
  outputTokens?: number | null
}

interface NormalizedBreakdownEntry {
  tierLabel: string
  tierClass: string
  modelLabel: string
  inputTokens: number
  outputTokens: number
}

const props = defineProps<{
  breakdown: TokenBreakdownEntry[]
  breakdownConsistent?: boolean | null
}>()

// AiConnectionModelCategory enum values
const CategoryLabels: Record<number, string> = {
  0: 'Low Effort',
  1: 'Medium Effort',
  2: 'High Effort',
  3: 'Embedding',
  4: 'Memory Reconsideration',
  5: 'Default',
}

const CategoryClasses: Record<number, string> = {
  0: 'tier-low',
  1: 'tier-medium',
  2: 'tier-high',
  3: 'tier-embedding',
  4: 'tier-memory',
  5: 'tier-default',
}

function parseCategory(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value
  }

  if (typeof value !== 'string') {
    return null
  }

  const normalized = value.trim().toLowerCase()
  if (!normalized) return null

  if (normalized === 'default') return 5
  if (normalized === 'loweffort') return 0
  if (normalized === 'mediumeffort') return 1
  if (normalized === 'higheffort') return 2
  if (normalized === 'embedding') return 3
  if (normalized === 'memoryreconsideration') return 4

  const parsed = Number(normalized)
  return Number.isFinite(parsed) ? parsed : null
}

function tierLabel(entry: TokenBreakdownEntry): string {
  const category = parseCategory(entry.connectionCategory ?? entry.tier)
  if (category == null) return 'Unknown'
  return CategoryLabels[category] ?? `Tier ${category}`
}

function tierClass(entry: TokenBreakdownEntry): string {
  const category = parseCategory(entry.connectionCategory ?? entry.tier)
  if (category == null) return 'tier-unknown'
  return CategoryClasses[category] ?? 'tier-unknown'
}

function formatTokens(n: number | null | undefined): string {
  if (n == null) return '—'
  return n.toLocaleString()
}

function formatModel(entry: TokenBreakdownEntry): string {
  const model = entry.modelId ?? entry.aiModel ?? ''
  if (!model || model === '(default)') {
    return '—'
  }

  return model
}

const normalizedBreakdown = computed<NormalizedBreakdownEntry[]>(() =>
  props.breakdown.map((entry) => ({
    tierLabel: tierLabel(entry),
    tierClass: tierClass(entry),
    modelLabel: formatModel(entry),
    inputTokens: entry.totalInputTokens ?? entry.inputTokens ?? 0,
    outputTokens: entry.totalOutputTokens ?? entry.outputTokens ?? 0,
  })),
)

const sumInput = computed(() => normalizedBreakdown.value.reduce((s, e) => s + e.inputTokens, 0))
const sumOutput = computed(() => normalizedBreakdown.value.reduce((s, e) => s + e.outputTokens, 0))
const grandTotal = computed(() => sumInput.value + sumOutput.value)

function pct(entry: NormalizedBreakdownEntry): string {
  const total = grandTotal.value
  if (!total) return '—'
  const share = ((entry.inputTokens + entry.outputTokens) / total) * 100
  return share.toFixed(1) + '%'
}
</script>

<style scoped>
.token-breakdown {
  margin-top: 1.5rem;
}

.breakdown-title {
  font-size: 0.9rem;
  font-weight: 600;
  color: var(--color-text-muted);
  text-transform: uppercase;
  letter-spacing: 0.05em;
  margin-bottom: 0.75rem;
}

.breakdown-caption {
  margin: -0.25rem 0 0.9rem;
  color: var(--color-text-muted);
  font-size: 0.8rem;
}

.breakdown-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.875rem;
}

.breakdown-table th,
.breakdown-table td {
  padding: 0.45rem 0.75rem;
  text-align: left;
  border-bottom: 1px solid var(--color-border);
}

.breakdown-table th {
  color: var(--color-text-muted);
  font-weight: 600;
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.num-col {
  text-align: right;
}

.totals-row td {
  font-weight: 600;
  border-top: 2px solid var(--color-border);
  border-bottom: none;
}

.tier-chip {
  display: inline-block;
  padding: 0.15rem 0.55rem;
  border-radius: 9999px;
  font-size: 0.72rem;
  font-weight: 600;
  letter-spacing: 0.03em;
}

.tier-low    { background: var(--color-success-bg, #d1fae5); color: var(--color-success, #065f46); }
.tier-medium { background: var(--color-warning-bg, #fef3c7); color: var(--color-warning, #92400e); }
.tier-high   { background: var(--color-error-bg,   #fee2e2); color: var(--color-error,   #991b1b); }
.tier-embedding { background: var(--color-info-bg,  #dbeafe); color: var(--color-info,    #1e40af); }
.tier-memory    { background: #ede9fe; color: #5b21b6; }
.tier-default   { background: var(--color-border); color: var(--color-text); }
.tier-unknown   { background: var(--color-border);   color: var(--color-text-muted); }

.breakdown-warning {
  margin-top: 0.5rem;
  font-size: 0.8rem;
  color: var(--color-warning, #92400e);
  display: flex;
  align-items: center;
  gap: 0.4rem;
}

.breakdown-empty {
  margin-top: 1rem;
  font-size: 0.85rem;
  color: var(--color-text-muted);
}
</style>
