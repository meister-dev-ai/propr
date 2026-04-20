// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const listProviderConnectionsMock = vi.fn()
const createProviderConnectionMock = vi.fn()
const updateProviderConnectionMock = vi.fn()
const verifyProviderConnectionMock = vi.fn()
const deleteProviderConnectionMock = vi.fn()
const listProviderScopesMock = vi.fn()
const createProviderScopeMock = vi.fn()
const updateProviderScopeMock = vi.fn()
const deleteProviderScopeMock = vi.fn()
const resolveReviewerIdentityCandidatesMock = vi.fn()
const getReviewerIdentityMock = vi.fn()
const setReviewerIdentityMock = vi.fn()
const deleteReviewerIdentityMock = vi.fn()
const listProviderActivationStatusesMock = vi.fn()
const notifyMock = vi.fn()

vi.mock('@/services/providerActivationService', () => ({
  getEnabledProviderOptions: (statuses: Array<{ providerFamily: string; isEnabled: boolean }>) => statuses
    .filter((status) => status.isEnabled)
    .map((status) => ({
      value: status.providerFamily,
      label: status.providerFamily === 'azureDevOps'
        ? 'Azure DevOps'
        : status.providerFamily === 'gitLab'
          ? 'GitLab'
          : status.providerFamily === 'forgejo'
            ? 'Forgejo'
            : 'GitHub',
    })),
  getProviderDefaultHostBaseUrl: (providerFamily: string) => {
    switch (providerFamily) {
      case 'azureDevOps':
        return 'https://dev.azure.com'
      case 'gitLab':
        return 'https://gitlab.com'
      case 'forgejo':
        return 'https://codeberg.org'
      default:
        return 'https://github.com'
    }
  },
  getSupportedAuthenticationKind: (providerFamily: string) => providerFamily === 'azureDevOps' ? 'oauthClientCredentials' : 'personalAccessToken',
  listProviderActivationStatuses: listProviderActivationStatusesMock,
}))

vi.mock('@/services/providerConnectionsService', () => ({
  listProviderConnections: listProviderConnectionsMock,
  createProviderConnection: createProviderConnectionMock,
  updateProviderConnection: updateProviderConnectionMock,
  verifyProviderConnection: verifyProviderConnectionMock,
  deleteProviderConnection: deleteProviderConnectionMock,
  listProviderScopes: listProviderScopesMock,
  createProviderScope: createProviderScopeMock,
  updateProviderScope: updateProviderScopeMock,
  deleteProviderScope: deleteProviderScopeMock,
  resolveReviewerIdentityCandidates: resolveReviewerIdentityCandidatesMock,
  getReviewerIdentity: getReviewerIdentityMock,
  setReviewerIdentity: setReviewerIdentityMock,
  deleteReviewerIdentity: deleteReviewerIdentityMock,
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({
    notify: notifyMock,
  }),
}))

function createDeferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((innerResolve, innerReject) => {
    resolve = innerResolve
    reject = innerReject
  })

  return { promise, resolve, reject }
}

async function mountTab() {
  const { default: ClientProviderConnectionsTab } = await import('@/components/ClientProviderConnectionsTab.vue')
  return mount(ClientProviderConnectionsTab, {
    props: {
      clientId: 'client-1',
    },
  })
}

