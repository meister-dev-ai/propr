// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'

const usernameSource = ref<string | null>('admin@example.com')
const hasLocalPasswordSource = ref(true)
const routerPushMock = vi.fn()
const changePasswordServiceMock = vi.fn(async (_payload: { currentPassword: string; newPassword: string }) => {})

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    username: usernameSource,
    hasLocalPassword: hasLocalPasswordSource,
  }),
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: routerPushMock }),
}))

vi.mock('@/services/userSecurityService', () => ({
  changeMyPassword: vi.fn(),
  ApiRequestError: class ApiRequestError extends Error {
    status: number
    constructor(message: string, status: number) {
      super(message)
      this.status = status
    }
  },
}))

vi.mock('@/services/api', () => ({
  UnauthorizedError: class UnauthorizedError extends Error {},
}))

import { useSettingsViewModel } from '@/features/settings/view-models/useSettingsViewModel'
import { ApiRequestError } from '@/services/userSecurityService'
import { UnauthorizedError } from '@/services/api'

describe('useSettingsViewModel (FR-007, FR-008, FR-012)', () => {
  beforeEach(() => {
    usernameSource.value = 'admin@example.com'
    hasLocalPasswordSource.value = true
    routerPushMock.mockReset()
    changePasswordServiceMock.mockReset()
    changePasswordServiceMock.mockResolvedValue(undefined)
  })

  it('starts in idle state with an empty form', () => {
    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })
    expect(vm.state.value.status).toBe('idle')
    expect(vm.form.currentPassword).toBe('')
    expect(vm.form.newPassword).toBe('')
    expect(vm.form.confirmPassword).toBe('')
  })

  it('exposes the username through usernameLabel and falls back when null', () => {
    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })
    expect(vm.usernameLabel.value).toBe('admin@example.com')

    usernameSource.value = null
    expect(vm.usernameLabel.value).toBe('current account')
  })

  it('mirrors reactive session password capability for the view shell', () => {
    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })

    expect(vm.hasLocalPassword.value).toBe(true)

    hasLocalPasswordSource.value = false

    expect(vm.hasLocalPassword.value).toBe(false)
  })

  it('rejects empty fields with validation_error and does not call the service', async () => {
    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })
    await vm.changePassword()
    expect(vm.state.value.status).toBe('validation_error')
    expect(vm.errorMessage.value).toBe('All password fields are required.')
    expect(changePasswordServiceMock).not.toHaveBeenCalled()
  })

  it('rejects new passwords shorter than 8 characters', async () => {
    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })
    vm.form.currentPassword = 'oldpass'
    vm.form.newPassword = 'short'
    vm.form.confirmPassword = 'short'
    await vm.changePassword()
    expect(vm.state.value.status).toBe('validation_error')
    expect(vm.errorMessage.value).toBe('New password must be at least 8 characters.')
    expect(changePasswordServiceMock).not.toHaveBeenCalled()
  })

  it('rejects mismatched confirmation', async () => {
    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })
    vm.form.currentPassword = 'oldpass'
    vm.form.newPassword = 'newpassword123'
    vm.form.confirmPassword = 'different-confirmation'
    await vm.changePassword()
    expect(vm.state.value.status).toBe('validation_error')
    expect(vm.errorMessage.value).toBe('New password confirmation does not match.')
    expect(changePasswordServiceMock).not.toHaveBeenCalled()
  })

  it('calls the service with current and new password on valid submit', async () => {
    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })
    vm.form.currentPassword = 'oldpass'
    vm.form.newPassword = 'newpassword123'
    vm.form.confirmPassword = 'newpassword123'
    await vm.changePassword()
    expect(changePasswordServiceMock).toHaveBeenCalledWith({
      currentPassword: 'oldpass',
      newPassword: 'newpassword123',
    })
  })

  it('transitions to success and resets the form after a successful change', async () => {
    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })
    vm.form.currentPassword = 'oldpass'
    vm.form.newPassword = 'newpassword123'
    vm.form.confirmPassword = 'newpassword123'
    await vm.changePassword()
    expect(vm.state.value.status).toBe('success')
    expect(vm.successMessage.value).toBe('Password changed. Refresh tokens were revoked and PATs remain valid.')
    expect(vm.form.currentPassword).toBe('')
    expect(vm.form.newPassword).toBe('')
    expect(vm.form.confirmPassword).toBe('')
  })

  it('maps an ApiRequestError message into error state without redirecting', async () => {
    changePasswordServiceMock.mockRejectedValueOnce(new ApiRequestError('Current password is incorrect.', 400))
    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })
    vm.form.currentPassword = 'oldpass'
    vm.form.newPassword = 'newpassword123'
    vm.form.confirmPassword = 'newpassword123'
    await vm.changePassword()
    expect(vm.state.value.status).toBe('error')
    expect(vm.errorMessage.value).toBe('Current password is incorrect.')
    expect(routerPushMock).not.toHaveBeenCalled()
  })

  it('falls back to a generic error message for unknown failures', async () => {
    changePasswordServiceMock.mockRejectedValueOnce(new Error('boom'))
    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })
    vm.form.currentPassword = 'oldpass'
    vm.form.newPassword = 'newpassword123'
    vm.form.confirmPassword = 'newpassword123'
    await vm.changePassword()
    expect(vm.state.value.status).toBe('error')
    expect(vm.errorMessage.value).toBe('Failed to change password.')
  })

  it('redirects to login when the service rejects with UnauthorizedError and does not set error state', async () => {
    changePasswordServiceMock.mockRejectedValueOnce(new UnauthorizedError())
    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })
    vm.form.currentPassword = 'oldpass'
    vm.form.newPassword = 'newpassword123'
    vm.form.confirmPassword = 'newpassword123'
    await vm.changePassword()
    expect(routerPushMock).toHaveBeenCalledWith({ name: 'login' })
    expect(vm.state.value.status).not.toBe('error')
  })

  it('resetForm clears all fields without changing UI state', () => {
    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })
    vm.form.currentPassword = 'a'
    vm.form.newPassword = 'b'
    vm.form.confirmPassword = 'c'
    vm.resetForm()
    expect(vm.form.currentPassword).toBe('')
    expect(vm.form.newPassword).toBe('')
    expect(vm.form.confirmPassword).toBe('')
    expect(vm.state.value.status).toBe('idle')
  })

  it('exposes isSaving=true while the service call is in flight', async () => {
    let resolveCall: (() => void) | undefined
    changePasswordServiceMock.mockImplementationOnce(
      () => new Promise<void>((resolve) => {
        resolveCall = resolve
      }),
    )

    const vm = useSettingsViewModel({ changePasswordService: changePasswordServiceMock })
    vm.form.currentPassword = 'oldpass'
    vm.form.newPassword = 'newpassword123'
    vm.form.confirmPassword = 'newpassword123'
    const pending = vm.changePassword()
    expect(vm.isSaving.value).toBe(true)

    resolveCall?.()
    await pending
    expect(vm.isSaving.value).toBe(false)
    expect(vm.state.value.status).toBe('success')
  })
})
