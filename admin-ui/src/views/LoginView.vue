<template>
  <div class="login-view">
      <div class="login-brand">
          <img :src="icon" alt="" aria-hidden="true" class="login-icon"/>
          <h1>Meister ProPR Admin</h1>
      </div>
    <form @submit.prevent="handleSubmit">
      <div v-if="validationError" class="error">{{ validationError }}</div>
      <div v-if="authError" class="error">{{ authError }}</div>
      <label for="admin-key">Admin Key</label>
      <input
        id="admin-key"
        v-model="adminKey"
        type="password"
        placeholder="Enter admin key"
        autocomplete="current-password"
        style="margin-bottom: 16px;"
      />
      <button type="submit" :disabled="loading">
        {{ loading ? 'Signing in…' : 'Sign in' }}
      </button>
    </form>
  </div>
</template>

<script setup lang="ts">
import {ref} from 'vue'
import {useRouter} from 'vue-router'
import {createAdminClient, UnauthorizedError} from '@/services/api'
import {useSession} from '@/composables/useSession'
import icon from '@/assets/logo_standalone.png'

const router = useRouter()
const { setAdminKey } = useSession()

const adminKey = ref('')
const loading = ref(false)
const validationError = ref('')
const authError = ref('')

async function handleSubmit() {
  validationError.value = ''
  authError.value = ''

  if (!adminKey.value.trim()) {
    validationError.value = 'Admin key is required'
    return
  }

  loading.value = true
  try {
    // Verify candidate key against backend before storing in sessionStorage
    await createAdminClient({ overrideKey: adminKey.value }).GET('/clients', {})
    setAdminKey(adminKey.value)
    router.push('/')
  } catch (err) {
    if (err instanceof UnauthorizedError) {
      authError.value = 'Invalid admin key'
    } else {
      authError.value = 'Connection error. Please try again.'
    }
  } finally {
    loading.value = false
  }
}
</script>
