<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="login-container">
    <div class="login-view">
      <div class="login-brand">
        <img :src="icon" alt="" aria-hidden="true" class="login-icon"/>
        <h1>Meister ProPR Admin</h1>
      </div>
      <div v-if="vm.authOptions.value" class="login-auth-summary">
        <span :class="['chip', vm.authOptions.value.edition === 'commercial' ? 'chip-success' : 'chip-muted']">
          {{ vm.authOptions.value.edition === 'commercial' ? 'Commercial Edition' : 'Community Edition' }}
        </span>
        <p class="login-auth-message">{{ vm.signInMessage.value }}</p>
        <p v-if="vm.ssoCapabilityMessage.value" class="login-auth-submessage">{{ vm.ssoCapabilityMessage.value }}</p>
      </div>
      <p v-else-if="vm.authOptionsError.value" class="error login-auth-error">{{ vm.authOptionsError.value }}</p>
      <form @submit.prevent="vm.submitLogin" class="login-form">
        <div v-if="vm.validationError.value" class="error">{{ vm.validationError.value }}</div>
        <div v-if="vm.authError.value" class="error">{{ vm.authError.value }}</div>
        <label for="username">Username</label>
        <input
          id="username"
          v-model="vm.username.value"
          type="text"
          placeholder="Enter username"
          autocomplete="username"
        />
        <label for="password">Password</label>
        <input
          id="password"
          v-model="vm.password.value"
          type="password"
          placeholder="Enter password"
          autocomplete="current-password"
        />
        <button type="submit" class="btn-primary" :disabled="vm.loading.value">
          {{ vm.loading.value ? 'Signing in…' : 'Sign in' }}
        </button>
      </form>

      <section v-if="vm.canUseTenantSignIn.value" class="tenant-login-entry">
        <div>
          <h2 class="tenant-login-entry-title">Single sign-on</h2>
          <p class="tenant-login-entry-copy">Continue with your tenant's SSO provider, then enter the tenant slug in the next step.</p>
        </div>

        <button
          v-if="!vm.showTenantSlugPrompt.value"
          data-testid="tenant-login-start"
          type="button"
          class="btn-secondary tenant-login-entry-button"
          @click="vm.showTenantSlugPrompt.value = true"
        >
          Sign in with SSO
        </button>

        <form v-else class="tenant-login-entry-form" @submit.prevent="vm.submitTenantLogin">
          <div v-if="vm.tenantValidationError.value" class="error">{{ vm.tenantValidationError.value }}</div>
          <label for="tenant-login-slug">Tenant slug</label>
          <input
            id="tenant-login-slug"
            v-model="vm.tenantSlug.value"
            data-testid="tenant-login-slug"
            type="text"
            placeholder="acme"
            autocomplete="organization"
          />
          <div class="tenant-login-entry-actions">
            <button data-testid="tenant-login-submit" type="submit" class="btn-primary">Continue to SSO</button>
            <button type="button" class="btn-secondary" @click="vm.closeTenantPrompt">Cancel</button>
          </div>
        </form>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useLoginViewModel } from '@/features/auth/view-models/useLoginViewModel'
import icon from '@/assets/logo_standalone.png'

const vm = useLoginViewModel()
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
