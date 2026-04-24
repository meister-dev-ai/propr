<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="client-overview">
    <div v-if="loading" class="overview-loading">
      <ProgressOrb class="state-orb" />
      <span>Loading overview…</span>
    </div>

    <div v-else class="overview-grid">
      <!-- SCM Providers -->
      <button class="overview-card overview-card--link" @click="emit('navigate', 'providers')">
        <div class="overview-card-icon">
          <i class="fi fi-rr-plug-connection"></i>
        </div>
        <div class="overview-card-body">
          <span class="overview-card-value">{{ scmCount }}</span>
          <span class="overview-card-label">SCM Providers</span>
        </div>
        <i class="fi fi-rr-arrow-small-right overview-card-arrow"></i>
      </button>

      <!-- AI Providers -->
      <button class="overview-card overview-card--link" @click="emit('navigate', 'ai')">
        <div class="overview-card-icon overview-card-icon--ai">
          <i class="fi fi-rr-robot"></i>
        </div>
        <div class="overview-card-body">
          <span class="overview-card-value">{{ aiCount }}</span>
          <span class="overview-card-label">AI Providers</span>
        </div>
        <i class="fi fi-rr-arrow-small-right overview-card-arrow"></i>
      </button>

      <!-- Reviews -->
      <button class="overview-card overview-card--link" @click="emit('navigate', 'history')">
        <div class="overview-card-icon overview-card-icon--reviews">
          <i class="fi fi-rr-time-past"></i>
        </div>
        <div class="overview-card-body">
          <span class="overview-card-value">{{ reviewCount != null ? reviewCount.toLocaleString() : '—' }}</span>
          <span class="overview-card-label">Reviews</span>
          <span v-if="latestReviewDate" class="overview-card-meta">Latest: {{ latestReviewDate }}</span>
        </div>
        <i class="fi fi-rr-arrow-small-right overview-card-arrow"></i>
      </button>

      <!-- 30d Token Usage -->
      <button v-if="isUsageCardAvailable" class="overview-card overview-card--link" @click="emit('navigate', 'usage')">
        <div class="overview-card-icon overview-card-icon--tokens">
          <i class="fi fi-rr-chart-histogram"></i>
        </div>
        <div class="overview-card-body">
          <span class="overview-card-value">{{ tokenSummary }}</span>
          <span class="overview-card-label">Tokens (30d)</span>
        </div>
        <i class="fi fi-rr-arrow-small-right overview-card-arrow"></i>
      </button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import ProgressOrb from '@/components/ProgressOrb.vue'
import { listProviderConnections } from '@/services/providerConnectionsService'
import { listAiConnections } from '@/services/aiConnectionsService'
import { getClientTokenUsage } from '@/services/clientTokenUsageService'
import { createAdminClient } from '@/services/api'
import { useSession } from '@/composables/useSession'
import type { components } from '@/services/generated/openapi'

type JobListItem = components['schemas']['JobListItem']

const props = defineProps<{ clientId: string }>()
const emit = defineEmits<{ (e: 'navigate', tab: string): void }>()
const { getAccessToken, getCapability } = useSession()

const loading = ref(true)
const scmCount = ref<number>(0)
const aiCount = ref<number>(0)
const reviewCount = ref<number | null>(null)
const latestReviewDate = ref<string | null>(null)
const tokenSummary = ref<string>('—')
const isUsageCardAvailable = computed(() => {
  if (import.meta.env.VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING === 'false') {
    return false
  }

  return getCapability('procursor')?.isAvailable === true
})

function todayStr() { return new Date().toISOString().slice(0, 10) }
function daysAgoStr(n: number) {
  const d = new Date(); d.setDate(d.getDate() - n); return d.toISOString().slice(0, 10)
}

function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
  if (n >= 1_000) return `${(n / 1_000).toFixed(0)}K`
  return n.toLocaleString()
}

