<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { useSession } from '@/composables/useSession'
import { getExtensionConsent, decideExtensionConsent, type ExtensionConsent } from '@/services/extensionAuthorizationService'
import { forgetExtensionSignIn, isExtensionSignInUnavailable } from '@/features/auth/extensionSignInContinuation'

const route = useRoute()
const { username } = useSession()
const request = typeof route.query.request === 'string' ? route.query.request : ''
const consent = ref<ExtensionConsent>()
const error = ref('')
const busy = ref(false)
const continuationUnavailable = computed(() => isExtensionSignInUnavailable(route.query))
const displayedError = computed(() => continuationUnavailable.value
  ? 'Your browser could not retain the authorization request. Allow session storage for ProPR.'
  : error.value)
onMounted(async () => {
  forgetExtensionSignIn()
  if (continuationUnavailable.value) return
  if (window.top !== window.self) {
    error.value = 'Open the ProPR authorization page in its own window.'
    return
  }
  try { consent.value = await getExtensionConsent(request) }
  catch (cause) { error.value = cause instanceof Error ? cause.message : 'The sign-in request is unavailable.' }
})

async function decide(approve: boolean) {
  if (busy.value || continuationUnavailable.value) return
  busy.value = true
  try {
    const redirect = await decideExtensionConsent(request, approve)
    if (!continuationUnavailable.value) window.location.replace(redirect)
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Authorization failed.'
    busy.value = false
  }
}
</script>

<template>
  <main class="consent section-card">
    <h1>Connect ProPR Lens</h1>
    <p v-if="displayedError" role="alert" class="error">{{ displayedError }}</p>
    <p v-if="displayedError">Return to the extension settings and select Sign in again.</p>
    <template v-if="consent && !continuationUnavailable">
      <p>Allow {{ consent.application }} to use your ProPR account <strong>{{ username }}</strong>?</p>
      <p class="muted-hint">Installation: {{ consent.issuer }}</p>
      <ul><li v-for="permission in consent.permissions" :key="permission">{{ permission }}</li></ul>
      <p>Your existing account permissions still apply. The extension follows this website session and receives no password or renewal token.</p>
      <p>You can revoke access from Settings → Connected applications.</p>
      <div class="form-actions">
        <button type="button" class="btn-primary" :disabled="busy" @click="decide(true)">Allow access</button>
        <button type="button" class="btn-secondary" :disabled="busy" @click="decide(false)">Cancel</button>
      </div>
    </template>
    <p v-else-if="!displayedError">Loading authorization request…</p>
  </main>
</template>

<style scoped>
.consent { max-width: 42rem; padding: 2rem; margin: 2rem auto; }
.consent p { margin-block: 1rem; }
</style>
