import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const pushMock = vi.fn()
const replaceMock = vi.fn()
const getClientMock = vi.fn()
const hasClientRoleMock = vi.fn(() => false)
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
    PATCH: vi.fn(),
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
  const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')

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
      },
      response: { status: 200 },
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
  })

  it('keeps configuration tabs visible for client administrators', async () => {
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: number) => minRole <= 1)

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('System')
    expect(wrapper.text()).toContain('SCM Providers')
    expect(wrapper.text()).toContain('Dismissed Findings')
    expect(wrapper.text()).toContain('Prompt Overrides')
  })

  it('shows the SCM comment posting setting only for client administrators', async () => {
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: number) => minRole <= 1)

    const adminWrapper = await mountView()
    await flushPromises()

    expect(adminWrapper.find('input[name="scmCommentPostingEnabled"]').exists()).toBe(true)

    hasClientRoleMock.mockImplementation((_clientId: string, minRole: number) => minRole === 0)

    const userWrapper = await mountView()
    await flushPromises()

    expect(userWrapper.find('input[name="scmCommentPostingEnabled"]').exists()).toBe(false)
  })
})
