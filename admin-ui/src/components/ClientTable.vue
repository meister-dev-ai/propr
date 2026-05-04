<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <table v-if="filteredClients.length > 0" class="client-table">
    <thead>
      <tr>
        <th>Display Name</th>
        <th>Tenant</th>
        <th>Status</th>
        <th>Usage (30d)</th>
        <th>Created</th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="client in filteredClients" :key="client.id" class="row-clickable" @click="openClient(client.id)">
        <td><RouterLink :to="clientDetailRoute(client.id)" @click.stop>{{ client.displayName }}</RouterLink></td>
        <td>
          <div class="tenant-cell">
            <span>{{ client.tenantDisplayName ?? 'Unassigned' }}</span>
            <span v-if="client.tenantSlug" class="tenant-slug">/{{ client.tenantSlug }}</span>
          </div>
        </td>
        <td>
          <span :class="client.isActive ? 'chip chip-success' : 'chip chip-muted'">
            <i :class="client.isActive ? 'fi fi-rr-check-circle' : 'fi fi-rr-ban'"></i>
            {{ client.isActive ? 'Active' : 'Inactive' }}
          </span>
        </td>
        <td>
          <span class="chip usage-badge" v-if="client.recentUsageTokens !== undefined">
            <i class="fi fi-rr-chart-line-up"></i>
            {{ formatTokens(client.recentUsageTokens) }}
          </span>
          <span class="chip usage-badge" v-else>
            <i class="fi fi-rr-minus-circle"></i> --
          </span>
        </td>
        <td>{{ formatDate(client.createdAt) }}</td>
      </tr>
    </tbody>
  </table>
  <p v-else class="clients-no-results">No clients match your search.</p>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, useRouter } from 'vue-router'

interface Client {
  id: string
  displayName: string
  isActive: boolean
  createdAt: string
  recentUsageTokens?: number
  tenantId?: string | null
  tenantSlug?: string | null
  tenantDisplayName?: string | null
}

const props = defineProps<{
  clients: Client[]
  filter: string
  tenantFilterId?: string
}>()

const router = useRouter()

const filteredClients = computed(() =>
  props.clients.filter((c) =>
    matchesSearch(c, props.filter) && matchesTenantFilter(c, props.tenantFilterId)
  )
)

function matchesSearch(client: Client, filter: string): boolean {
  const normalizedFilter = filter.trim().toLowerCase()
  if (!normalizedFilter) {
    return true
  }

  return [client.displayName, client.tenantDisplayName, client.tenantSlug]
    .filter((value): value is string => Boolean(value))
    .some((value) => value.toLowerCase().includes(normalizedFilter))
}

function matchesTenantFilter(client: Client, tenantFilterId?: string): boolean {
  return !tenantFilterId || client.tenantId === tenantFilterId
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString()
}

function formatTokens(tokens?: number): string {
  if (!tokens) return '0'
  if (tokens >= 1000) {
    return (tokens / 1000).toFixed(1).replace(/\.0$/, '') + 'k'
  }
  return tokens.toString()
}

function clientDetailRoute(clientId: string) {
  return {
    name: 'client-detail',
    params: { id: clientId },
  }
}

function openClient(clientId: string) {
  void router.push(clientDetailRoute(clientId))
}
</script>

<style scoped>
.row-clickable {
  cursor: pointer;
  transition: background 0.15s;
}

.clients-no-results {
  padding: 2rem 1.25rem;
  color: var(--color-text-muted);
  font-size: 0.875rem;
  margin: 0;
}

.usage-badge {
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid var(--color-border);
  color: var(--color-text-muted);
  font-family: monospace;
  font-size: 0.85rem;
  letter-spacing: 0.02em;
}
.usage-badge i {
  margin-right: 4px;
}

.tenant-cell {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
}

.tenant-slug {
  color: var(--color-text-muted);
  font-size: 0.8rem;
}
</style>
