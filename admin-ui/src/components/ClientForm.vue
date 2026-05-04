<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <form @submit.prevent="handleSubmit" class="client-form">
    <div v-if="formError" class="error">{{ formError }}</div>

    <div class="form-field">
      <label for="tenantId">Tenant</label>
      <select
        id="tenantId"
        v-model="tenantId"
        data-testid="client-tenant-select"
        name="tenantId"
      >
        <option value="">Select a tenant</option>
        <option v-for="tenant in tenants" :key="tenant.id" :value="tenant.id">
          {{ tenant.displayName }}
        </option>
      </select>
      <span v-if="tenantError" class="field-error">{{ tenantError }}</span>
    </div>

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
import type { TenantDto } from '@/services/tenantAdminService'

const props = withDefaults(defineProps<{
  tenants?: TenantDto[]
  initialTenantId?: string
}>(), {
  tenants: () => [],
  initialTenantId: '',
})

const emit = defineEmits<{
  'client-created': [client: unknown]
  cancel: []
}>()

const tenantId = ref(props.initialTenantId)
const displayName = ref('')
const tenantError = ref('')
const displayNameError = ref('')
const formError = ref('')
const loading = ref(false)

async function handleSubmit() {
  tenantError.value = ''
  displayNameError.value = ''
  formError.value = ''

  let valid = true
  if (!tenantId.value) {
    tenantError.value = 'Tenant is required'
    valid = false
  }
  if (!displayName.value.trim()) {
    displayNameError.value = 'Display name is required'
    valid = false
  }
  if (!valid) return

  loading.value = true
  try {
    const { data, response } = await createAdminClient().POST('/clients', {
      body: {
        displayName: displayName.value.trim(),
        tenantId: tenantId.value,
      },
    })
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
