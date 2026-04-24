<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="page-view">
    <div class="header-stack" style="margin-bottom: 2rem;">
      <div class="header-nav-links">
        <router-link :to="{ name: 'client-detail', params: { id: clientId } }" class="back-link">
          ← Back to Client
        </router-link>
      </div>
      <h2 style="margin: 0;">Source Events</h2>
      <p class="view-subtitle" style="margin-top: 0.25rem;">Recent ProCursor capture events, filtered organically.</p>
    </div>

    <div v-if="!isProCursorAvailable" class="section-card">
      <div class="section-card-header">
        <h3>Usage Log</h3>
      </div>
      <div class="section-card-body">
        <p class="premium-unavailable-copy">{{ unavailableMessage }}</p>
      </div>
    </div>

    <div v-else class="section-card">
      <div class="section-card-header">
        <h3>Usage Log</h3>
        <div class="section-card-header-actions">
          <input
            v-model="filterText"
            type="text"
            placeholder="Search references..."
            class="filter-input"
          />
          <select v-model="filterTime" class="filter-select">
            <option value="all">All loaded time</option>
            <option value="1h">Last 1 hour</option>
            <option value="24h">Last 24 hours</option>
            <option value="7d">Last 7 days</option>
          </select>
          <button class="icon-btn" title="Refresh" @click="loadEvents">
            <i class="fi fi-rr-refresh"></i>
          </button>
        </div>
      </div>
      <div class="section-card-body" style="padding: 0;">
        <ProCursorUsageRecentEventsTable
          :items="filteredEvents"
          :loading="loading"
          :error="error"
        />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { useSession } from '@/composables/useSession'
import { getProCursorRecentEvents, PremiumFeatureUnavailableError } from '@/services/proCursorService'
import ProCursorUsageRecentEventsTable from '@/components/ProCursorUsageRecentEventsTable.vue'
import type { ProCursorTokenUsageEventDto } from '@/types/proCursorTokenUsage'

const route = useRoute()
const { getCapability } = useSession()
const clientId = route.params.id as string
const sourceId = route.params.sourceId as string

const loading = ref(false)
const error = ref('')
const events = ref<ProCursorTokenUsageEventDto[]>([])

const filterText = ref('')
const filterTime = ref('all')
const proCursorCapability = computed(() => getCapability('procursor'))
const isProCursorAvailable = computed(() => proCursorCapability.value?.isAvailable === true)
const unavailableMessage = computed(() =>
  proCursorCapability.value?.message
    ?? 'Commercial edition is required to use ProCursor knowledge sources, indexing, and usage reporting.',
)

const filteredEvents = computed(() => {
  let result = events.value

  if (filterText.value) {
    const q = filterText.value.toLowerCase()
    result = result.filter(
      (e) =>
        (e.sourcePath?.toLowerCase() || '').includes(q) ||
        (e.resourceId?.toLowerCase() || '').includes(q) ||
        (e.modelName?.toLowerCase() || '').includes(q) ||
        (e.requestId?.toLowerCase() || '').includes(q)
    )
  }

  if (filterTime.value !== 'all') {
    const now = new Date().getTime()
    const msMap: Record<string, number> = {
      '1h': 60 * 60 * 1000,
      '24h': 24 * 60 * 60 * 1000,
      '7d': 7 * 24 * 60 * 60 * 1000,
    }
    const delta = msMap[filterTime.value]
    if (delta) {
      result = result.filter((e) => {
        if (!e.occurredAtUtc) return false
        const t = new Date(e.occurredAtUtc).getTime()
        return now - t <= delta
      })
    }
  }

  return result
})

async function loadEvents() {
  if (!isProCursorAvailable.value) {
    events.value = []
    error.value = ''
    loading.value = false
    return
  }

  loading.value = true
  error.value = ''
  try {
    const response = await getProCursorRecentEvents(clientId, sourceId, 250)
    events.value = response.items ?? []
  } catch (err) {
    if (err instanceof PremiumFeatureUnavailableError) {
      error.value = ''
      return
    }

    error.value = err instanceof Error ? err.message : 'Unknown error'
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void loadEvents()
})
</script>

<style scoped>
.filter-input,
.filter-select {
  padding: 0.4rem 0.6rem;
  font-size: 0.85rem;
  border-radius: 6px;
  border: 1px solid var(--color-border);
  background: var(--color-surface);
  color: var(--color-text);
  min-width: 140px;
}

.filter-input {
  width: 180px;
}

.icon-btn {
  background: transparent;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  color: var(--color-text-muted);
  width: 32px;
  height: 32px;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  transition: all 0.2s;
}

.icon-btn:hover {
  background: var(--color-border);
  color: var(--color-text);
}

.premium-unavailable-copy {
  margin: 0;
  color: var(--color-text-muted);
}
</style>
