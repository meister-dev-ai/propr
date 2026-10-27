<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<script setup lang="ts">
/**
 * The audit history of a tenant: changes to its settings, memberships, sign-in providers and machine credentials,
 * identity erasures, and the elevations of platform administrators with every content read under them.
 *
 * A page that refreshes itself while a review runs reads the same content every few seconds, and each read is a
 * record. Consecutive records of the same event by the same person for the same request are therefore shown as one
 * row with their number and their time span.
 */
import { computed, onMounted, ref, watch } from 'vue'
import { listTenantAuditEntries, type TenantAuditEntry } from '@/services/elevationsService'

const props = defineProps<{ tenantId: string }>()

const PAGE_SIZE = 100

const entries = ref<TenantAuditEntry[]>([])
const totalCount = ref(0)
const page = ref(1)
const loading = ref(false)
const error = ref('')

interface AuditRow {
  key: string
  first: TenantAuditEntry
  count: number
  oldest: string
}

/** Consecutive entries with the same event, actor, summary, detail and elevation, folded into one row. */
const rows = computed<AuditRow[]>(() => {
  const folded: AuditRow[] = []
  for (const entry of entries.value) {
    const previous = folded.at(-1)
    if (previous
      && previous.first.eventType === entry.eventType
      && previous.first.actorUserId === entry.actorUserId
      && previous.first.summary === entry.summary
      && previous.first.detail === entry.detail
      && previous.first.elevationId === entry.elevationId) {
      previous.count++
      previous.oldest = entry.occurredAt
      continue
    }

    folded.push({ key: entry.id, first: entry, count: 1, oldest: entry.occurredAt })
  }

  return folded
})

const hasMore = computed(() => entries.value.length < totalCount.value)

// Each load takes the next number, and only the newest load writes its answer, so a late answer for the tenant the
// page showed before never replaces the history of the current tenant.
let latestLoad = 0

onMounted(() => load(1))
watch(() => props.tenantId, () => {
  entries.value = []
  totalCount.value = 0
  void load(1)
})

async function load(nextPage: number): Promise<void> {
  const request = ++latestLoad
  loading.value = true
  error.value = ''
  try {
    const result = await listTenantAuditEntries(props.tenantId, nextPage, PAGE_SIZE)
    if (request !== latestLoad) {
      return
    }

    entries.value = nextPage === 1 ? result.items : [...entries.value, ...result.items]
    totalCount.value = result.totalCount
    page.value = nextPage
  } catch (failure) {
    if (request === latestLoad) {
      error.value = failure instanceof Error ? failure.message : 'The audit history could not be loaded.'
    }
  } finally {
    if (request === latestLoad) {
      loading.value = false
    }
  }
}

function formatTime(value: string): string {
  return new Date(value).toLocaleString()
}

function isElevationEvent(eventType: string): boolean {
  return eventType.startsWith('elevation.')
}
</script>

<template>
  <section class="section-card" data-testid="tenant-audit-history">
    <div class="section-card-header">
      <div>
        <h2>Audit history</h2>
        <p class="audit-subtitle">
          Changes to the settings, members and sign-in providers of this tenant, and the elevations of platform
          administrators with every content read under them, the newest first.
        </p>
      </div>
    </div>

    <div class="section-card-body">
      <p v-if="error" class="error" role="alert">{{ error }}</p>
      <p v-else-if="!loading && rows.length === 0" class="audit-empty" data-testid="tenant-audit-empty">
        The audit history of this tenant is empty.
      </p>

      <!-- The loaded entries stay in view when a later page cannot be loaded. -->
      <ol v-if="rows.length > 0" class="audit-list">
        <li
          v-for="row in rows"
          :key="row.key"
          :class="['audit-row', { 'audit-row--elevation': isElevationEvent(row.first.eventType) }]"
          data-testid="tenant-audit-row"
        >
          <div class="audit-row-meta">
            <time :datetime="row.first.occurredAt">{{ formatTime(row.first.occurredAt) }}</time>
            <span v-if="row.count > 1" class="audit-row-repeat" data-testid="tenant-audit-repeat">
              {{ row.count }} times since {{ formatTime(row.oldest) }}
            </span>
            <span class="audit-row-actor">{{ row.first.actorName ?? 'ProPR' }}</span>
          </div>
          <p class="audit-row-summary">{{ row.first.summary }}</p>
          <p v-if="row.first.detail" class="audit-row-detail">{{ row.first.detail }}</p>
        </li>
      </ol>

      <button v-if="hasMore" type="button" class="btn-secondary" :disabled="loading" @click="load(page + 1)">
        {{ loading ? 'Loading…' : 'Load older entries' }}
      </button>
    </div>
  </section>
</template>

<style scoped>
.audit-subtitle,
.audit-empty {
  margin: 0;
  font-size: 0.85rem;
  color: var(--color-text-muted);
}

.audit-list {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  margin: 0 0 1rem;
  padding: 0;
  list-style: none;
}

.audit-row {
  padding: 0.6rem 0.8rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
}

.audit-row--elevation {
  border-left: 3px solid var(--color-warning);
}

.audit-row-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem;
  font-size: 0.78rem;
  color: var(--color-text-muted);
}

.audit-row-repeat {
  font-weight: 600;
}

.audit-row-summary {
  margin: 0.3rem 0 0;
  font-size: 0.9rem;
}

.audit-row-detail {
  margin: 0.2rem 0 0;
  font-size: 0.8rem;
  color: var(--color-text-muted);
  word-break: break-word;
}
</style>
