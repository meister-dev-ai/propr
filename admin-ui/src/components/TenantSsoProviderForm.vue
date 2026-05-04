<template>
  <section class="section-card tenant-provider-form-card">
    <div class="section-card-header">
      <div>
        <h3>Add Provider</h3>
        <p class="section-subtitle">Configure a tenant-scoped provider and optional first-sign-in policy.</p>
      </div>
    </div>

    <div class="section-card-body">
      <form data-testid="provider-submit" class="tenant-provider-form" @submit.prevent="handleSubmit">
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
            {{ busy ? 'Saving…' : 'Add provider' }}
          </button>
        </div>
      </form>
    </div>
  </section>
</template>

<script setup lang="ts">
import { reactive } from 'vue'
import type { TenantSsoProviderInput } from '@/services/tenantSsoProvidersService'

const props = withDefaults(defineProps<{
  busy?: boolean
  error?: string
  redirectUri?: string
}>(), {
  busy: false,
  error: '',
  redirectUri: '',
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

  if (!props.busy) {
    resetForm()
  }
}
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
