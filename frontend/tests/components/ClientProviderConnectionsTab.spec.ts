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

vi.mock('@/services/providerActivationService', async () => {
  const actual = await vi.importActual<typeof import('@/services/providerActivationService')>('@/services/providerActivationService')

  return {
    ...actual,
    listProviderActivationStatuses: listProviderActivationStatusesMock,
  }
})

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
  const { default: ClientProviderConnectionsTab } = await import('@/features/clients/components/ClientProviderConnectionsTab.vue')
  if (!document.getElementById('provider-sidebar-target')) {
    const el = document.createElement('div')
    el.id = 'provider-sidebar-target'
    document.body.appendChild(el)
  }

  return mount(ClientProviderConnectionsTab, {
    props: {
      clientId: 'client-1',
    },
    attachTo: document.body
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
        gitHubAppId: null,
        gitHubAppInstallationId: null,
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

    await wrapper.findAll('.provider-connection-item')[0].trigger('click')
    await flushPromises()

    expect(listProviderScopesMock).toHaveBeenCalledWith('client-1', 'provider-conn-1')
    expect(getReviewerIdentityMock).toHaveBeenCalledWith('client-1', 'provider-conn-1')
    expect(document.getElementById('provider-sidebar-target')?.textContent).toContain('GitHub Cloud')

    // The scopes and reviewers should be in the DOM because of v-show
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
    expect(document.getElementById('provider-sidebar-target')?.textContent).toContain('GitLab Self-Hosted')
  })

  it('serializes GitHub App IDs as numbers when creating a GitHub App connection', async () => {
    createProviderConnectionMock.mockResolvedValue({
      id: 'provider-conn-2',
      clientId: 'client-1',
      providerFamily: 'github',
      hostBaseUrl: 'https://github.example.com',
      authenticationKind: 'appInstallation',
      gitHubAppId: 123456,
      gitHubAppInstallationId: 789012,
      displayName: 'GitHub App Connection',
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

    const authenticationSelect = wrapper.findAll('select')[1]
    await authenticationSelect.setValue('appInstallation')

    await wrapper.find('input[placeholder="e.g. GitHub Enterprise"]').setValue('GitHub App Connection')
    await wrapper.find('input[placeholder="https://github.com"]').setValue('https://github.example.com')
    await wrapper.find('input[placeholder="123456"]').setValue('123456')
    await wrapper.find('input[placeholder="987654321"]').setValue('789012')
    await wrapper.find('input[placeholder="Paste the GitHub App private key (PEM)"]').setValue('-----BEGIN PRIVATE KEY-----')
    await wrapper.get('.provider-create-submit').trigger('click')
    await flushPromises()

    expect(createProviderConnectionMock).toHaveBeenCalledWith('client-1', expect.objectContaining({
      providerFamily: 'github',
      authenticationKind: 'appInstallation',
      gitHubAppId: 123456,
      gitHubAppInstallationId: 789012,
      secret: '-----BEGIN PRIVATE KEY-----',
    }))
  })

  it('clears GitHub App IDs when switching an existing connection back to PAT', async () => {
    listProviderConnectionsMock.mockResolvedValue([
      {
        id: 'provider-conn-1',
        clientId: 'client-1',
        providerFamily: 'github',
        hostBaseUrl: 'https://github.example.com',
        authenticationKind: 'appInstallation',
        gitHubAppId: 123456,
        gitHubAppInstallationId: 789012,
        displayName: 'GitHub App Connection',
        isActive: true,
        verificationStatus: 'verified',
        readinessLevel: 'workflowComplete',
        readinessReason: 'Connection meets onboarding and workflow-complete readiness criteria.',
        hostVariant: 'selfHosted',
        missingReadinessCriteria: [],
        lastVerifiedAt: '2026-04-13T21:00:00Z',
        lastVerificationError: null,
        createdAt: '2026-04-13T20:00:00Z',
        updatedAt: '2026-04-13T21:00:00Z',
      },
    ])
    updateProviderConnectionMock.mockResolvedValue({
      id: 'provider-conn-1',
      clientId: 'client-1',
      providerFamily: 'github',
      hostBaseUrl: 'https://github.example.com',
      authenticationKind: 'personalAccessToken',
      gitHubAppId: null,
      gitHubAppInstallationId: null,
      displayName: 'GitHub App Connection',
      isActive: true,
      verificationStatus: 'verified',
      readinessLevel: 'workflowComplete',
      readinessReason: 'Connection meets onboarding and workflow-complete readiness criteria.',
      hostVariant: 'selfHosted',
      missingReadinessCriteria: [],
      lastVerifiedAt: '2026-04-13T21:10:00Z',
      lastVerificationError: null,
      createdAt: '2026-04-13T20:00:00Z',
      updatedAt: '2026-04-13T21:10:00Z',
    })

    const wrapper = await mountTab()
    await flushPromises()

    await wrapper.findAll('.provider-connection-item')[0].trigger('click')
    await flushPromises()

    const authenticationSelect = wrapper.findAll('select').find((select) =>
      select.findAll('option').some((option) => option.element.getAttribute('value') === 'appInstallation'),
    )

    expect(authenticationSelect).toBeTruthy()
    await authenticationSelect!.setValue('personalAccessToken')
    await wrapper.find('input[placeholder="Paste the provider secret"]').setValue('ghp_new_token')
    await wrapper.get('.btn-primary.btn-sm').trigger('click')
    await flushPromises()

    expect(updateProviderConnectionMock).toHaveBeenCalledWith('client-1', 'provider-conn-1', expect.objectContaining({
      authenticationKind: 'personalAccessToken',
      gitHubAppId: null,
      gitHubAppInstallationId: null,
      secret: 'ghp_new_token',
    }))
  })

  it('serializes GitHub App IDs as numbers when switching an existing PAT connection to GitHub App auth', async () => {
    updateProviderConnectionMock.mockResolvedValue({
      id: 'provider-conn-1',
      clientId: 'client-1',
      providerFamily: 'github',
      hostBaseUrl: 'https://github.com',
      authenticationKind: 'appInstallation',
      gitHubAppId: 123456,
      gitHubAppInstallationId: 789012,
      displayName: 'GitHub Cloud',
      isActive: true,
      verificationStatus: 'unknown',
      readinessLevel: 'configured',
      readinessReason: 'Connection has not completed onboarding verification yet.',
      hostVariant: 'hosted',
      missingReadinessCriteria: ['Connection has not been verified yet.'],
      lastVerifiedAt: null,
      lastVerificationError: null,
      createdAt: '2026-04-13T20:00:00Z',
      updatedAt: '2026-04-13T21:10:00Z',
    })

    const wrapper = await mountTab()
    await flushPromises()

    await wrapper.findAll('.provider-connection-item')[0].trigger('click')
    await flushPromises()

    const authenticationSelect = wrapper.findAll('select').find((select) =>
      select.findAll('option').some((option) => option.element.getAttribute('value') === 'appInstallation'),
    )

    expect(authenticationSelect).toBeTruthy()
    await authenticationSelect!.setValue('appInstallation')
    await wrapper.find('input[placeholder="123456"]').setValue('123456')
    await wrapper.find('input[placeholder="987654321"]').setValue('789012')
    await wrapper.find('input[placeholder="Paste the GitHub App private key (PEM)"]').setValue('-----BEGIN PRIVATE KEY-----')
    await wrapper.get('.btn-primary.btn-sm').trigger('click')
    await flushPromises()

    expect(updateProviderConnectionMock).toHaveBeenCalledWith('client-1', 'provider-conn-1', expect.objectContaining({
      authenticationKind: 'appInstallation',
      gitHubAppId: 123456,
      gitHubAppInstallationId: 789012,
      secret: '-----BEGIN PRIVATE KEY-----',
    }))
  })

  it('submits userName for Azure DevOps Server windows auth create flow', async () => {
    listProviderActivationStatusesMock.mockResolvedValue([
      { providerFamily: 'azureDevOps', isEnabled: true },
    ])
    createProviderConnectionMock.mockResolvedValue({
      id: 'provider-conn-2',
      clientId: 'client-1',
      providerFamily: 'azureDevOps',
      hostBaseUrl: 'https://ado-server.example.com/tfs',
      authenticationKind: 'windowsUserAccount',
      userName: 'CONTOSO\\ado-user',
      displayName: 'Azure DevOps Server',
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
    await wrapper.find('input[placeholder="e.g. GitHub Enterprise"]').setValue('Azure DevOps Server')
    await wrapper.find('input[placeholder="https://dev.azure.com or https://ado-server.example.com/tfs"]').setValue('https://ado-server.example.com/tfs')
    await flushPromises()

    const authenticationSelect = wrapper.findAll('select').find((select) =>
      select.findAll('option').some((option) => option.element.getAttribute('value') === 'windowsUserAccount'),
    )
    expect(authenticationSelect).toBeTruthy()
    await authenticationSelect!.setValue('windowsUserAccount')
    const userNameInput = wrapper.findAll('input').find((input) => input.attributes('placeholder')?.includes('ado-user'))
    expect(userNameInput).toBeTruthy()
    await userNameInput!.setValue('CONTOSO\\ado-user')
    await wrapper.find('input[placeholder="Paste the provider secret"]').setValue('password')
    await wrapper.get('.provider-create-submit').trigger('click')
    await flushPromises()

    expect(createProviderConnectionMock).toHaveBeenCalledWith('client-1', expect.objectContaining({
      providerFamily: 'azureDevOps',
      authenticationKind: 'windowsUserAccount',
      userName: 'CONTOSO\\ado-user',
      hostBaseUrl: 'https://ado-server.example.com/tfs',
      secret: 'password',
    }))
  })

  it('shows a private-key-specific error when switching an existing PAT connection to GitHub App without a secret', async () => {
    const wrapper = await mountTab()
    await flushPromises()

    await wrapper.findAll('.provider-connection-item')[0].trigger('click')
    await flushPromises()

    const authenticationSelect = wrapper.findAll('select').find((select) =>
      select.findAll('option').some((option) => option.element.getAttribute('value') === 'appInstallation'),
    )

    expect(authenticationSelect).toBeTruthy()
    await authenticationSelect!.setValue('appInstallation')
    await wrapper.find('input[placeholder="123456"]').setValue('123456')
    await wrapper.find('input[placeholder="987654321"]').setValue('789012')
    await wrapper.get('.btn-primary.btn-sm').trigger('click')
    await flushPromises()

    expect(updateProviderConnectionMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('A GitHub App private key is required when switching to GitHub App authentication.')
  })

  it('shows an Azure DevOps-specific secret error when switching auth modes without a replacement secret', async () => {
    listProviderConnectionsMock.mockResolvedValue([
      {
        id: 'provider-conn-1',
        clientId: 'client-1',
        providerFamily: 'azureDevOps',
        hostBaseUrl: 'https://ado-server.example.com/tfs',
        authenticationKind: 'personalAccessToken',
        userName: null,
        displayName: 'Azure DevOps Server',
        isActive: true,
        verificationStatus: 'verified',
        readinessLevel: 'configured',
        readinessReason: 'Configured.',
        hostVariant: 'selfHosted',
        missingReadinessCriteria: [],
        lastVerifiedAt: '2026-04-13T21:00:00Z',
        lastVerificationError: null,
        createdAt: '2026-04-13T20:00:00Z',
        updatedAt: '2026-04-13T21:00:00Z',
      },
    ])
    listProviderScopesMock.mockResolvedValue([])
    getReviewerIdentityMock.mockResolvedValue(null)

    const wrapper = await mountTab()
    await flushPromises()

    await wrapper.findAll('.provider-connection-item')[0].trigger('click')
    await flushPromises()

    const authenticationSelect = wrapper.findAll('select').find((select) =>
      select.findAll('option').some((option) => option.element.getAttribute('value') === 'windowsUserAccount'),
    )
    expect(authenticationSelect).toBeTruthy()
    await authenticationSelect!.setValue('windowsUserAccount')
    const userNameInput = wrapper.findAll('input').find((input) => input.attributes('placeholder')?.includes('ado-user'))
    expect(userNameInput).toBeTruthy()
    await userNameInput!.setValue('CONTOSO\\ado-user')
    await wrapper.get('.btn-primary.btn-sm').trigger('click')
    await flushPromises()

    expect(updateProviderConnectionMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('A replacement secret is required when switching Azure DevOps authentication modes.')
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

    await wrapper.findAll('.provider-connection-item')[0].trigger('click')
    await flushPromises()

    const identityTab = wrapper.findAll('li').find(el => el.text() === 'Reviewer Identity')
    if (identityTab) await identityTab.trigger('click')

    expect(wrapper.text()).toContain('optional reviewer trigger')
    expect(wrapper.text()).toContain('does not change the connection identity used for posting')

    await wrapper.find('input[placeholder="Search reviewer login or bot account"]').setValue('meister')
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
    expect(notifyMock).toHaveBeenCalledWith('Reviewer trigger saved.')
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
        missingReadinessCriteria: ['Automatic workflow proof is still missing for this provider variant.'],
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

    const connectionItems = wrapper.findAll('.provider-connection-item')
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
