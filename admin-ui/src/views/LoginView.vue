<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="login-container">
    <div class="login-view">
      <div class="login-brand">
        <img :src="icon" alt="" aria-hidden="true" class="login-icon"/>
        <h1>Meister ProPR Admin</h1>
      </div>
      <div v-if="authOptions" class="login-auth-summary">
        <span :class="['chip', authOptions.edition === 'commercial' ? 'chip-success' : 'chip-muted']">
          {{ authOptions.edition === 'commercial' ? 'Commercial Edition' : 'Community Edition' }}
        </span>
        <p class="login-auth-message">{{ signInMessage }}</p>
        <p v-if="ssoCapabilityMessage" class="login-auth-submessage">{{ ssoCapabilityMessage }}</p>
      </div>
      <p v-else-if="authOptionsError" class="error login-auth-error">{{ authOptionsError }}</p>
      <form @submit.prevent="handleSubmit" class="login-form">
        <div v-if="validationError" class="error">{{ validationError }}</div>
        <div v-if="authError" class="error">{{ authError }}</div>
        <label for="username">Username</label>
        <input
          id="username"
          v-model="username"
          type="text"
          placeholder="Enter username"
          autocomplete="username"
        />
        <label for="password">Password</label>
        <input
          id="password"
          v-model="password"
          type="password"
          placeholder="Enter password"
          autocomplete="current-password"
        />
        <button type="submit" :disabled="loading">
          {{ loading ? 'Signing in…' : 'Sign in' }}
        </button>
      </form>

      <section v-if="canUseTenantSignIn" class="tenant-login-entry">
        <div>
          <h2 class="tenant-login-entry-title">Single sign-on</h2>
          <p class="tenant-login-entry-copy">Continue with your tenant's SSO provider, then enter the tenant slug in the next step.</p>
        </div>

        <button
          v-if="!showTenantSlugPrompt"
          data-testid="tenant-login-start"
          type="button"
          class="btn-secondary tenant-login-entry-button"
          @click="showTenantSlugPrompt = true"
        >
          Sign in with SSO
        </button>

        <form v-else class="tenant-login-entry-form" @submit.prevent="handleTenantLogin">
          <div v-if="tenantValidationError" class="error">{{ tenantValidationError }}</div>
          <label for="tenant-login-slug">Tenant slug</label>
          <input
            id="tenant-login-slug"
            v-model="tenantSlug"
            data-testid="tenant-login-slug"
            type="text"
            placeholder="acme"
            autocomplete="organization"
          />
          <div class="tenant-login-entry-actions">
            <button data-testid="tenant-login-submit" type="submit">Continue to SSO</button>
            <button type="button" class="btn-secondary" @click="closeTenantPrompt">Cancel</button>
          </div>
        </form>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {useRouter} from 'vue-router'
import {useSession} from '@/composables/useSession'
import { API_BASE_URL } from '@/services/apiBase'
import { getAuthOptions, supportsTenantSignIn, type AuthOptions } from '@/services/authOptionsService'
import icon from '@/assets/logo_standalone.png'

const router = useRouter()
const { establishSession } = useSession()

const authOptions = ref<AuthOptions | null>(null)
const authOptionsError = ref('')
const username = ref('')
const password = ref('')
const tenantSlug = ref('')
const showTenantSlugPrompt = ref(false)
const loading = ref(false)
const validationError = ref('')
const authError = ref('')
const tenantValidationError = ref('')

const signInMessage = computed(() => {
  if (!authOptions.value) {
    return 'Sign in with your username and password.'
  }

  return authOptions.value.availableSignInMethods.includes('sso')
    ? 'Password and single sign-on are available for this installation.'
    : 'Password sign-in is available for this installation.'
})

const ssoCapabilityMessage = computed(() =>
  authOptions.value?.capabilities.find((capability) => capability.key === 'sso-authentication')?.message ?? '',
)

const canUseTenantSignIn = computed(() => supportsTenantSignIn(authOptions.value))

onMounted(async () => {
  try {
    authOptions.value = await getAuthOptions()
  } catch {
    authOptionsError.value = 'Unable to load sign-in options right now.'
  }
})

async function handleSubmit() {
  validationError.value = ''
  authError.value = ''

  if (!username.value.trim()) {
    validationError.value = 'Username is required'
    return
  }
  if (!password.value) {
    validationError.value = 'Password is required'
    return
  }

  loading.value = true
  try {
    const res = await fetch(API_BASE_URL + '/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: username.value, password: password.value }),
    })

    if (res.status === 401) {
      authError.value = 'Invalid username or password'
      return
    }

    if (!res.ok) {
      authError.value = 'Login failed. Please try again.'
      return
    }

    const data = await res.json() as { accessToken: string; refreshToken: string }
    await establishSession(data)
    await router.push({ name: 'home' })
  } catch {
    authError.value = 'Connection error. Please try again.'
  } finally {
    loading.value = false
  }
}

async function handleTenantLogin() {
  tenantValidationError.value = ''

  if (!tenantSlug.value.trim()) {
    tenantValidationError.value = 'Tenant slug is required'
    return
  }

  await router.push({
    name: 'tenant-login',
    params: {
      tenantSlug: tenantSlug.value.trim(),
    },
  })
}

function closeTenantPrompt() {
  tenantValidationError.value = ''
  tenantSlug.value = ''
  showTenantSlugPrompt.value = false
}
</script>

<style scoped>
.login-auth-summary {
  margin-bottom: 1.25rem;
  text-align: center;
}

.login-auth-message,
.login-auth-submessage,
.login-auth-error {
  margin: 0.6rem 0 0;
}

.login-auth-submessage {
  color: var(--color-text-muted);
}

.tenant-login-entry {
  margin-top: 1.25rem;
  padding-top: 1.25rem;
  border-top: 1px solid var(--color-border);
}

.tenant-login-entry-title {
  margin: 0;
  font-size: 1rem;
}

.tenant-login-entry-copy {
  margin: 0.35rem 0 0;
  color: var(--color-text-muted);
}

.tenant-login-entry-form {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  margin-top: 1rem;
}

.tenant-login-entry-button {
  margin-top: 1rem;
}

.tenant-login-entry-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem;
}
</style>
