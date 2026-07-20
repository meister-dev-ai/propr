import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const pushMock = vi.fn()
const replaceMock = vi.fn()
const getClientMock = vi.fn()
const patchClientMock = vi.fn()
const hasClientRoleMock = vi.fn((_clientId: string, _minRole: number) => false)
const getCapabilityMock = vi.fn((key: string) => {
  if (key === 'procursor') {
    return { key, isAvailable: true, message: null }
  }

  return { key, isAvailable: true, message: null }
})

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')

  return {
    ...actual,
    useRoute: () => ({
      params: { id: 'client-1' },
      query: {},
    }),
    useRouter: () => ({
      push: pushMock,
      replace: replaceMock,
    }),
  }
})

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({
    GET: getClientMock,
    PATCH: patchClientMock,
    DELETE: vi.fn(),
  }),
}))

vi.mock('@/services/findingDismissalsService', () => ({
  dismissFinding: vi.fn(),
}))

vi.mock('@/services/promptOverridesService', () => ({
  listOverrides: vi.fn(),
  createOverride: vi.fn(),
  deleteOverride: vi.fn(),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    hasClientRole: hasClientRoleMock,
    getCapability: getCapabilityMock,
  }),
}))

async function mountView() {
  const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')

  return mount(ClientDetailView, {
    global: {
      stubs: {
        RouterLink: {
          props: ['to'],
          template: '<a :href="typeof to === \'string\' ? to : JSON.stringify(to)"><slot /></a>',
        },
        ClientCrawlConfigsTab: true,
        ClientWebhookConfigsTab: true,
        ClientProviderConnectionsTab: true,
        ClientAiConnectionsTab: true,
        ClientOverview: true,
        ClientProCursorTab: true,
        UsageDashboard: true,
        ConfirmDialog: true,
        ReviewHistorySection: true,
        TextViewerModal: true,
      },
    },
  })
}

