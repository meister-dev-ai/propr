<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="pats-view">
    <div class="pats-page-header">
      <h2 class="view-title">Personal Access Tokens</h2>
      <p class="pats-description">PATs let you authenticate API calls and ADO extension webhooks without your password. Treat them like passwords — they inherit your account permissions.</p>
    </div>

    <div class="section-card">
      <div class="section-card-header">
        <h3>Create new token</h3>
      </div>
      <div class="section-card-body">
        <div v-if="vm.createError.value" class="error">{{ vm.createError.value }}</div>

        <div v-if="vm.generatedToken.value" class="token-reveal">
          <p><strong>Copy this token now — it will not be shown again.</strong></p>
          <code>{{ vm.generatedToken.value }}</code>
          <div style="display: flex; gap: 0.5rem; margin-top: 1rem;">
            <button class="btn-primary" @click="vm.copyGeneratedToken">Copy</button>
            <button class="btn-secondary" @click="vm.dismissGeneratedToken">Dismiss</button>
          </div>
        </div>

        <form v-else class="token-create-form" @submit.prevent="vm.createToken">
          <div class="token-fields-grid">
            <div class="form-field">
              <label for="pat-label">Label</label>
              <input id="pat-label" v-model="vm.newLabel.value" type="text" placeholder="e.g. CI pipeline" />
            </div>
            <div class="form-field">
              <label for="pat-expires">Expires (optional)</label>
              <input id="pat-expires" v-model="vm.newExpires.value" type="datetime-local" />
            </div>
          </div>
          <div class="form-actions" style="margin-top: 0;">
            <button class="btn-primary" type="submit" :disabled="vm.creating.value">
              <i class="fi fi-rr-key"></i> {{ vm.creating.value ? 'Creating…' : 'Generate token' }}
            </button>
          </div>
        </form>
      </div>
    </div>

    <div class="section-card">
      <div class="section-card-header">
        <h3>Active tokens</h3>
        <span class="chip chip-muted">{{ vm.pats.value.length }} token{{ vm.pats.value.length === 1 ? '' : 's' }}</span>
      </div>
      <div v-if="vm.loadError.value" class="error" style="padding: 1rem 1.25rem;">{{ vm.loadError.value }}</div>
      <div v-else-if="vm.loading.value" class="loading" style="padding: 1rem 1.25rem;">Loading…</div>
      <table v-else-if="vm.pats.value.length">
        <thead>
          <tr>
            <th>Label</th>
            <th>Created</th>
            <th>Last used</th>
            <th>Expires</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="pat in vm.pats.value" :key="pat.id">
            <td>{{ pat.label }}</td>
            <td>{{ vm.formatDate(pat.createdAt) }}</td>
            <td>{{ pat.lastUsedAt ? vm.formatDate(pat.lastUsedAt) : '—' }}</td>
            <td>{{ pat.expiresAt ? vm.formatDate(pat.expiresAt) : 'Never' }}</td>
            <td>
              <button class="btn-danger" @click="vm.revokeToken(pat.id)" :disabled="vm.revoking.value === pat.id">
                {{ vm.revoking.value === pat.id ? 'Revoking…' : 'Revoke' }}
              </button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else class="pats-empty">No active tokens.</p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { usePatsViewModel } from '@/features/pats/view-models/usePatsViewModel'

const vm = usePatsViewModel()
</script>

<style scoped>
.pats-page-header {
  margin-bottom: 1.5rem;
}

.pats-description {
  color: var(--color-text-muted);
  font-size: 0.9rem;
  margin: 0.5rem 0 0;
}

.token-fields-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0.75rem;
  margin-bottom: 1rem;
}

.token-reveal {
  background: var(--color-bg);
  border: 1px solid var(--color-success);
  border-radius: var(--radius-lg);
  padding: 1.25rem;
  margin-bottom: 1rem;
}

.token-reveal code {
  display: block;
  font-family: monospace;
  font-size: 0.875rem;
  word-break: break-all;
  background: var(--color-surface);
  color: var(--color-accent);
  padding: 0.875rem 1rem;
  border-radius: var(--radius-md);
  margin: 0.75rem 0;
}

.pats-empty {
  padding: 2rem 1.25rem;
  color: var(--color-text-muted);
  font-size: 0.875rem;
  margin: 0;
}
</style>