function formatDate(iso: string | null | undefined): string {
  if (!iso) return ''
  return new Date(iso).toLocaleDateString([], { month: 'short', day: 'numeric', year: 'numeric' })
}

onMounted(async () => {
  loading.value = true
  try {
    const [scm, ai, jobsResp] = await Promise.allSettled([
      listProviderConnections(props.clientId),
      listAiConnections(props.clientId),
      createAdminClient().GET('/jobs', { params: { query: { clientId: props.clientId, limit: 500 } } }),
    ])

    if (scm.status === 'fulfilled') scmCount.value = scm.value.length
    if (ai.status === 'fulfilled') aiCount.value = ai.value.length

    if (jobsResp.status === 'fulfilled') {
      const items = (jobsResp.value.data as { items?: JobListItem[] })?.items ?? []
      reviewCount.value = items.length

      const latestItem = items
        .filter(i => i.status === 'completed' || i.status === 'failed')
        .sort((a, b) => (b.completedAt ?? '').localeCompare(a.completedAt ?? ''))[0]
      latestReviewDate.value = latestItem ? formatDate(latestItem.completedAt) : null
    }

    const token = getAccessToken()
    if (token && isUsageCardAvailable.value) {
      const usage = await getClientTokenUsage(props.clientId, daysAgoStr(29), todayStr(), token)
      const total = (usage.totalInputTokens ?? 0) + (usage.totalOutputTokens ?? 0)
      tokenSummary.value = total > 0 ? formatTokens(total) : '0'
    }
  } catch {
    // fail silently — individual metrics are best-effort
  } finally {
    loading.value = false
  }
})
</script>

<style scoped>
.client-overview {
  padding: 0.25rem 0;
}

.overview-loading {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 1rem 0;
  color: var(--color-text-muted);
  font-size: 0.9rem;
}

.overview-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  gap: 0.75rem;
}

.overview-card {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.9rem 1rem;
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.02);
  border: 1px solid rgba(255, 255, 255, 0.06);
  transition: all 0.2s ease;
  text-decoration: none;
  color: inherit;
  cursor: pointer;
  font: inherit;
  text-align: left;
  width: 100%;
}

.overview-card--link:hover {
  background: rgba(255, 255, 255, 0.05);
  border-color: rgba(255, 255, 255, 0.12);
  transform: translateY(-1px);
}

.overview-card--link:hover .overview-card-arrow {
  opacity: 1;
  transform: translateX(2px);
}

.overview-card-icon {
  width: 34px;
  height: 34px;
  border-radius: 9px;
  background: rgba(34, 211, 238, 0.1);
  border: 1px solid rgba(34, 211, 238, 0.15);
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  color: var(--color-accent);
  font-size: 1rem;
}

.overview-card-icon--ai {
  background: rgba(168, 85, 247, 0.1);
  border-color: rgba(168, 85, 247, 0.15);
  color: #a855f7;
}

.overview-card-icon--reviews {
  background: rgba(34, 197, 94, 0.1);
  border-color: rgba(34, 197, 94, 0.15);
  color: var(--color-success);
}

.overview-card-icon--tokens {
  background: rgba(249, 115, 22, 0.1);
  border-color: rgba(249, 115, 22, 0.15);
  color: #f97316;
}

.overview-card-body {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-width: 0;
}

.overview-card-value {
  font-size: 1.25rem;
  font-weight: 700;
  color: var(--color-text);
  line-height: 1.2;
}

.overview-card-label {
  font-size: 0.75rem;
  color: var(--color-text-muted);
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.overview-card-meta {
  font-size: 0.72rem;
  color: var(--color-text-muted);
  margin-top: 0.15rem;
  opacity: 0.75;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.overview-card-arrow {
  color: var(--color-text-muted);
  opacity: 0.4;
  flex-shrink: 0;
  transition: all 0.2s ease;
  font-size: 1.1rem;
}
</style>
