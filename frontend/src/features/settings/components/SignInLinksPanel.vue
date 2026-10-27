<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="sign-in-links">
    <div class="settings-page-header">
      <h2 class="view-title">Single sign-on</h2>
      <p class="settings-description">
        The identity providers of tenants that sign you in to this account. A person who belongs to several tenants links
        the sign-in of each tenant to one account.
      </p>
    </div>

    <div v-if="vm.pendingLink.value" class="section-card" data-testid="sign-in-links-pending">
      <div class="section-card-body sign-in-links-pending">
        <p>
          A sign-in through {{ vm.pendingLink.value.providerDisplayName }} of
          {{ vm.pendingLink.value.tenantDisplayName }} waits to be linked to an account.
        </p>
        <RouterLink class="btn-secondary" :to="{ name: 'sign-in-link' }">Review the sign-in</RouterLink>
      </div>
    </div>

    <div class="section-card">
      <div class="section-card-header">
        <h3>Linked sign-ins</h3>
      </div>
      <div class="section-card-body">
        <p v-if="vm.loadError.value" class="error">{{ vm.loadError.value }}</p>
        <p v-else-if="vm.loading.value" class="muted-hint">Loading…</p>
        <p v-else-if="vm.links.value.length === 0" class="muted-hint" data-testid="sign-in-links-empty">
          No sign-in of a tenant is linked to this account.
        </p>
        <ul v-else class="sign-in-links-list" data-testid="sign-in-links-list">
          <li v-for="link in vm.links.value" :key="`${link.tenantSlug}-${link.providerDisplayName}`">
            <strong>{{ link.providerDisplayName }}</strong>
            <span class="muted-hint">({{ link.providerLabel }}) of {{ link.tenantDisplayName }}</span>
            <span>{{ link.email }}</span>
            <span v-if="link.lastSignInAt" class="muted-hint">Last sign-in {{ formatDateTime(link.lastSignInAt) }}</span>
          </li>
        </ul>
      </div>
    </div>

    <div class="section-card">
      <div class="section-card-header">
        <h3>Link a sign-in of a tenant</h3>
      </div>
      <div class="section-card-body sign-in-links-new">
        <p class="muted-hint">
          Enter the slug of the tenant and choose its identity provider. ProPR sends you to the provider and asks you to
          confirm the link when you return.
        </p>
        <form class="sign-in-links-form" @submit.prevent="vm.findProviders">
          <div class="form-field">
            <label for="sign-in-link-tenant">Tenant slug</label>
            <input
              id="sign-in-link-tenant"
              v-model="vm.tenantSlug.value"
              data-testid="sign-in-link-tenant"
              type="text"
              placeholder="acme"
              autocomplete="organization"
            />
          </div>
          <button class="btn-secondary" type="submit" :disabled="vm.loadingProviders.value">
            {{ vm.loadingProviders.value ? 'Loading…' : 'Show providers' }}
          </button>
        </form>
        <p v-if="vm.providersError.value" class="error">{{ vm.providersError.value }}</p>
        <div v-if="vm.providers.value.length" class="sign-in-links-providers">
          <a
            v-for="provider in vm.providers.value"
            :key="provider.providerId"
            :data-testid="`sign-in-link-provider-${provider.providerId}`"
            class="btn-primary"
            :href="vm.linkHref(provider.providerId)"
          >
            Link {{ provider.displayName }}
          </a>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import { formatDateTime } from '@/components/usageDashboardFormatters'
import { useSignInLinksViewModel } from '@/features/settings/view-models/useSignInLinksViewModel'

const vm = useSignInLinksViewModel()
</script>

<style scoped>
.settings-page-header {
  margin-bottom: 1.5rem;
}

.settings-description {
  color: var(--color-text-muted);
  margin: 0.5rem 0 0;
}

.sign-in-links-pending,
.sign-in-links-new {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.75rem;
}

.sign-in-links-pending p,
.sign-in-links-new p {
  margin: 0;
}

.sign-in-links-list {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

.sign-in-links-list li {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  align-items: baseline;
}

.sign-in-links-form {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 0.75rem;
}

.sign-in-links-providers {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem;
}
</style>
