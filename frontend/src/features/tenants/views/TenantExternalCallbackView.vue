<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="login-container tenant-login-container">
    <div class="login-view tenant-login-view">
      <div>
        <p class="tenant-login-eyebrow">Tenant Sign-In</p>
        <h1>{{ vm.pendingLink.value ? 'An account with this email address exists' : 'Completing sign-in' }}</h1>
      </div>

      <p v-if="vm.loading.value" class="tenant-callback-copy">Signing you in and loading your access...</p>
      <template v-else-if="vm.signedIn.value">
        <p class="tenant-callback-copy" data-testid="tenant-callback-signed-in">
          You are signed in, but the start page could not be loaded.
        </p>
        <RouterLink class="tenant-login-recovery" :to="{ name: 'home' }">Open the start page</RouterLink>
      </template>
      <template v-else-if="vm.pendingLink.value">
        <p class="tenant-callback-copy" data-testid="tenant-callback-pending-link">
          ProPR already has an account with the address <strong>{{ vm.pendingLink.value.email }}</strong>. The sign-in
          through {{ vm.pendingLink.value.providerDisplayName }} of {{ vm.pendingLink.value.tenantDisplayName }} is not
          linked to it.
        </p>

        <section class="tenant-callback-choice">
          <h2>Use the existing account</h2>
          <p class="tenant-callback-copy">
            Sign in to the existing account with the method you use for it. ProPR then shows this sign-in, and you confirm
            that it may sign you in to that account.
          </p>
          <button
            data-testid="tenant-callback-use-existing-account"
            type="button"
            class="btn-primary"
            :disabled="vm.creatingAccount.value || vm.openingExistingAccount.value"
            @click="vm.useExistingAccount"
          >
            Sign in to the existing account
          </button>
        </section>

        <section v-if="vm.pendingLink.value.canCreateAccount" class="tenant-callback-choice">
          <h2>Create a new account</h2>
          <p class="tenant-callback-copy">
            ProPR creates a separate account for this sign-in. The existing account stays as it is.
          </p>
          <button
            data-testid="tenant-callback-new-account"
            type="button"
            class="btn-secondary"
            :disabled="vm.creatingAccount.value || vm.openingExistingAccount.value"
            @click="vm.continueWithNewAccount"
          >
            {{ vm.creatingAccount.value ? 'Creating the account…' : 'Continue with a new account' }}
          </button>
        </section>
        <p v-else class="tenant-callback-copy" data-testid="tenant-callback-no-new-account">
          {{ vm.pendingLink.value.providerDisplayName }} creates no new accounts. If you need a separate account, ask a
          tenant administrator of {{ vm.pendingLink.value.tenantDisplayName }}.
        </p>

        <p v-if="vm.errorMessage.value" class="error">{{ vm.errorMessage.value }}</p>
      </template>
      <template v-else>
        <p v-if="vm.errorMessage.value" class="error">{{ vm.errorMessage.value }}</p>
        <p v-else class="tenant-callback-copy">Tenant sign-in did not complete.</p>

        <RouterLink class="tenant-login-recovery" :to="{ name: 'tenant-login', params: { tenantSlug: vm.tenantSlug } }">
          Back to tenant sign-in
        </RouterLink>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import { useTenantCallbackViewModel } from '@/features/tenants/view-models/useTenantCallbackViewModel'

const vm = useTenantCallbackViewModel()
</script>

<style scoped>
.tenant-callback-copy {
  margin: 0;
  color: var(--color-text-muted);
}

.tenant-callback-choice {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.6rem;
  padding-top: 1rem;
  border-top: 1px solid var(--color-border);
}

.tenant-callback-choice h2 {
  margin: 0;
  font-size: 1rem;
}
</style>
