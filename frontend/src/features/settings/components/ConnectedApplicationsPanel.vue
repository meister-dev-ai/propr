<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { formatDateTime } from '@/components/usageDashboardFormatters'
import { listExtensionGrants, revokeExtensionGrant, type ExtensionGrant } from '@/services/extensionAuthorizationService'
const grants = ref<ExtensionGrant[]>([])
const error = ref('')
const busy = ref(false)
const loading = ref(true)
const loaded = ref(false)
async function load() {
  loading.value = true
  error.value = ''
  try { grants.value = await listExtensionGrants(); loaded.value = true }
  catch (cause) { error.value = cause instanceof Error ? cause.message : 'Applications could not be loaded.' }
  finally { loading.value = false }
}
async function revoke(id: string) {
  busy.value = true
  error.value = ''
  try {
    await revokeExtensionGrant(id)
    grants.value = grants.value.filter((grant) => grant.id !== id)
    await load()
  }
  catch (cause) { error.value = cause instanceof Error ? cause.message : 'Access could not be revoked.' }
  finally { busy.value = false }
}
onMounted(load)
</script>
<template>
  <div class="connected-applications">
    <div class="settings-page-header">
      <h2 class="view-title">Connected applications</h2>
      <p class="settings-description">
        Revoking access signs the application out of this account in every browser. Your ProPR website session stays
        signed in.
      </p>
    </div>
    <div class="section-card">
      <div class="section-card-header">
        <h3>Applications with access</h3>
      </div>
      <div class="section-card-body connected-applications-body">
        <p v-if="error" class="error" role="alert">{{ error }}</p>
        <button v-if="error" type="button" class="btn-secondary" data-testid="retry-applications" :disabled="loading || busy" @click="load">
          Retry loading applications
        </button>
        <p v-if="loading" class="muted-hint" role="status">Loading applications…</p>
        <p v-else-if="loaded && !error && !grants.length" class="muted-hint">No applications have access.</p>
        <ul v-if="grants.length" class="connected-applications-list">
          <li v-for="grant in grants" :key="grant.id">
            <div class="connected-application">
              <strong>{{ grant.application }}</strong>
              <span v-if="grant.grantedAt" class="muted-hint">Access granted {{ formatDateTime(grant.grantedAt) }}</span>
            </div>
            <button type="button" class="btn-secondary" :disabled="busy || loading" @click="revoke(grant.id!)">Revoke access</button>
          </li>
        </ul>
      </div>
    </div>
  </div>
</template>

<style scoped>
.settings-page-header {
  margin-bottom: 1.5rem;
}

.settings-description {
  color: var(--color-text-muted);
  margin: 0.5rem 0 0;
}

.connected-applications-body {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.75rem;
}

.connected-applications-body > p {
  margin: 0;
}

.connected-applications-list {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  width: 100%;
  margin: 0;
  padding: 0;
  list-style: none;
}

.connected-applications-list li {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
}

.connected-application {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
}
</style>
