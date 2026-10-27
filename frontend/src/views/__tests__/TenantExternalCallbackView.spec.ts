import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const replaceMock = vi.fn()
const establishSessionMock = vi.fn()

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')

  return {
    ...actual,
    useRoute: () => ({
      params: {
        tenantSlug: 'acme',
      },
    }),
    useRouter: () => ({
      replace: replaceMock,
    }),
  }
})

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    establishSession: establishSessionMock,
  }),
}))

async function mountView(hash = '') {
  window.location.hash = hash
  const { default: TenantExternalCallbackView } = await import('@/features/tenants/views/TenantExternalCallbackView.vue')
  return mount(TenantExternalCallbackView)
}

describe('TenantExternalCallbackView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    window.location.hash = ''
    establishSessionMock.mockResolvedValue(undefined)
    replaceMock.mockResolvedValue(undefined)
  })

  it('establishes the session from the callback fragment and redirects home', async () => {
    const wrapper = await mountView('#accessToken=tenant-access&refreshToken=tenant-refresh&expiresIn=900&tokenType=Bearer')
    await flushPromises()

    expect(establishSessionMock).toHaveBeenCalledWith({
      accessToken: 'tenant-access',
      expiresIn: 900,
      tokenType: 'Bearer',
    })
    expect(replaceMock).toHaveBeenCalledWith({ name: 'home' })
    expect(wrapper.text()).toContain('Completing sign-in')
  })

  it('renders the callback failure message when tenant sign-in is rejected', async () => {
    const wrapper = await mountView('#error=disallowed_domain&message=Email%20domain%20%27gmail.com%27%20is%20not%20allowed%20for%20this%20tenant%20sign-in%20provider.')
    await flushPromises()

    expect(establishSessionMock).not.toHaveBeenCalled()
    expect(replaceMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain("Email domain 'gmail.com' is not allowed for this tenant sign-in provider.")
  })

  it('shows an error when session establishment fails', async () => {
    establishSessionMock.mockRejectedValue(new Error('session setup failed'))

    const wrapper = await mountView('#accessToken=tenant-access&refreshToken=tenant-refresh&expiresIn=900&tokenType=Bearer')
    await flushPromises()

    expect(establishSessionMock).toHaveBeenCalledWith({
      accessToken: 'tenant-access',
      expiresIn: 900,
      tokenType: 'Bearer',
    })
    expect(replaceMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Tenant sign-in could not be completed. Please try again or contact a tenant administrator.')
    expect(wrapper.text()).not.toContain('Signing you in and loading your access...')
    expect(window.location.hash).toBe('')
    expect(window.location.href).not.toContain('tenant-access')
  })

  it('reports an established session apart from a start page that does not load', async () => {
    replaceMock.mockRejectedValue(new Error('navigation failed'))

    const wrapper = await mountView('#accessToken=tenant-access&refreshToken=tenant-refresh&expiresIn=900&tokenType=Bearer')
    await flushPromises()

    expect(establishSessionMock).toHaveBeenCalledWith({
      accessToken: 'tenant-access',
      expiresIn: 900,
      tokenType: 'Bearer',
    })
    expect(replaceMock).toHaveBeenCalledWith({ name: 'home' })
    expect(wrapper.find('.error').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('Signing you in and loading your access...')
    expect(wrapper.get('[data-testid="tenant-callback-signed-in"]').text())
      .toBe('You are signed in, but the start page could not be loaded.')
    expect(wrapper.text()).toContain('Open the start page')
    expect(wrapper.text()).not.toContain('Back to tenant sign-in')
    expect(window.location.href).not.toContain('tenant-access')
  })
})
