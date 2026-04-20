<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div :class="shellClass">
    <div class="provider-form-grid">
      <div v-if="isCreateMode" class="form-field">
        <label>Provider</label>
        <select v-model="form.providerFamily" :disabled="availableProviderOptions.length === 0">
          <option v-for="option in availableProviderOptions" :key="option.value" :value="option.value">
            {{ option.label }}
          </option>
        </select>
        <p v-if="availableProviderOptions.length === 0" class="error provider-form-unavailable">No provider families are currently enabled.</p>
      </div>
      <div class="form-field">
        <label>Display Name</label>
        <input v-model="form.displayName" type="text" placeholder="e.g. GitHub Enterprise" />
      </div>
      <div class="form-field">
        <label>Host Base URL</label>
        <input v-model="form.hostBaseUrl" type="text" :placeholder="hostPlaceholder" />
      </div>
      <div class="form-field">
        <label>Authentication</label>
        <select v-model="form.authenticationKind">
          <option
            v-for="option in authenticationOptions"
            :key="option.value"
            :value="option.value"
          >
            {{ option.label }}
          </option>
        </select>
      </div>
      <div v-if="showAzureOAuthFields" class="form-field">
        <label>OAuth Tenant ID</label>
        <input v-model="form.oAuthTenantId" type="text" placeholder="contoso.onmicrosoft.com or tenant GUID" />
      </div>
      <div v-if="showAzureOAuthFields" class="form-field">
        <label>OAuth Client ID</label>
        <input v-model="form.oAuthClientId" type="text" placeholder="Azure app registration client ID" />
      </div>
      <div class="form-field provider-form-grid-full">
        <label>
          Secret
          <span v-if="!isCreateMode" class="field-hint-inline">(leave blank to keep current)</span>
        </label>
        <input
          v-model="form.secret"
          type="password"
          :placeholder="isCreateMode ? 'Paste the provider secret' : 'Optional replacement secret'"
        />
      </div>
    </div>
    <label class="toggle-checkbox">
      <input v-model="form.isActive" type="checkbox" />
      <span>{{ isCreateMode ? 'Connection is active immediately' : 'Connection is active' }}</span>
    </label>
    <p v-if="error" class="error">{{ error }}</p>
    <div class="form-actions">
      <button :class="submitButtonClass" :disabled="busy" @click="emit('submit')">
        {{ busy ? busyLabel : submitLabel }}
      </button>
      <button v-if="showCancel" class="btn-secondary btn-sm provider-form-cancel" @click="emit('cancel')">Cancel</button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { ProviderOption } from '@/services/providerActivationService'
import type { ScmAuthenticationKind, ScmProviderFamily } from '@/services/providerConnectionsService'

type ProviderConnectionFormModel = {
  providerFamily?: ScmProviderFamily
  hostBaseUrl: string
  authenticationKind: ScmAuthenticationKind
  oAuthTenantId?: string
  oAuthClientId?: string
  displayName: string
  secret: string
  isActive: boolean
}

const props = withDefaults(defineProps<{
  mode: 'create' | 'edit'
  form: ProviderConnectionFormModel
  providerOptions?: ProviderOption[]
  busy?: boolean
  error?: string
  submitLabel: string
  busyLabel: string
  submitButtonClass?: string
  showCancel?: boolean
}>(), {
  busy: false,
  error: '',
  submitButtonClass: 'btn-primary btn-sm provider-form-submit',
  showCancel: false,
})

const emit = defineEmits<{
  (e: 'submit'): void
  (e: 'cancel'): void
}>()

const defaultProviderOptions: ProviderOption[] = [
  { value: 'azureDevOps', label: 'Azure DevOps' },
  { value: 'github', label: 'GitHub' },
  { value: 'gitLab', label: 'GitLab' },
  { value: 'forgejo', label: 'Forgejo' },
]

const isCreateMode = computed(() => props.mode === 'create')
const shellClass = computed(() => isCreateMode.value ? 'section-card-body provider-form-shell' : 'provider-edit-shell')
const availableProviderOptions = computed(() => props.providerOptions?.length ? props.providerOptions : defaultProviderOptions)
const authenticationOptions = computed(() =>
  props.form.providerFamily === 'azureDevOps'
    ? [{ value: 'oauthClientCredentials', label: 'OAuth Client Credentials' }]
    : [{ value: 'personalAccessToken', label: 'Personal Access Token' }],
)
const showAzureOAuthFields = computed(() => props.form.providerFamily === 'azureDevOps')
const hostPlaceholder = computed(() => props.form.providerFamily === 'azureDevOps' ? 'https://dev.azure.com' : 'https://github.com')
</script>

<style scoped>
.provider-form-shell,
.provider-edit-shell {
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  margin-top: 1rem;
  padding-top: 1rem;
}

.provider-form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 0.85rem;
}

.provider-form-grid-full {
  grid-column: 1 / -1;
}

.toggle-checkbox {
  display: inline-flex;
  align-items: center;
  gap: 0.6rem;
  margin-top: 0.75rem;
}
</style>
