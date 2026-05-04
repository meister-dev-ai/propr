<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="page-with-sidebar">
    <!-- Sidebar -->
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

    <!-- Main Content -->
    <main class="page-main-content">
      <div v-show="activeTab === 'profile'">
        <div class="settings-page-header">
          <h2 class="view-title">Profile & Password</h2>
          <p class="settings-description">
            <template v-if="hasLocalPassword">
              Change the password for
              <strong>{{ usernameLabel }}</strong>.
            </template>
            <template v-else>
              Review how
              <strong>{{ usernameLabel }}</strong>
              signs in.
            </template>
          </p>
        </div>

        <div class="section-card settings-card">
          <div class="section-card-header settings-card-header">
            <div>
              <h3>Password</h3>
              <p v-if="hasLocalPassword" class="settings-subtitle">Changing your password revokes refresh tokens. Personal access tokens remain valid.</p>
              <p v-else class="settings-subtitle">This account signs in through single sign-on and does not have a local password to change here.</p>
            </div>
            <span class="chip chip-muted">{{ usernameLabel }}</span>
          </div>

          <div v-if="hasLocalPassword" class="section-card-body">
            <form class="settings-form" @submit.prevent="handleSubmit">
              <div class="settings-form-grid">
                <div class="form-field">
                  <label for="currentPassword">Current password</label>
                  <input id="currentPassword" v-model="form.currentPassword" name="currentPassword" type="password" autocomplete="current-password" />
                </div>
                <div class="form-field">
                  <label for="newPassword">New password</label>
                  <input id="newPassword" v-model="form.newPassword" name="newPassword" type="password" autocomplete="new-password" />
                </div>
                <div class="form-field settings-form-grid-full">
                  <label for="confirmPassword">Confirm new password</label>
                  <input id="confirmPassword" v-model="form.confirmPassword" name="confirmPassword" type="password" autocomplete="new-password" />
                </div>
              </div>

              <p class="muted-hint">Use at least 8 characters.</p>
              <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
              <p v-if="successMessage" class="success-hint">{{ successMessage }}</p>

              <div class="form-actions">
                <button class="btn-primary" type="submit" :disabled="saving">
                  {{ saving ? 'Saving…' : 'Update password' }}
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
import { computed, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { UnauthorizedError } from '@/services/api'
import { ApiRequestError, changeMyPassword } from '@/services/userSecurityService'
import { useSession } from '@/composables/useSession'
import PatsView from '@/views/PatsView.vue'

const router = useRouter()
const { username, hasLocalPassword } = useSession()

const activeTab = ref<'profile' | 'pats'>('profile')

const form = reactive({
  currentPassword: '',
  newPassword: '',
  confirmPassword: '',
})

const saving = ref(false)
const errorMessage = ref('')
const successMessage = ref('')

const usernameLabel = computed(() => username.value ?? 'current account')

function resetForm() {
  form.currentPassword = ''
  form.newPassword = ''
  form.confirmPassword = ''
}

async function handleSubmit() {
  errorMessage.value = ''
  successMessage.value = ''

  if (!form.currentPassword || !form.newPassword || !form.confirmPassword) {
    errorMessage.value = 'All password fields are required.'
    return
  }

  if (form.newPassword.length < 8) {
    errorMessage.value = 'New password must be at least 8 characters.'
    return
  }

  if (form.newPassword !== form.confirmPassword) {
    errorMessage.value = 'New password confirmation does not match.'
    return
  }

  saving.value = true
  try {
    await changeMyPassword({
      currentPassword: form.currentPassword,
      newPassword: form.newPassword,
    })
    resetForm()
    successMessage.value = 'Password changed. Refresh tokens were revoked and PATs remain valid.'
  } catch (error) {
    if (error instanceof UnauthorizedError) {
      router.push({ name: 'login' })
      return
    }

    if (error instanceof ApiRequestError) {
      errorMessage.value = error.message
      return
    }

    errorMessage.value = 'Failed to change password.'
  } finally {
    saving.value = false
  }
}
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
