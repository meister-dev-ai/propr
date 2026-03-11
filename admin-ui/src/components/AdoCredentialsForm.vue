<template>
  <div class="ado-credentials-form">
    <h3>ADO Credentials</h3>
    <p class="credentials-status">
      Status: {{ hasCredentials ? 'Configured' : 'Not configured' }}
    </p>

    <form @submit.prevent="handleSave">
      <div v-if="formError" class="error">{{ formError }}</div>

      <div class="form-field">
        <label for="tenantId">Tenant ID</label>
        <input id="tenantId" name="tenantId" v-model="tenantId" type="text" />
      </div>
      <div class="form-field">
        <label for="clientId">Client ID</label>
        <input id="clientId" name="clientId" v-model="formClientId" type="text" />
      </div>
      <div class="form-field">
        <label for="secret">Client Secret</label>
        <input id="secret" name="secret" v-model="secret" type="password" autocomplete="new-password" />
      </div>

      <div class="form-actions">
        <button type="submit" :disabled="saving">
          {{ saving ? 'Saving…' : 'Save Credentials' }}
        </button>
        <button
          v-if="hasCredentials"
          type="button"
          class="clear-btn btn-danger"
          @click="handleClear"
          :disabled="saving"
        >
          Clear
        </button>
      </div>
    </form>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { createAdminClient } from '@/services/api'

const props = defineProps<{
  clientId: string
  hasCredentials: boolean
}>()

const emit = defineEmits<{
  'credentials-updated': []
  'credentials-cleared': []
}>()

const tenantId = ref('')
const formClientId = ref('')
const secret = ref('')
const saving = ref(false)
const formError = ref('')

async function handleSave() {
  saving.value = true
  formError.value = ''
  try {
    const { response } = await createAdminClient().PUT('/clients/{clientId}/ado-credentials', {
      params: { path: { clientId: props.clientId } },
      body: { tenantId: tenantId.value, clientId: formClientId.value, secret: secret.value },
    })
    if (!(response as Response).ok) {
      formError.value = 'Failed to save credentials.'
      return
    }
    secret.value = ''
    emit('credentials-updated')
  } catch {
    formError.value = 'Connection error.'
  } finally {
    saving.value = false
  }
}

async function handleClear() {
  saving.value = true
  formError.value = ''
  try {
    await createAdminClient().DELETE('/clients/{clientId}/ado-credentials', {
      params: { path: { clientId: props.clientId } },
    })
    emit('credentials-cleared')
  } catch {
    formError.value = 'Connection error.'
  } finally {
    saving.value = false
  }
}
</script>