describe('ClientDetailView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: number) => minRole === 0)
    getClientMock.mockResolvedValue({
      data: {
        id: 'client-1',
        displayName: 'Acme Review Team',
        isActive: true,
        createdAt: '2026-04-25T10:00:00Z',
        scmCommentPostingEnabled: true,
        enableEvidenceBackedVerification: false,
        enableMultiPassUnion: false,
        includeLinkedItemsInContext: true,
        enableLanguageRobustScreening: false,
      },
      response: { status: 200 },
    })
    patchClientMock.mockResolvedValue({
      data: {
        id: 'client-1',
        displayName: 'Acme Review Team',
        isActive: true,
        createdAt: '2026-04-25T10:00:00Z',
        scmCommentPostingEnabled: true,
        enableEvidenceBackedVerification: false,
        enableMultiPassUnion: false,
        includeLinkedItemsInContext: true,
        enableLanguageRobustScreening: false,
      },
    })
  })

  it('shows only read-only review and usage tabs for client users', async () => {
    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Review History')
    expect(wrapper.text()).toContain('Tokens & Usage')
    expect(wrapper.text()).not.toContain('System')
    expect(wrapper.text()).not.toContain('SCM Providers')
    expect(wrapper.text()).not.toContain('Dismissed Findings')
    expect(wrapper.text()).not.toContain('Prompt Overrides')
  }, 10_000)

  it('keeps configuration tabs visible for client administrators', async () => {
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: number) => minRole <= 1)

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('System')
    expect(wrapper.text()).toContain('SCM Providers')
    expect(wrapper.text()).toContain('Dismissed Findings')
    expect(wrapper.text()).toContain('Prompt Overrides')
  })

  it('shows advanced settings toggles only for client administrators', async () => {
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: number) => minRole <= 1)

    const adminWrapper = await mountView()
    await flushPromises()

    expect(adminWrapper.find('input[name="scmCommentPostingEnabled"]').exists()).toBe(true)
    expect(adminWrapper.find('input[name="enableEvidenceBackedVerification"]').exists()).toBe(true)
    expect(adminWrapper.find('input[name="enableMultiPassUnion"]').exists()).toBe(true)
    expect(adminWrapper.find('input[name="enableLanguageRobustScreening"]').exists()).toBe(true)

    hasClientRoleMock.mockImplementation((_clientId: string, minRole: number) => minRole === 0)

    const userWrapper = await mountView()
    await flushPromises()

    expect(userWrapper.find('input[name="scmCommentPostingEnabled"]').exists()).toBe(false)
    expect(userWrapper.find('input[name="enableEvidenceBackedVerification"]').exists()).toBe(false)
    expect(userWrapper.find('input[name="enableMultiPassUnion"]').exists()).toBe(false)
    expect(userWrapper.find('input[name="enableLanguageRobustScreening"]').exists()).toBe(false)
  })

  it('sends enableEvidenceBackedVerification when saving advanced settings', async () => {
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: number) => minRole <= 1)

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.find('input[name="enableEvidenceBackedVerification"]').setValue(true)
    await wrapper.find('button.scm-advanced-settings-save-btn').trigger('click')
    await flushPromises()

    expect(patchClientMock).toHaveBeenCalledWith('/clients/{clientId}', {
      params: { path: { clientId: 'client-1' } },
      body: {
        scmCommentPostingEnabled: true,
        enableEvidenceBackedVerification: true,
        enableMultiPassUnion: false,
        includeLinkedItemsInContext: true,
        enableLanguageRobustScreening: false,
        baselineReasoningEffort: 'none',
      },
    })
  })

  it('sends enableMultiPassUnion when saving advanced settings', async () => {
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: number) => minRole <= 1)

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.find('input[name="enableMultiPassUnion"]').setValue(true)
    await wrapper.find('button.scm-advanced-settings-save-btn').trigger('click')
    await flushPromises()

    expect(patchClientMock).toHaveBeenCalledWith('/clients/{clientId}', {
      params: { path: { clientId: 'client-1' } },
      body: {
        scmCommentPostingEnabled: true,
        enableEvidenceBackedVerification: false,
        enableMultiPassUnion: true,
        includeLinkedItemsInContext: true,
        enableLanguageRobustScreening: false,
        baselineReasoningEffort: 'none',
      },
    })
  })

  it('sends enableLanguageRobustScreening when saving advanced settings', async () => {
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: number) => minRole <= 1)

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.find('input[name="enableLanguageRobustScreening"]').setValue(true)
    await wrapper.find('button.scm-advanced-settings-save-btn').trigger('click')
    await flushPromises()

    expect(patchClientMock).toHaveBeenCalledWith('/clients/{clientId}', {
      params: { path: { clientId: 'client-1' } },
      body: {
        scmCommentPostingEnabled: true,
        enableEvidenceBackedVerification: false,
        enableMultiPassUnion: false,
        includeLinkedItemsInContext: true,
        enableLanguageRobustScreening: true,
        baselineReasoningEffort: 'none',
      },
    })
  })

  it('enables the budget Save button after entering a cap value', async () => {
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: number) => minRole <= 1)

    const wrapper = await mountView()
    await flushPromises()

    const budgetNav = wrapper.findAll('.sidebar-nav-link').find((b) => b.text().includes('Budget'))
    expect(budgetNav).toBeTruthy()
    await budgetNav!.trigger('click')
    await flushPromises()

    const saveBtn = wrapper.find('button.budget-save-btn')
    expect(saveBtn.exists()).toBe(true)
    // Nothing changed yet, so Save starts disabled.
    expect(saveBtn.attributes('disabled')).toBeDefined()

    await wrapper.find('input[name="monthlyBudgetSoftCapUsd"]').setValue('50')
    await flushPromises()

    // Entering a cap makes the config dirty, so Save must activate.
    expect(saveBtn.attributes('disabled')).toBeUndefined()
  })

})
