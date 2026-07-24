<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <form data-testid="provider-submit" class="tenant-provider-form" @submit.prevent="handleSubmit">
      <p class="section-subtitle tenant-provider-intro">
        Configure a tenant-scoped single sign-on (SSO) identity provider for authentication, and its optional
        first-sign-in policy.
      </p>
        <div v-if="redirectUri" class="form-field tenant-provider-redirect-field">
          <label for="tenant-provider-redirect-uri">Redirect URI</label>
          <input
            id="tenant-provider-redirect-uri"
            data-testid="tenant-provider-redirect-uri"
            :value="redirectUri"
            type="text"
            readonly
          />
          <p class="field-help">Register this exact callback URI with the provider. It stays stable for this tenant.</p>
        </div>

        <div class="tenant-provider-grid">
          <div class="form-field">
            <label for="provider-display-name">Display Name</label>
            <input id="provider-display-name" data-testid="provider-display-name" v-model="form.displayName" type="text" />
          </div>

          <div class="form-field">
            <label for="provider-kind">Provider</label>
            <select id="provider-kind" data-testid="provider-kind" v-model="form.providerKind">
              <option value="EntraId">Entra ID</option>
              <option value="Google">Google</option>
              <option value="GitHub">GitHub</option>
            </select>
          </div>

          <div class="form-field">
            <label for="provider-protocol-kind">Protocol</label>
            <select id="provider-protocol-kind" data-testid="provider-protocol-kind" v-model="form.protocolKind">
              <option value="Oidc">OIDC</option>
              <option value="Oauth2">OAuth 2.0</option>
            </select>
          </div>

          <div class="form-field">
            <label for="provider-authority-url">Authority URL</label>
            <input id="provider-authority-url" data-testid="provider-authority-url" v-model="form.issuerOrAuthorityUrl" type="text" />
          </div>

          <div class="form-field">
            <label for="provider-client-id">Client ID</label>
            <input id="provider-client-id" data-testid="provider-client-id" v-model="form.clientId" type="text" />
          </div>

          <div class="form-field">
            <label for="provider-client-secret">Client Secret</label>
            <input id="provider-client-secret" data-testid="provider-client-secret" v-model="form.clientSecret" type="password" />
          </div>

          <div class="form-field">
            <label for="provider-scopes">Scopes</label>
            <input id="provider-scopes" data-testid="provider-scopes" v-model="form.scopes" type="text" placeholder="openid, profile, email" />
          </div>

          <div class="form-field">
            <label for="provider-allowed-domains">Allowed Domains</label>
            <input id="provider-allowed-domains" data-testid="provider-allowed-domains" v-model="form.allowedEmailDomains" type="text" placeholder="acme.test, example.com" />
          </div>
        </div>

        <div class="tenant-provider-flags">
          <label class="toggle-checkbox">
            <input v-model="form.isEnabled" type="checkbox" />
            <span>Provider is enabled</span>
          </label>

          <label class="toggle-checkbox">
            <input data-testid="provider-auto-create-users" v-model="form.autoCreateUsers" type="checkbox" />
            <span>Auto-create users on first sign-in</span>
          </label>
        </div>

        <p v-if="error" class="error">{{ error }}</p>

        <div class="form-actions">
          <button class="btn-primary btn-sm" type="submit" :disabled="busy">
            {{ busy ? 'Saving…' : provider ? 'Save changes' : 'Add SSO provider' }}
          </button>
        </div>
  </form>
</template>

<script setup lang="ts">
import { reactive, watch } from 'vue'
import type { TenantSsoProviderDto, TenantSsoProviderInput } from '@/services/tenantSsoProvidersService'

const props = withDefaults(defineProps<{
  busy?: boolean
  error?: string
  redirectUri?: string
  // When set, the form edits this provider (prefilled); when null it creates a new one.
  provider?: TenantSsoProviderDto | null
}>(), {
  busy: false,
  error: '',
  redirectUri: '',
  provider: null,
})

const emit = defineEmits<{
  (e: 'submit', request: TenantSsoProviderInput): void
}>()

const form = reactive({
  displayName: '',
  providerKind: 'EntraId',
  protocolKind: 'Oidc',
  issuerOrAuthorityUrl: '',
  clientId: '',
  clientSecret: '',
  scopes: 'openid, profile, email',
  allowedEmailDomains: '',
  isEnabled: true,
  autoCreateUsers: true,
})

function parseList(value: string): string[] {
  return value
    .split(',')
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0)
}

function resetForm() {
  form.displayName = ''
  form.providerKind = 'EntraId'
  form.protocolKind = 'Oidc'
  form.issuerOrAuthorityUrl = ''
  form.clientId = ''
  form.clientSecret = ''
  form.scopes = 'openid, profile, email'
  form.allowedEmailDomains = ''
  form.isEnabled = true
  form.autoCreateUsers = true
}

function handleSubmit() {
  emit('submit', {
    displayName: form.displayName,
    providerKind: form.providerKind,
    protocolKind: form.protocolKind,
    issuerOrAuthorityUrl: form.issuerOrAuthorityUrl,
    clientId: form.clientId,
    clientSecret: form.clientSecret,
    scopes: parseList(form.scopes),
    allowedEmailDomains: parseList(form.allowedEmailDomains),
    isEnabled: form.isEnabled,
    autoCreateUsers: form.autoCreateUsers,
  })
}

// Prefill from the provider being edited, or reset to defaults for a new one. The client secret is never
// returned, so it stays blank on edit (a blank secret keeps the stored one).
watch(
  () => props.provider,
  (provider) => {
    if (!provider) {
      resetForm()
      return
    }

    form.displayName = provider.displayName
    form.providerKind = provider.providerKind
    form.protocolKind = provider.protocolKind
    form.issuerOrAuthorityUrl = provider.issuerOrAuthorityUrl ?? ''
    form.clientId = provider.clientId
    form.clientSecret = ''
    form.scopes = provider.scopes.join(', ')
    form.allowedEmailDomains = provider.allowedEmailDomains.join(', ')
    form.isEnabled = provider.isEnabled
    form.autoCreateUsers = provider.autoCreateUsers
  },
  { immediate: true },
)
</script>

<style scoped>
.tenant-provider-form {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.tenant-provider-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 0.85rem;
}

.tenant-provider-flags {
  display: flex;
  flex-wrap: wrap;
  gap: 1rem;
}

.tenant-provider-redirect-field {
  margin-bottom: 0.25rem;
}

.field-help {
  margin: 0.35rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.85rem;
}
</style>
