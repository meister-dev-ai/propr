<template>
  <div class="clients-view">
    <div class="clients-toolbar">
      <h2>Clients</h2>
      <input
        v-model="filter"
        type="search"
        placeholder="Search by name…"
        class="search-input"
      />
      <RouterLink to="/reviews" class="btn-secondary">Review History</RouterLink>
      <button class="btn-primary" @click="showCreateForm = true">New Client</button>
    </div>

    <ClientForm
      v-if="showCreateForm"
      @client-created="onClientCreated"
      @cancel="showCreateForm = false"
    />

    <p v-if="loading" class="loading">Loading…</p>
    <p v-else-if="error" class="error">{{ error }}</p>
    <ClientTable v-else :clients="clients" :filter="filter" />
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { RouterLink } from 'vue-router'
import ClientTable from '@/components/ClientTable.vue'
import ClientForm from '@/components/ClientForm.vue'
import { createAdminClient } from '@/services/api'

interface Client {
  id: string
  displayName: string
  isActive: boolean
  hasAdoCredentials: boolean
  createdAt: string
}

const clients = ref<Client[]>([])
const filter = ref('')
const loading = ref(false)
const error = ref('')
const showCreateForm = ref(false)

onMounted(async () => {
  loading.value = true
  try {
    const { data } = await createAdminClient().GET('/clients', {})
    clients.value = (data as Client[]) ?? []
  } catch {
    error.value = 'Failed to load clients.'
  } finally {
    loading.value = false
  }
})

function onClientCreated(client: unknown) {
  clients.value.unshift(client as Client)
  showCreateForm.value = false
}
</script>
