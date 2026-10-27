<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="sign-in-link-page">
    <div class="settings-page-header">
      <h2 class="view-title">Link a sign-in to your account</h2>
    </div>

    <div class="section-card">
      <div class="section-card-body sign-in-link-body">
        <p v-if="vm.loading.value" class="muted-hint">Loading the waiting sign-in…</p>
        <template v-else-if="vm.linked.value && vm.pendingLink.value">
          <p class="success-hint" data-testid="sign-in-link-linked">
            The sign-in through {{ vm.pendingLink.value.providerDisplayName }} of
            {{ vm.pendingLink.value.tenantDisplayName }} is linked to your account.
          </p>
          <RouterLink :to="{ name: 'home' }">Open the start page</RouterLink>
        </template>
        <template v-else-if="vm.pendingLink.value">
          <p data-testid="sign-in-link-pending">
            {{ vm.pendingLink.value.providerDisplayName }} of {{ vm.pendingLink.value.tenantDisplayName }} confirmed
            the address <strong>{{ vm.pendingLink.value.email }}</strong>. When you link this sign-in, it signs you in to
            the account <strong>{{ vm.username.value ?? 'that you are signed in to' }}</strong>.
          </p>
          <p class="muted-hint">
            Link the sign-in only if you started it yourself. The identity provider of
            {{ vm.pendingLink.value.tenantDisplayName }} can then sign in to this account.
          </p>
          <p v-if="vm.errorMessage.value" class="error">{{ vm.errorMessage.value }}</p>
          <div class="form-actions">
            <button
              data-testid="sign-in-link-confirm"
              type="button"
              class="btn-primary"
              :disabled="vm.confirming.value"
              @click="vm.confirm"
            >
              {{ vm.confirming.value ? 'Linking…' : 'Link the sign-in' }}
            </button>
            <RouterLink class="btn-secondary" :to="{ name: 'settings' }">Cancel</RouterLink>
          </div>
        </template>
        <template v-else>
          <p class="error" data-testid="sign-in-link-error">{{ vm.errorMessage.value }}</p>
          <RouterLink :to="{ name: 'settings' }">Back to the settings</RouterLink>
        </template>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import { useSignInLinkConfirmationViewModel } from '@/features/settings/view-models/useSignInLinkConfirmationViewModel'

const vm = useSignInLinkConfirmationViewModel()
</script>

<style scoped>
.sign-in-link-page {
  max-width: 48rem;
}

.settings-page-header {
  margin-bottom: 1.5rem;
}

.sign-in-link-body {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.sign-in-link-body p {
  margin: 0;
}

.success-hint {
  color: var(--color-success);
  font-weight: 600;
}
</style>
