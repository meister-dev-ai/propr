<template>
  <table v-if="filteredClients.length > 0" class="client-table">
    <thead>
      <tr>
        <th>Display Name</th>
        <th>Status</th>
        <th>ADO Credentials</th>
        <th>Created</th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="client in filteredClients" :key="client.id" class="row-clickable" @click="$router.push('/' + client.id)">
        <td><RouterLink :to="'/' + client.id">{{ client.displayName }}</RouterLink></td>
        <td>
          <span :class="client.isActive ? 'chip chip-success' : 'chip chip-muted'">
            <i :class="client.isActive ? 'fi fi-rr-check-circle' : 'fi fi-rr-ban'"></i>
            {{ client.isActive ? 'Active' : 'Inactive' }}
          </span>
        </td>
        <td>
          <span :class="client.hasAdoCredentials ? 'chip chip-success' : 'chip chip-muted'">
            <i :class="client.hasAdoCredentials ? 'fi fi-rr-plug-connection' : 'fi fi-rr-minus-circle'"></i>
            {{ client.hasAdoCredentials ? 'Configured' : 'None' }}
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
  hasAdoCredentials: boolean
  createdAt: string
}

const props = defineProps<{
  clients: Client[]
  filter: string
}>()

const $router = useRouter()

const filteredClients = computed(() =>
  props.clients.filter((c) =>
    c.displayName.toLowerCase().includes(props.filter.toLowerCase())
  )
)

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString()
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
</style>
