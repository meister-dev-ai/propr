<template>
  <div class="login-container">
    <div class="login-view">
        <div class="login-brand">
            <img :src="icon" alt="" aria-hidden="true" class="login-icon"/>
            <h1>Meister ProPR Admin</h1>
        </div>
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
    </div>
  </div>
</template>

<script setup lang="ts">
import {ref} from 'vue'
import {useRouter} from 'vue-router'
import {useSession} from '@/composables/useSession'
import icon from '@/assets/logo_standalone.png'

const router = useRouter()
const { setTokens } = useSession()

const username = ref('')
const password = ref('')
const loading = ref(false)
const validationError = ref('')
const authError = ref('')

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
    const res = await fetch(
      (import.meta.env.VITE_API_BASE_URL ?? '') + '/auth/login',
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: username.value, password: password.value }),
      },
    )

    if (res.status === 401) {
      authError.value = 'Invalid username or password'
      return
    }

    if (!res.ok) {
      authError.value = 'Login failed. Please try again.'
      return
    }

    const data = await res.json() as { accessToken: string; refreshToken: string }
    setTokens(data.accessToken, data.refreshToken)
    router.push('/')
  } catch {
    authError.value = 'Connection error. Please try again.'
  } finally {
    loading.value = false
  }
}
</script>

