// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const replaceMock = vi.fn()
const establishSessionMock = vi.fn()

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')

  return {
    ...actual,
    useRouter: () => ({ replace: replaceMock }),
  }
})

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ establishSession: establishSessionMock }),
}))

async function mountView(hash: string) {
  window.location.hash = hash
  const { default: LoginCallbackView } = await import('@/features/auth/views/LoginCallbackView.vue')
  return mount(LoginCallbackView)
}

describe('LoginCallbackView', () => {
  beforeEach(() => {
    window.location.hash = ''
    establishSessionMock.mockReset()
    establishSessionMock.mockResolvedValue(undefined)
    replaceMock.mockReset()
    replaceMock.mockResolvedValue(undefined)
  })

  it('establishes the session from the fragment and opens the start page', async () => {
    await mountView('#accessToken=platform-access&expiresIn=900&tokenType=Bearer')
    await flushPromises()

    expect(establishSessionMock).toHaveBeenCalledWith({
      accessToken: 'platform-access',
      expiresIn: 900,
      tokenType: 'Bearer',
    })
    expect(replaceMock).toHaveBeenCalledWith({ name: 'home' })
  })

  it('shows the reason of a refused sign-in and a way back to the sign-in page', async () => {
    const wrapper = await mountView(
      '#error=platform_role_missing&message=Your%20account%20at%20the%20identity%20provider%20does%20not%20hold%20the%20role.',
    )
    await flushPromises()

    expect(establishSessionMock).not.toHaveBeenCalled()
    expect(replaceMock).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="login-callback-error"]').text())
      .toBe('Your account at the identity provider does not hold the role.')
    expect(wrapper.text()).toContain('Back to sign-in')
  })

  it('shows a general reason when the fragment carries neither a session nor a message', async () => {
    const wrapper = await mountView('')
    await flushPromises()

    expect(establishSessionMock).not.toHaveBeenCalled()
    expect(replaceMock).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="login-callback-error"]').text())
      .toBe('The sign-in through the identity provider failed. Go back to the sign-in page and try again.')
  })

  it('shows an error when the session cannot be established, and leaves no token in the address', async () => {
    establishSessionMock.mockRejectedValue(new Error('profile failed'))

    const wrapper = await mountView('#accessToken=platform-access&expiresIn=900&tokenType=Bearer')
    await flushPromises()

    expect(replaceMock).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="login-callback-error"]').text())
      .toBe('The sign-in could not be completed. Go back to the sign-in page and try again.')
    expect(window.location.hash).toBe('')
    expect(window.location.href).not.toContain('platform-access')
  })

  it('reports an established session apart from a start page that does not load', async () => {
    replaceMock.mockRejectedValue(new Error('navigation failed'))

    const wrapper = await mountView('#accessToken=platform-access&expiresIn=900&tokenType=Bearer')
    await flushPromises()

    expect(establishSessionMock).toHaveBeenCalled()
    expect(wrapper.find('[data-testid="login-callback-error"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="login-callback-signed-in"]').text())
      .toBe('You are signed in, but the start page could not be loaded.')
    expect(wrapper.text()).toContain('Open the start page')
    expect(window.location.href).not.toContain('platform-access')
  })

  it('removes the fragment from the address before the session is established', async () => {
    let addressDuringSessionSetup = ''
    establishSessionMock.mockImplementation(async () => {
      addressDuringSessionSetup = window.location.href
    })

    await mountView('#accessToken=platform-access&expiresIn=900&tokenType=Bearer')
    await flushPromises()

    expect(addressDuringSessionSetup).not.toBe('')
    expect(addressDuringSessionSetup).not.toContain('platform-access')
  })
})
