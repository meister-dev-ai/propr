// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const listProviderOperationalStatusMock = vi.fn()

vi.mock('@/services/providerOperationsService', () => ({
  listProviderOperationalStatus: listProviderOperationalStatusMock,
}))

async function mountList() {
  const { default: ProviderConnectionStatusList } = await import('@/components/ProviderConnectionStatusList.vue')
  return mount(ProviderConnectionStatusList, {
    props: {
      clientId: 'client-1',
    },
  })
}

describe('ProviderConnectionStatusList', () => {
  beforeEach(() => {
    vi.clearAllMocks()

    listProviderOperationalStatusMock.mockResolvedValue([
      {
        connectionId: 'provider-conn-github-1',
        providerFamily: 'github',
        displayName: 'Acme GitHub',
        hostBaseUrl: 'https://github.com',
        hostVariant: 'hosted',
        isActive: true,
        verificationStatus: 'verified',
        readinessLevel: 'workflowComplete',
        readinessReason: 'Connection meets onboarding and workflow-complete readiness criteria.',
        missingReadinessCriteria: [],
        health: 'healthy',
        lastCheckedAt: '2026-04-08T10:00:00Z',
        failureCategory: null,
        statusReason: 'Connection meets onboarding and workflow-complete readiness criteria.',
      },
      {
        connectionId: 'provider-conn-gitlab-1',
        providerFamily: 'gitLab',
        displayName: 'Platform GitLab',
        hostBaseUrl: 'https://gitlab.example.com',
        hostVariant: 'selfHosted',
        isActive: true,
        verificationStatus: 'failed',
        readinessLevel: 'degraded',
        readinessReason: 'Token missing read_api scope.',
        missingReadinessCriteria: ['Token missing read_api scope.'],
        health: 'failing',
        lastCheckedAt: '2026-04-08T11:00:00Z',
        failureCategory: 'authentication',
        statusReason: 'Token missing read_api scope.',
      },
      {
        connectionId: 'provider-conn-forgejo-1',
        providerFamily: 'forgejo',
        displayName: 'Codeberg Mirror',
        hostBaseUrl: 'https://codeberg.org',
        hostVariant: 'hosted',
        isActive: false,
        verificationStatus: 'stale',
        readinessLevel: 'degraded',
        readinessReason: 'Connection is disabled.',
        missingReadinessCriteria: ['Connection must be active.'],
        health: 'inactive',
        lastCheckedAt: '2026-04-07T15:00:00Z',
        failureCategory: null,
        statusReason: 'Connection is disabled.',
      },
    ])
  })

  it('loads and renders mixed-provider connection health', async () => {
    const wrapper = await mountList()
    await flushPromises()

    expect(listProviderOperationalStatusMock).toHaveBeenCalledWith('client-1')
    expect(wrapper.text()).toContain('Connection Status')
    expect(wrapper.text()).toContain('Acme GitHub')
    expect(wrapper.text()).toContain('GitHub')
    expect(wrapper.text()).toContain('Healthy')
    expect(wrapper.text()).toContain('Workflow Complete')
    expect(wrapper.text()).toContain('Platform GitLab')
    expect(wrapper.text()).toContain('Failing')
    expect(wrapper.text()).toContain('authentication')
    expect(wrapper.text()).toContain('Codeberg Mirror')
    expect(wrapper.text()).toContain('Inactive')
  })

  it('shows an empty state when the client has no provider operations to monitor', async () => {
    listProviderOperationalStatusMock.mockResolvedValue([])

    const wrapper = await mountList()
    await flushPromises()

    expect(wrapper.text()).toContain('No provider connections to monitor yet')
  })
})