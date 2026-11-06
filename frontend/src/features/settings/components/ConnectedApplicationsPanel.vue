<script setup lang="ts">
import { onMounted, ref } from 'vue'
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
  <section class="section-card settings-card">
    <h2>Connected applications</h2>
    <p>Revoking access disconnects this application across its installations. Your ProPR website session stays signed in.</p>
    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <button v-if="error" type="button" class="btn-secondary" data-testid="retry-applications" :disabled="loading || busy" @click="load">Retry loading applications</button>
    <p v-if="loading" role="status">Loading applications…</p>
    <p v-else-if="loaded && !error && !grants.length">No applications have access.</p>
    <ul>
      <li v-for="grant in grants" :key="grant.id">
        {{ grant.application }}
        <button type="button" class="btn-secondary" :disabled="busy || loading" @click="revoke(grant.id!)">Revoke access</button>
      </li>
    </ul>
  </section>
</template>