describe('ClientProviderConnectionsTab', () => {
  beforeEach(() => {
    vi.clearAllMocks()

    listProviderActivationStatusesMock.mockResolvedValue([
      { providerFamily: 'azureDevOps', isEnabled: true },
      { providerFamily: 'github', isEnabled: true },
      { providerFamily: 'gitLab', isEnabled: true },
      { providerFamily: 'forgejo', isEnabled: true },
    ])

    listProviderConnectionsMock.mockResolvedValue([
      {
        id: 'provider-conn-1',
        clientId: 'client-1',
        providerFamily: 'github',
        hostBaseUrl: 'https://github.com',
        authenticationKind: 'personalAccessToken',
        displayName: 'GitHub Cloud',
        isActive: true,
        verificationStatus: 'verified',
        readinessLevel: 'workflowComplete',
        readinessReason: 'Connection meets onboarding and workflow-complete readiness criteria.',
        hostVariant: 'hosted',
        missingReadinessCriteria: [],
        lastVerifiedAt: '2026-04-13T21:00:00Z',
        lastVerificationError: null,
        createdAt: '2026-04-13T20:00:00Z',
        updatedAt: '2026-04-13T21:00:00Z',
      },
    ])

    listProviderScopesMock.mockResolvedValue([
      {
        id: 'scope-1',
        clientId: 'client-1',
        connectionId: 'provider-conn-1',
        scopeType: 'organization',
        externalScopeId: 'acme',
        scopePath: 'acme',
        displayName: 'Acme',
        verificationStatus: 'verified',
        isEnabled: true,
        lastVerifiedAt: '2026-04-13T21:00:00Z',
        lastVerificationError: null,
        createdAt: '2026-04-13T20:00:00Z',
        updatedAt: '2026-04-13T21:00:00Z',
      },
    ])

    getReviewerIdentityMock.mockResolvedValue({
      id: 'reviewer-1',
      clientId: 'client-1',
      connectionId: 'provider-conn-1',
      providerFamily: 'github',
      externalUserId: '42',
      login: 'meister-review-bot[bot]',
      displayName: 'Meister Review Bot',
      isBot: true,
      updatedAt: '2026-04-13T21:00:00Z',
    })
  })

  it('loads connections, scopes, and reviewer identity for the selected connection', async () => {
    const wrapper = await mountTab()
    await flushPromises()

    expect(listProviderConnectionsMock).toHaveBeenCalledWith('client-1')
    expect(listProviderScopesMock).toHaveBeenCalledWith('client-1', 'provider-conn-1')
    expect(getReviewerIdentityMock).toHaveBeenCalledWith('client-1', 'provider-conn-1')
    expect(wrapper.text()).toContain('GitHub Cloud')
    expect(wrapper.text()).toContain('Workflow Complete')
    expect(wrapper.text()).toContain('Acme')
    expect(wrapper.text()).toContain('Meister Review Bot')
  })

  it('filters disabled provider families out of the create selector', async () => {
    listProviderActivationStatusesMock.mockResolvedValue([
      { providerFamily: 'azureDevOps', isEnabled: true },
      { providerFamily: 'gitLab', isEnabled: true },
      { providerFamily: 'github', isEnabled: false },
      { providerFamily: 'forgejo', isEnabled: false },
    ])

    const wrapper = await mountTab()
    await flushPromises()

    await wrapper.get('.provider-create-toggle').trigger('click')

    const providerOptions = wrapper.findAll('select').at(0)?.findAll('option').map((option) => option.text())
    expect(providerOptions).toEqual(['Azure DevOps', 'GitLab'])
  })

  it('creates a provider connection from the inline form', async () => {
    createProviderConnectionMock.mockResolvedValue({
      id: 'provider-conn-2',
      clientId: 'client-1',
      providerFamily: 'gitLab',
      hostBaseUrl: 'https://gitlab.example.com',
      authenticationKind: 'personalAccessToken',
      displayName: 'GitLab Self-Hosted',
      isActive: true,
      verificationStatus: 'unknown',
      readinessLevel: 'configured',
      readinessReason: 'Connection has not completed onboarding verification yet.',
      hostVariant: 'selfHosted',
      missingReadinessCriteria: ['Connection has not been verified yet.'],
      lastVerifiedAt: null,
      lastVerificationError: null,
      createdAt: '2026-04-13T21:10:00Z',
      updatedAt: '2026-04-13T21:10:00Z',
    })
    listProviderScopesMock.mockResolvedValue([])
    getReviewerIdentityMock.mockResolvedValue(null)

    const wrapper = await mountTab()
    await flushPromises()

    await wrapper.get('.provider-create-toggle').trigger('click')
    await wrapper.find('input[placeholder="e.g. GitHub Enterprise"]').setValue('GitLab Self-Hosted')
    await wrapper.find('input[placeholder="https://github.com"]').setValue('https://gitlab.example.com')
    await wrapper.find('input[placeholder="Paste the provider secret"]').setValue('glpat-test')
    await wrapper.get('.provider-create-submit').trigger('click')
    await flushPromises()

    expect(createProviderConnectionMock).toHaveBeenCalledWith('client-1', expect.objectContaining({
      displayName: 'GitLab Self-Hosted',
      hostBaseUrl: 'https://gitlab.example.com',
      secret: 'glpat-test',
    }))
    expect(notifyMock).toHaveBeenCalledWith('Provider connection created.')
    expect(wrapper.text()).toContain('GitLab Self-Hosted')
  })

  it('resolves reviewer candidates and saves the selected reviewer identity', async () => {
    resolveReviewerIdentityCandidatesMock.mockResolvedValue([
      {
        clientId: 'client-1',
        connectionId: 'provider-conn-1',
        providerFamily: 'github',
        externalUserId: '99',
        login: 'meister-review-bot[bot]',
        displayName: 'Meister Review Bot',
        isBot: true,
      },
    ])
    setReviewerIdentityMock.mockResolvedValue({
      id: 'reviewer-2',
      clientId: 'client-1',
      connectionId: 'provider-conn-1',
      providerFamily: 'github',
      externalUserId: '99',
      login: 'meister-review-bot[bot]',
      displayName: 'Meister Review Bot',
      isBot: true,
      updatedAt: '2026-04-13T21:20:00Z',
    })

    const wrapper = await mountTab()
    await flushPromises()

    await wrapper.find('input[placeholder="Search login or bot account"]').setValue('meister')
    await wrapper.get('.provider-reviewer-resolve').trigger('click')
    await flushPromises()

    expect(resolveReviewerIdentityCandidatesMock).toHaveBeenCalledWith('client-1', 'provider-conn-1', 'meister')
    expect(wrapper.text()).toContain('Meister Review Bot')

    await wrapper.get('.provider-reviewer-save').trigger('click')
    await flushPromises()

    expect(setReviewerIdentityMock).toHaveBeenCalledWith('client-1', 'provider-conn-1', {
      externalUserId: '99',
      login: 'meister-review-bot[bot]',
      displayName: 'Meister Review Bot',
      isBot: true,
    })
    expect(notifyMock).toHaveBeenCalledWith('Reviewer identity saved.')
  })

  it('ignores stale scope and reviewer responses after switching the selected connection', async () => {
    const firstScopes = createDeferred<Array<Record<string, unknown>>>()
    const firstReviewerIdentity = createDeferred<Record<string, unknown> | null>()
    const secondScopes = createDeferred<Array<Record<string, unknown>>>()
    const secondReviewerIdentity = createDeferred<Record<string, unknown> | null>()

    listProviderConnectionsMock.mockResolvedValue([
      {
        id: 'provider-conn-1',
        clientId: 'client-1',
        providerFamily: 'github',
        hostBaseUrl: 'https://github.com',
        authenticationKind: 'personalAccessToken',
        displayName: 'GitHub Cloud',
        isActive: true,
        verificationStatus: 'verified',
        readinessLevel: 'workflowComplete',
        readinessReason: 'Connection meets onboarding and workflow-complete readiness criteria.',
        hostVariant: 'hosted',
        missingReadinessCriteria: [],
        lastVerifiedAt: '2026-04-13T21:00:00Z',
        lastVerificationError: null,
        createdAt: '2026-04-13T20:00:00Z',
        updatedAt: '2026-04-13T21:00:00Z',
      },
      {
        id: 'provider-conn-2',
        clientId: 'client-1',
        providerFamily: 'gitLab',
        hostBaseUrl: 'https://gitlab.example.com',
        authenticationKind: 'personalAccessToken',
        displayName: 'GitLab Self-Hosted',
        isActive: true,
        verificationStatus: 'verified',
        readinessLevel: 'onboardingReady',
        readinessReason: 'Connection is verified for onboarding, but workflow-complete readiness criteria are still missing.',
        hostVariant: 'selfHosted',
        missingReadinessCriteria: ['Configured reviewer identity is required for workflow-complete readiness.'],
        lastVerifiedAt: '2026-04-13T21:00:00Z',
        lastVerificationError: null,
        createdAt: '2026-04-13T20:00:00Z',
        updatedAt: '2026-04-13T21:00:00Z',
      },
    ])

    listProviderScopesMock.mockImplementation((_clientId: string, connectionId: string) => {
      return connectionId === 'provider-conn-1'
        ? firstScopes.promise
        : secondScopes.promise
    })
    getReviewerIdentityMock.mockImplementation((_clientId: string, connectionId: string) => {
      return connectionId === 'provider-conn-1'
        ? firstReviewerIdentity.promise
        : secondReviewerIdentity.promise
    })

    const wrapper = await mountTab()
    await flushPromises()

    const connectionItems = wrapper.findAll('.provider-connection-main')
    await connectionItems[1].trigger('click')

    secondScopes.resolve([
      {
        id: 'scope-2',
        clientId: 'client-1',
        connectionId: 'provider-conn-2',
        scopeType: 'organization',
        externalScopeId: 'fresh-org',
        scopePath: 'fresh-org',
        displayName: 'Fresh Scope',
        verificationStatus: 'verified',
        isEnabled: true,
        lastVerifiedAt: '2026-04-13T21:00:00Z',
        lastVerificationError: null,
        createdAt: '2026-04-13T20:00:00Z',
        updatedAt: '2026-04-13T21:00:00Z',
      },
    ])
    secondReviewerIdentity.resolve({
      id: 'reviewer-2',
      clientId: 'client-1',
      connectionId: 'provider-conn-2',
      providerFamily: 'gitLab',
      externalUserId: '84',
      login: 'fresh-reviewer',
      displayName: 'Fresh Reviewer',
      isBot: false,
      updatedAt: '2026-04-13T21:00:00Z',
    })
    await flushPromises()

    expect(wrapper.text()).toContain('Fresh Scope')
    expect(wrapper.text()).toContain('Fresh Reviewer')

    firstScopes.resolve([
      {
        id: 'scope-1',
        clientId: 'client-1',
        connectionId: 'provider-conn-1',
        scopeType: 'organization',
        externalScopeId: 'legacy-org',
        scopePath: 'legacy-org',
        displayName: 'Legacy Scope',
        verificationStatus: 'verified',
        isEnabled: true,
        lastVerifiedAt: '2026-04-13T21:00:00Z',
        lastVerificationError: null,
        createdAt: '2026-04-13T20:00:00Z',
        updatedAt: '2026-04-13T21:00:00Z',
      },
    ])
    firstReviewerIdentity.resolve({
      id: 'reviewer-1',
      clientId: 'client-1',
      connectionId: 'provider-conn-1',
      providerFamily: 'github',
      externalUserId: '42',
      login: 'legacy-reviewer',
      displayName: 'Legacy Reviewer',
      isBot: false,
      updatedAt: '2026-04-13T21:00:00Z',
    })
    await flushPromises()

    expect(wrapper.text()).toContain('Fresh Scope')
    expect(wrapper.text()).toContain('Fresh Reviewer')
    expect(wrapper.text()).not.toContain('Legacy Scope')
    expect(wrapper.text()).not.toContain('Legacy Reviewer')
  })
})
