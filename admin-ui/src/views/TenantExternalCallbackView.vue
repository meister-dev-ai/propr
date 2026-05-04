<template>
  <div class="login-container tenant-login-container">
    <div class="login-view tenant-login-view">
      <div>
        <p class="tenant-login-eyebrow">Tenant Sign-In</p>
        <h1>Completing sign-in</h1>
      </div>

      <p v-if="loading" class="tenant-callback-copy">Signing you in and loading your access...</p>
      <p v-else-if="errorMessage" class="error">{{ errorMessage }}</p>
      <p v-else class="tenant-callback-copy">Tenant sign-in did not complete.</p>

      <RouterLink class="tenant-login-recovery" :to="{ name: 'tenant-login', params: { tenantSlug } }">
        Back to tenant sign-in
      </RouterLink>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { useSession } from '@/composables/useSession'

const route = useRoute()
const router = useRouter()
const { establishSession } = useSession()

const tenantSlug = String(route.params.tenantSlug ?? '')
const loading = ref(true)
const errorMessage = ref('')

onMounted(() => {
  void completeTenantSignIn()
})

async function completeTenantSignIn() {
  const fragment = new URLSearchParams(window.location.hash.startsWith('#') ? window.location.hash.slice(1) : window.location.hash)
  const accessToken = fragment.get('accessToken')
  const refreshToken = fragment.get('refreshToken')

  if (accessToken && refreshToken) {
    try {
      await establishSession({
        accessToken,
        refreshToken,
        expiresIn: toOptionalNumber(fragment.get('expiresIn')),
        tokenType: fragment.get('tokenType') ?? undefined,
      })

      await router.replace({ name: 'home' })
    } catch {
      errorMessage.value = 'Tenant sign-in could not be completed. Please try again or contact a tenant administrator.'
      loading.value = false
    }

    return
  }

  errorMessage.value = fragment.get('message')
    ?? 'Tenant sign-in failed. Please try again or contact a tenant administrator.'
  loading.value = false
}

function toOptionalNumber(value: string | null): number | undefined {
  if (!value) {
    return undefined
  }

  const parsed = Number.parseInt(value, 10)
  return Number.isFinite(parsed) ? parsed : undefined
}
</script>

<style scoped>
.tenant-callback-copy {
  margin: 0;
  color: var(--color-text-muted);
}
</style>
