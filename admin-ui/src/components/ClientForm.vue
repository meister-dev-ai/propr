<template>
  <form @submit.prevent="handleSubmit" class="client-form">
    <div v-if="formError" class="error">{{ formError }}</div>

    <div class="form-field">
      <label for="displayName">Display Name</label>
      <input
        id="displayName"
        name="displayName"
        v-model="displayName"
        type="text"
        placeholder="Client display name"
      />
      <span v-if="displayNameError" class="field-error">{{ displayNameError }}</span>
    </div>

    <div class="form-field">
      <label for="key">Client Key</label>
      <input
        id="key"
        name="key"
        v-model="clientKey"
        type="text"
        placeholder="Unique client key"
      />
      <span v-if="keyError" class="field-error">{{ keyError }}</span>
    </div>

    <div class="form-actions">
      <button type="submit" class="btn-primary" :disabled="loading">
        {{ loading ? 'Creating…' : 'Create Client' }}
      </button>
      <button type="button" class="btn-secondary" @click="$emit('cancel')">Cancel</button>
    </div>
  </form>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { createAdminClient } from '@/services/api'

const emit = defineEmits<{
  'client-created': [client: unknown]
  cancel: []
}>()

const displayName = ref('')
const clientKey = ref('')
const displayNameError = ref('')
const keyError = ref('')
const formError = ref('')
const loading = ref(false)

async function handleSubmit() {
  displayNameError.value = ''
  keyError.value = ''
  formError.value = ''

  let valid = true
  if (!displayName.value.trim()) {
    displayNameError.value = 'Display name is required'
    valid = false
  }
  if (!clientKey.value.trim()) {
    keyError.value = 'Client key is required'
    valid = false
  }
  if (!valid) return

  loading.value = true
  try {
    const { data, response } = await createAdminClient().POST('/clients', {
      body: { displayName: displayName.value, key: clientKey.value },
    })
    if (response.status === 409) {
      formError.value = 'Key already in use'
      return
    }
    if (!response.ok) {
      formError.value = 'Failed to create client.'
      return
    }
    emit('client-created', data)
  } catch {
    formError.value = 'Connection error. Please try again.'
  } finally {
    loading.value = false
  }
}
</script>
