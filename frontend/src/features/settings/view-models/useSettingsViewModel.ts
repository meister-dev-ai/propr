// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, reactive, ref, type ComputedRef, type Ref } from 'vue'
import { useRouter } from 'vue-router'
import { UnauthorizedError } from '@/services/api'
import { ApiRequestError, changeMyPassword } from '@/services/userSecurityService'
import { useSession } from '@/composables/useSession'
import {
  error as errorState,
  idle,
  saving,
  success,
  validationError,
  type UiState,
} from '@/types/uiState'

export interface PasswordChangeForm {
  currentPassword: string
  newPassword: string
  confirmPassword: string
}

export interface SettingsViewModel {
  readonly name: 'useSettingsViewModel'
  state: Ref<UiState<null, string>>
  form: PasswordChangeForm
  usernameLabel: ComputedRef<string>
  hasLocalPassword: Ref<boolean>
  isSaving: ComputedRef<boolean>
  errorMessage: ComputedRef<string>
  successMessage: ComputedRef<string>
  changePassword: () => Promise<void>
  resetForm: () => void
}

export interface UseSettingsViewModelOptions {
  /** Override the password-change service call. Used by tests. */
  changePasswordService?: typeof changeMyPassword
}

export function useSettingsViewModel(options: UseSettingsViewModelOptions = {}): SettingsViewModel {
  const { username, hasLocalPassword } = useSession()
  const router = useRouter()
  const changePasswordFn = options.changePasswordService ?? changeMyPassword

  const state = ref<UiState<null, string>>(idle())
  const form = reactive<PasswordChangeForm>({
    currentPassword: '',
    newPassword: '',
    confirmPassword: '',
  })

  const usernameLabel = computed(() => username.value ?? 'current account')
  const isSaving = computed(() => state.value.status === 'saving')
  const errorMessage = computed(() =>
    state.value.status === 'validation_error' || state.value.status === 'error'
      ? state.value.message ?? ''
      : '',
  )
  const successMessage = computed(() =>
    state.value.status === 'success' ? state.value.message ?? '' : '',
  )

  function resetForm(): void {
    form.currentPassword = ''
    form.newPassword = ''
    form.confirmPassword = ''
  }

  async function changePassword(): Promise<void> {
    if (!form.currentPassword || !form.newPassword || !form.confirmPassword) {
      state.value = validationError('All password fields are required.')
      return
    }
    if (form.newPassword.length < 8) {
      state.value = validationError('New password must be at least 8 characters.')
      return
    }
    if (form.newPassword !== form.confirmPassword) {
      state.value = validationError('New password confirmation does not match.')
      return
    }

    state.value = saving()
    try {
      await changePasswordFn({
        currentPassword: form.currentPassword,
        newPassword: form.newPassword,
      })
      resetForm()
      state.value = success(null, 'Password changed. Refresh tokens were revoked and PATs remain valid.')
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        router.push({ name: 'login' })
        return
      }
      if (err instanceof ApiRequestError) {
        state.value = errorState(err.message)
        return
      }
      state.value = errorState('Failed to change password.')
    }
  }

  return {
    name: 'useSettingsViewModel',
    state,
    form,
    usernameLabel,
    hasLocalPassword,
    isSaving,
    errorMessage,
    successMessage,
    changePassword,
    resetForm,
  }
}
