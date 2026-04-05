<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="recent-events-table">
    <div v-if="loading" class="events-state">
      <p>Loading recent usage events...</p>
    </div>

    <div v-else-if="error" class="events-state events-state--error">
      <p>{{ error }}</p>
    </div>

    <div v-else-if="!items.length" class="events-state">
      <p>No recent usage events.</p>
    </div>

    <div v-else class="events-table-scroll">
      <table class="table-wrapper">
        <thead>
          <tr>
            <th>When</th>
            <th>Call</th>
            <th>Model</th>
            <th>Tokens</th>
            <th>Safe Reference</th>
            <th>Request</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="`${item.requestId ?? item.occurredAtUtc ?? 'event'}-${item.sourcePath ?? item.resourceId ?? ''}`">
            <td>
              <div class="table-primary">{{ formatDate(item.occurredAtUtc) }}</div>
              <div class="table-secondary">{{ item.deploymentName || 'Unknown deployment' }}</div>
            </td>
            <td>
              <div class="table-primary">{{ formatCallType(item.callType) }}</div>
              <span v-if="isEstimated(item)" class="estimate-pill">Estimated</span>
            </td>
            <td>
              <div class="table-primary">{{ item.modelName || 'Unknown model' }}</div>
              <div class="table-secondary">{{ formatUsd(item.estimatedCostUsd) }}</div>
            </td>
            <td>
              <div class="table-primary">{{ formatNumber(item.totalTokens) }}</div>
              <div class="table-secondary">{{ formatNumber(item.promptTokens) }} in / {{ formatNumber(item.completionTokens) }} out</div>
            </td>
            <td>
              <div class="table-primary">{{ item.sourcePath || item.resourceId || 'n/a' }}</div>
              <div v-if="item.sourcePath && item.resourceId" class="table-secondary">{{ item.resourceId }}</div>
            </td>
            <td>
              <code :title="item.requestId || undefined" class="request-id">{{ shortRequestId(item.requestId) }}</code>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<script setup lang="ts">
import type { ProCursorTokenUsageEventDto } from '@/types/proCursorTokenUsage'

withDefaults(
  defineProps<{
    items?: ProCursorTokenUsageEventDto[]
    loading?: boolean
    error?: string
  }>(),
  {
    items: () => [],
    loading: false,
    error: '',
  },
)

const integerFormatter = new Intl.NumberFormat('en-US')
const currencyFormatter = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

function formatDate(value?: string): string {
  if (!value) {
    return 'Unknown time'
  }

  const parsed = new Date(value)
  if (Number.isNaN(parsed.getTime())) {
    return value
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(parsed)
}

function formatCallType(value?: string): string {
  if (!value) {
    return 'Unknown'
  }

  return value.replace(/[_-]/g, ' ').replace(/\b\w/g, (character) => character.toUpperCase())
}

function formatNumber(value?: number | null): string {
  return integerFormatter.format(value ?? 0)
}

function formatUsd(value?: number | null): string {
  return value == null ? 'Cost n/a' : currencyFormatter.format(value)
}

function shortRequestId(value?: string | null): string {
  if (!value) {
    return 'n/a'
  }

  return value.length <= 18 ? value : `${value.slice(0, 15)}...`
}

function isEstimated(item: ProCursorTokenUsageEventDto): boolean {
  return Boolean(item.tokensEstimated || item.costEstimated)
}
</script>

<style scoped>
.recent-events-table {
  min-width: 0;
}

.events-state {
  border: 1px dashed rgba(148, 163, 184, 0.22);
  border-radius: 0.9rem;
  padding: 1rem;
  text-align: center;
  color: var(--color-text-muted);
}

.events-state--error {
  border-style: solid;
  border-color: rgba(239, 68, 68, 0.22);
  color: #fecaca;
}

.events-table-scroll {
  overflow-x: auto;
}

table {
  width: 100%;
  border-collapse: collapse;
}

th {
  text-align: left;
  font-size: 0.72rem;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--color-text-muted);
  padding: 0 0 0.65rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

td {
  padding: 0.85rem 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
  vertical-align: top;
}

tbody tr:last-child td {
  border-bottom: 0;
}

.table-wrapper {
  width: calc(100% - 16px);
  margin: 8px;
}

.table-primary {
  font-weight: 600;
  overflow-wrap: anywhere;
}

.table-secondary {
  margin-top: 0.2rem;
  color: var(--color-text-muted);
  font-size: 0.8rem;
  overflow-wrap: anywhere;
}

.estimate-pill {
  display: inline-flex;
  align-items: center;
  margin-top: 0.3rem;
  padding: 0.18rem 0.45rem;
  border-radius: 999px;
  background: rgba(245, 158, 11, 0.14);
  color: #fcd34d;
  font-size: 0.72rem;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.request-id {
  display: inline-block;
  padding: 0.22rem 0.4rem;
  border-radius: 0.5rem;
  background: rgba(15, 23, 42, 0.4);
  color: var(--color-text);
  font-size: 0.78rem;
}

@media (max-width: 720px) {
  th,
  td {
    min-width: 120px;
  }
}
</style>
