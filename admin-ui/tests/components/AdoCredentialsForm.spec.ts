import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

const mockPut = vi.fn()
const mockDelete = vi.fn()
vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ PUT: mockPut, DELETE: mockDelete })),
  UnauthorizedError: class UnauthorizedError extends Error {},
}))

async function importAdoCredentialsForm() {
  const mod = await import('@/components/AdoCredentialsForm.vue')
  return mod.default
}

describe('AdoCredentialsForm', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('renders tenantId, clientId, and secret inputs', async () => {
    const AdoCredentialsForm = await importAdoCredentialsForm()
    const wrapper = mount(AdoCredentialsForm, {
      props: { clientId: 'client-1', hasCredentials: false },
    })
    expect(wrapper.find('input[name="tenantId"]').exists()).toBe(true)
    expect(wrapper.find('input[name="clientId"]').exists()).toBe(true)
    const secretInput = wrapper.find('input[name="secret"]')
    expect(secretInput.exists()).toBe(true)
    expect((secretInput.element as HTMLInputElement).type).toBe('password')
  })

  it('secret input is never pre-populated', async () => {
    const AdoCredentialsForm = await importAdoCredentialsForm()
    const wrapper = mount(AdoCredentialsForm, {
      props: { clientId: 'client-1', hasCredentials: true },
    })
    const secretInput = wrapper.find('input[name="secret"]')
    expect((secretInput.element as HTMLInputElement).value).toBe('')
  })

  it('calls PUT /clients/{clientId}/ado-credentials with form data on save', async () => {
    mockPut.mockResolvedValue({ response: { ok: true } })
    const AdoCredentialsForm = await importAdoCredentialsForm()
    const wrapper = mount(AdoCredentialsForm, {
      props: { clientId: 'client-1', hasCredentials: false },
    })
    await wrapper.find('input[name="tenantId"]').setValue('tenant-abc')
    await wrapper.find('input[name="clientId"]').setValue('client-abc')
    await wrapper.find('input[name="secret"]').setValue('my-secret')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(mockPut).toHaveBeenCalledWith(
      '/clients/{clientId}/ado-credentials',
      expect.objectContaining({
        params: { path: { clientId: 'client-1' } },
        body: { tenantId: 'tenant-abc', clientId: 'client-abc', secret: 'my-secret' },
      })
    )
  })

  it('calls DELETE on Clear button and emits credentials-cleared', async () => {
    mockDelete.mockResolvedValue({ response: { ok: true } })
    const AdoCredentialsForm = await importAdoCredentialsForm()
    const wrapper = mount(AdoCredentialsForm, {
      props: { clientId: 'client-1', hasCredentials: true },
    })
    await wrapper.find('button.clear-btn').trigger('click')
    await flushPromises()
    expect(mockDelete).toHaveBeenCalledWith(
      '/clients/{clientId}/ado-credentials',
      expect.objectContaining({ params: { path: { clientId: 'client-1' } } })
    )
    expect(wrapper.emitted('credentials-cleared')).toBeTruthy()
  })
})
