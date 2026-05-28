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
      <div v-if="showUserNameField" class="form-field">
        <label>User Name</label>
        <input v-model="form.userName" type="text" placeholder="CONTOSO\\ado-user" />
      </div>
      <div v-if="showGitHubAppFields" class="form-field">
        <label>GitHub App ID</label>
        <input v-model="form.gitHubAppId" type="number" min="1" placeholder="123456" />
      </div>
      <div v-if="showGitHubAppFields" class="form-field">
        <label>Installation ID</label>
        <input v-model="form.gitHubAppInstallationId" type="number" min="1" placeholder="987654321" />
      </div>
      <p v-if="showAzureDevOpsServerSecurityHint" class="provider-form-grid-full provider-form-hint">
        Self-hosted Azure DevOps Server PAT and Windows user-account connections require HTTPS. If ProPR runs on Linux, WSL, or in containers, the server certificate must also be trusted inside that runtime.
      </p>
      <div class="form-field provider-form-grid-full">
        <label>
          {{ secretLabel }}
          <span v-if="!isCreateMode" class="field-hint-inline">{{ secretHintText }}</span>
        </label>
        <input
          v-model="form.secret"
          type="password"
          :placeholder="secretPlaceholder"
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
import { isHostedAzureDevOpsHost, type ProviderOption } from '@/services/providerActivationService'
import type { ScmAuthenticationKind, ScmProviderFamily } from '@/services/providerConnectionsService'

type ProviderConnectionFormModel = {
  providerFamily?: ScmProviderFamily
  hostBaseUrl: string
  authenticationKind: ScmAuthenticationKind
  userName?: string
  oAuthTenantId?: string
  oAuthClientId?: string
  gitHubAppId?: string | number
  gitHubAppInstallationId?: string | number
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
  secretRequired?: boolean
}>(), {
  busy: false,
  error: '',
  submitButtonClass: 'btn-primary btn-sm provider-form-submit',
  showCancel: false,
  secretRequired: false,
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
    ? props.form.hostBaseUrl.includes('dev.azure.com') || props.form.hostBaseUrl.includes('.visualstudio.com')
      ? [{ value: 'oauthClientCredentials', label: 'OAuth Client Credentials' }]
      : [
          { value: 'personalAccessToken', label: 'Personal Access Token' },
          { value: 'windowsUserAccount', label: 'Windows User Account' },
        ]
    : props.form.providerFamily === 'github'
      ? [
          { value: 'personalAccessToken', label: 'Personal Access Token' },
          { value: 'appInstallation', label: 'GitHub App Installation' },
        ]
      : [{ value: 'personalAccessToken', label: 'Personal Access Token' }],
)
const showAzureOAuthFields = computed(
  () => props.form.providerFamily === 'azureDevOps' && props.form.authenticationKind === 'oauthClientCredentials',
)
const showUserNameField = computed(
  () => props.form.providerFamily === 'azureDevOps' && props.form.authenticationKind === 'windowsUserAccount',
)
const showGitHubAppFields = computed(
  () => props.form.providerFamily === 'github' && props.form.authenticationKind === 'appInstallation',
)
const showAzureDevOpsServerSecurityHint = computed(
  () => props.form.providerFamily === 'azureDevOps' && !isHostedAzureDevOpsHost(props.form.hostBaseUrl),
)
const hostPlaceholder = computed(() => props.form.providerFamily === 'azureDevOps' ? 'https://dev.azure.com or https://ado-server.example.com/tfs' : 'https://github.com')
const secretLabel = computed(() => showGitHubAppFields.value ? 'Private Key (PEM)' : 'Secret')
const secretHintText = computed(() =>
  props.secretRequired ? '(required for this authentication change)' : '(leave blank to keep current)',
)
const secretPlaceholder = computed(() => {
  if (showGitHubAppFields.value) {
    return isCreateMode.value || props.secretRequired
      ? 'Paste the GitHub App private key (PEM)'
      : 'Optional replacement private key (PEM)'
  }

  return isCreateMode.value || props.secretRequired ? 'Paste the provider secret' : 'Optional replacement secret'
})
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

.provider-form-hint {
  color: rgba(15, 23, 42, 0.7);
  font-size: 0.95rem;
  margin: 0;
}

.toggle-checkbox {
  display: inline-flex;
  align-items: center;
  gap: 0.6rem;
  margin-top: 0.75rem;
}
</style>
