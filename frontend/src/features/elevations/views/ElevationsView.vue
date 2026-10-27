<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<script setup lang="ts">
/**
 * The elevations of all platform administrators, the newest first, with the records of each one: its start, every
 * content read and its end or expiry. The same records are in the audit history of each tenant.
 */
import { onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { AppTopBar } from '@/components'
import { describeScope, usePlatformAdminElevations } from '@/composables/usePlatformAdminElevations'
import {
  listElevationAuditEntries,
  listElevations,
  type PlatformAdminElevation,
  type TenantAuditEntry,
} from '@/services/elevationsService'

const PAGE_SIZE = 50

const elevations = ref<PlatformAdminElevation[]>([])
const totalCount = ref(0)
const page = ref(1)
const loading = ref(false)
const error = ref('')
const expandedId = ref<string | null>(null)
const records = ref<Record<string, TenantAuditEntry[]>>({})
// The elevations whose end request runs. An administrator can end several elevations at once, and each button stays
// disabled until its own request finishes.
const ending = ref(new Set<string>())

const { end } = usePlatformAdminElevations()

// Every list request takes the next number, and only the answer of the newest request changes the list. The reload of
// the first page after an end makes a pending request for an older page obsolete, so that page is not appended to the
// reloaded list.
let listRequest = 0

// Every request for the records of an elevation takes the next number, and the newest number is kept for each
// elevation. An answer is applied only when it belongs to the newest request for its elevation, so a late answer does
// not replace newer records of the same elevation.
let recordRequest = 0
const newestRecordRequest = new Map<string, number>()

onMounted(() => load(1))

async function load(nextPage: number): Promise<void> {
  const request = ++listRequest
  loading.value = true
  error.value = ''
  try {
    const result = await listElevations(nextPage, PAGE_SIZE)
    if (request !== listRequest) {
      return
    }

    elevations.value = nextPage === 1 ? result.items : [...elevations.value, ...result.items]
    totalCount.value = result.totalCount
    page.value = nextPage
  } catch (failure) {
    if (request === listRequest) {
      error.value = failure instanceof Error ? failure.message : 'The elevations could not be loaded.'
    }
  } finally {
    if (request === listRequest) {
      loading.value = false
    }
  }
}

async function toggle(elevation: PlatformAdminElevation): Promise<void> {
  if (expandedId.value === elevation.id) {
    expandedId.value = null
    return
  }

  expandedId.value = elevation.id
  error.value = ''
  const request = ++recordRequest
  newestRecordRequest.set(elevation.id, request)
  try {
    const entries = await listElevationAuditEntries(elevation.id)
    if (newestRecordRequest.get(elevation.id) === request) {
      records.value = { ...records.value, [elevation.id]: entries }
    }
  } catch (failure) {
    if (newestRecordRequest.get(elevation.id) === request && expandedId.value === elevation.id) {
      error.value = failure instanceof Error ? failure.message : 'The records of the elevation could not be loaded.'
    }
  }
}

async function endElevation(elevation: PlatformAdminElevation): Promise<void> {
  ending.value.add(elevation.id)
  error.value = ''
  try {
    await end(elevation.id)
    await load(1)
  } catch (failure) {
    error.value = failure instanceof Error ? failure.message : 'The elevation could not be ended.'
  } finally {
    ending.value.delete(elevation.id)
  }
}

function formatTime(value: string | null | undefined): string {
  return value ? new Date(value).toLocaleString() : '—'
}

function stateClass(state: string): string {
  return state === 'active' ? 'chip chip-warning' : 'chip chip-muted'
}
</script>

<template>
  <div class="page-view">
    <AppTopBar>
      <h2>Elevations</h2>
    </AppTopBar>

    <p class="elevations-intro">
      A platform administrator reads the content of a tenant only during an elevation. This page lists the elevations
      of all platform administrators with the content they read. The tenant administrators read the same records in
      the audit history of their tenant.
    </p>

    <div v-if="error" class="elevations-error" role="alert" data-testid="elevations-error">
      <p class="error">{{ error }}</p>
      <button v-if="elevations.length === 0" type="button" class="btn-secondary" :disabled="loading" @click="load(1)">
        Try again
      </button>
    </div>
    <p v-if="loading && elevations.length === 0" class="loading">Loading…</p>
    <p v-else-if="!error && elevations.length === 0" class="elevations-empty" data-testid="elevations-empty">
      No platform administrator has started an elevation yet.
    </p>

    <ol v-if="elevations.length > 0" class="elevations-list">
      <li v-for="elevation in elevations" :key="elevation.id" class="elevation-item" data-testid="elevation-item">
        <div class="elevation-item-header">
          <span :class="stateClass(elevation.state)">{{ elevation.state }}</span>
          <strong>{{ elevation.administratorName }}</strong>
          <span>{{ describeScope(elevation) }}</span>
          <RouterLink :to="{ name: 'tenant-detail', params: { tenantId: elevation.tenantId }, query: { section: 'audit' } }">
            Audit history
          </RouterLink>
        </div>
        <p class="elevation-item-reason">Reason: {{ elevation.reason }}</p>
        <p class="elevation-item-times">
          Started {{ formatTime(elevation.startedAt) }}, expires {{ formatTime(elevation.expiresAt) }}<template
            v-if="elevation.endedByName">, ended by {{ elevation.endedByName }} {{ formatTime(elevation.endedAt) }}</template>.
          {{ elevation.contentReads }} content reads.
        </p>
        <div class="elevation-item-actions">
          <button type="button" class="btn-secondary" data-testid="elevation-records" @click="toggle(elevation)">
            {{ expandedId === elevation.id ? 'Hide records' : 'Show records' }}
          </button>
          <button
            v-if="elevation.state === 'active'"
            type="button"
            class="btn-secondary"
            :disabled="ending.has(elevation.id)"
            data-testid="elevation-end-item"
            @click="endElevation(elevation)"
          >
            {{ ending.has(elevation.id) ? 'Ending…' : 'End elevation' }}
          </button>
        </div>
        <ol v-if="expandedId === elevation.id" class="elevation-records" data-testid="elevation-record-list">
          <li v-for="entry in records[elevation.id] ?? []" :key="entry.id" class="elevation-record">
            <time :datetime="entry.occurredAt">{{ formatTime(entry.occurredAt) }}</time>
            <span>{{ entry.summary }}</span>
            <span v-if="entry.detail" class="elevation-record-detail">{{ entry.detail }}</span>
          </li>
        </ol>
      </li>
    </ol>

    <button
      v-if="elevations.length < totalCount"
      type="button"
      class="btn-secondary"
      :disabled="loading"
      data-testid="elevations-older"
      @click="load(page + 1)"
    >
      {{ loading ? 'Loading…' : 'Load older elevations' }}
    </button>
  </div>
</template>

<style scoped>
.elevations-error {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.elevations-intro,
.elevations-empty {
  max-width: 60rem;
  color: var(--color-text-muted);
  font-size: 0.9rem;
}

.elevations-list {
  display: flex;
  flex-direction: column;
  gap: 0.8rem;
  padding: 0;
  list-style: none;
}

.elevation-item {
  padding: 0.9rem 1.1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--color-surface);
}

.elevation-item-header {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.75rem;
}

.elevation-item-reason,
.elevation-item-times {
  margin: 0.35rem 0 0;
  font-size: 0.85rem;
  color: var(--color-text-muted);
}

.elevation-item-actions {
  display: flex;
  gap: 0.5rem;
  margin-top: 0.6rem;
}

.elevation-records {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  margin: 0.75rem 0 0;
  padding: 0;
  list-style: none;
}

.elevation-record {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
  padding: 0.4rem 0.6rem;
  border-left: 3px solid var(--color-border);
  font-size: 0.82rem;
}

.elevation-record-detail {
  color: var(--color-text-muted);
  word-break: break-word;
}
</style>
