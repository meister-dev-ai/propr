<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="page-with-sidebar">
    <aside class="page-sidebar">
      <div class="sidebar-nav">
        <div class="sidebar-nav-group">
          <h4>Account</h4>
          <button
            class="sidebar-nav-link"
            :class="{ active: activeTab === 'profile' }"
            @click="activeTab = 'profile'"
          >
            <i class="fi fi-rr-user-lock"></i> Profile & Password
          </button>
          <button
            class="sidebar-nav-link"
            :class="{ active: activeTab === 'pats' }"
            @click="activeTab = 'pats'"
          >
            <i class="fi fi-rr-key"></i> Personal Access Tokens
          </button>
        </div>
      </div>
    </aside>

    <main class="page-main-content">
      <div v-show="activeTab === 'profile'">
        <div class="settings-page-header">
          <h2 class="view-title">Profile & Password</h2>
          <p class="settings-description">
            <template v-if="vm.hasLocalPassword.value">
              Change the password for
              <strong>{{ vm.usernameLabel.value }}</strong>.
            </template>
            <template v-else>
              Review how
              <strong>{{ vm.usernameLabel.value }}</strong>
              signs in.
            </template>
          </p>
        </div>

        <div class="section-card settings-card">
          <div class="section-card-header settings-card-header">
            <div>
              <h3>Password</h3>
              <p v-if="vm.hasLocalPassword.value" class="settings-subtitle">Changing your password revokes refresh tokens. Personal access tokens remain valid.</p>
              <p v-else class="settings-subtitle">This account signs in through single sign-on and does not have a local password to change here.</p>
            </div>
            <span class="chip chip-muted">{{ vm.usernameLabel.value }}</span>
          </div>

          <div v-if="vm.hasLocalPassword.value" class="section-card-body">
            <form class="settings-form" @submit.prevent="vm.changePassword">
              <div class="settings-form-grid">
                <div class="form-field">
                  <label for="currentPassword">Current password</label>
                  <input id="currentPassword" v-model="vm.form.currentPassword" name="currentPassword" type="password" autocomplete="current-password" />
                </div>
                <div class="form-field">
                  <label for="newPassword">New password</label>
                  <input id="newPassword" v-model="vm.form.newPassword" name="newPassword" type="password" autocomplete="new-password" />
                </div>
                <div class="form-field settings-form-grid-full">
                  <label for="confirmPassword">Confirm new password</label>
                  <input id="confirmPassword" v-model="vm.form.confirmPassword" name="confirmPassword" type="password" autocomplete="new-password" />
                </div>
              </div>

              <p class="muted-hint">Use at least 8 characters.</p>
              <p v-if="vm.errorMessage.value" class="error">{{ vm.errorMessage.value }}</p>
              <p v-if="vm.successMessage.value" class="success-hint">{{ vm.successMessage.value }}</p>

              <div class="form-actions">
                <button class="btn-primary" type="submit" :disabled="vm.isSaving.value">
                  {{ vm.isSaving.value ? 'Saving…' : 'Update password' }}
                </button>
              </div>
            </form>
          </div>

          <div v-else class="section-card-body">
            <p class="muted-hint">
              Use your organization's identity provider to manage your sign-in credentials. Personal access tokens remain available from this page.
            </p>
          </div>
        </div>
      </div>

      <div v-show="activeTab === 'pats'">
        <PatsView />
      </div>
    </main>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useSettingsViewModel } from '@/features/settings/view-models/useSettingsViewModel'
import PatsView from '@/features/pats/views/PatsView.vue'

const vm = useSettingsViewModel()
const activeTab = ref<'profile' | 'pats'>('profile')
</script>

<style scoped>
.settings-page-header {
  margin-bottom: 1.5rem;
}

.settings-description,
.settings-subtitle {
  color: var(--color-text-muted);
  margin: 0.5rem 0 0;
}

.settings-card-header {
  gap: 1rem;
}

.settings-form {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.settings-form-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0.75rem;
}

.settings-form-grid-full {
  grid-column: 1 / -1;
}

.success-hint {
  margin: 0;
  color: var(--color-success);
  font-weight: 600;
}

@media (max-width: 760px) {
  .settings-form-grid {
    grid-template-columns: 1fr;
  }

  .settings-form-grid-full {
    grid-column: auto;
  }
}
</style>
