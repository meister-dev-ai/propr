<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="login-container">
    <div class="login-view">
      <div class="login-brand">
        <img :src="icon" alt="" aria-hidden="true" class="login-icon"/>
        <h1>{{ vm.hasCustomerSignIn.value ? 'Meister ProPR' : 'Meister ProPR Admin' }}</h1>
      </div>
      <div v-if="vm.authOptions.value" class="login-auth-summary">
        <span v-if="!vm.provisionedUserSignIn.value" :class="['chip', vm.authOptions.value.edition === 'commercial' ? 'chip-success' : 'chip-muted']">
          {{ vm.authOptions.value.edition === 'commercial' ? 'Commercial Edition' : 'Community Edition' }}
        </span>
        <p class="login-auth-message">{{ vm.signInMessage.value }}</p>
        <p v-if="!vm.provisionedUserSignIn.value && vm.ssoCapabilityMessage.value" class="login-auth-submessage">{{ vm.ssoCapabilityMessage.value }}</p>
      </div>
      <p v-else-if="vm.authOptionsError.value" class="error login-auth-error">{{ vm.authOptionsError.value }}</p>
      <section v-if="vm.provisionedUserSignIn.value" class="installation-sign-in">
        <a
          data-testid="provisioned-user-sign-in-link"
          class="btn-primary provisioned-user-sign-in-link"
          :href="vm.provisionedUserSignInHref.value"
        >
          Sign in with {{ vm.provisionedUserSignIn.value.displayName }}
        </a>
      </section>

      <section v-if="vm.canUseTenantSignIn.value" class="tenant-login-entry" :class="{ 'alternative-sign-in': vm.provisionedUserSignIn.value }">
        <div>
          <h2 class="tenant-login-entry-title">Tenant sign-in</h2>
          <p class="tenant-login-entry-copy">Enter the tenant slug provided by your administrator to continue with your organization's sign-in.</p>
        </div>

        <button
          v-if="!vm.showTenantSlugPrompt.value"
          data-testid="tenant-login-start"
          type="button"
          class="tenant-login-entry-button"
          :class="vm.provisionedUserSignIn.value ? 'btn-secondary' : 'btn-primary'"
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
            <button data-testid="tenant-login-submit" type="submit" :class="vm.provisionedUserSignIn.value ? 'btn-secondary' : 'btn-primary'">Continue to SSO</button>
            <button type="button" class="btn-secondary" @click="vm.closeTenantPrompt">Cancel</button>
          </div>
        </form>
      </section>

      <form v-if="vm.passwordSignInAvailable.value" @submit.prevent="vm.submitLogin" class="login-form" :class="{ 'alternative-sign-in': vm.hasCustomerSignIn.value }">
        <h2 v-if="vm.hasCustomerSignIn.value" class="password-sign-in-title">Password sign-in</h2>
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
        <button type="submit" :class="vm.hasCustomerSignIn.value ? 'btn-secondary' : 'btn-primary'" :disabled="vm.loading.value">
          {{ vm.loading.value ? 'Signing in…' : 'Sign in' }}
        </button>
      </form>

      <section v-if="vm.platformAdminSignIn.value" class="installation-sign-in" :class="{ 'alternative-sign-in': vm.hasCustomerSignIn.value }">
        <button
          v-if="vm.hasCustomerSignIn.value"
          type="button"
          class="btn-ghost staff-access-toggle"
          data-testid="staff-access-toggle"
          :aria-expanded="vm.showStaffSignIn.value"
          aria-controls="staff-sign-in"
          @click="vm.showStaffSignIn.value = !vm.showStaffSignIn.value"
        >
          Staff access
        </button>
        <div v-show="!vm.hasCustomerSignIn.value || vm.showStaffSignIn.value" id="staff-sign-in" class="staff-sign-in">
          <p v-if="vm.hasCustomerSignIn.value" class="staff-sign-in-copy">For platform administrators.</p>
          <a
            data-testid="platform-admin-sign-in-link"
            class="platform-admin-sign-in-link"
            :class="vm.hasCustomerSignIn.value || vm.passwordSignInAvailable.value ? 'btn-secondary' : 'btn-primary'"
            :href="vm.platformAdminSignInHref.value"
          >
            Sign in with {{ vm.platformAdminSignIn.value.displayName }}
          </a>
        </div>
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
.login-container {
  padding: clamp(1rem, 4vw, 2rem);
}

.login-view {
  padding: clamp(1.5rem, 5vw, 3rem);
}

.login-view :is(a, button):focus-visible {
  outline: 2px solid var(--color-accent);
  outline-offset: 3px;
}

.login-view :is(a, button) {
  min-height: 2.75rem;
  text-align: center;
}

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

.installation-sign-in,
.staff-sign-in {
  display: flex;
  flex-direction: column;
}

.installation-sign-in {
  margin-top: 1rem;
}

.alternative-sign-in {
  margin-top: 1.25rem;
  padding-top: 1.25rem;
  border-top: 1px solid var(--color-border);
}

.tenant-login-entry-title,
.password-sign-in-title {
  margin: 0;
  font-size: 1rem;
}

.password-sign-in-title {
  margin-bottom: 1rem;
}

.staff-access-toggle {
  align-self: center;
}

.staff-sign-in-copy {
  margin: 0.75rem 0;
  color: var(--color-text-muted);
  text-align: center;
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
