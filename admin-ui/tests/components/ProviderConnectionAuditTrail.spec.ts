// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const listProviderAuditTrailMock = vi.fn()

vi.mock('@/services/providerOperationsService', () => ({
  listProviderAuditTrail: listProviderAuditTrailMock,
}))

async function mountTrail() {
  const { default: ProviderConnectionAuditTrail } = await import('@/components/ProviderConnectionAuditTrail.vue')
  return mount(ProviderConnectionAuditTrail, {
    props: {
      clientId: 'client-1',
    },
  })
}

describe('ProviderConnectionAuditTrail', () => {
  beforeEach(() => {
    vi.clearAllMocks()

    listProviderAuditTrailMock.mockResolvedValue([
      {
        id: 'audit-1',
        connectionId: 'provider-conn-gitlab-1',
        providerFamily: 'gitLab',
        displayName: 'Platform GitLab',
        hostBaseUrl: 'https://gitlab.example.com',
        eventType: 'connectionVerificationFailed',
        summary: 'Verification failed for Platform GitLab.',
        occurredAt: '2026-04-08T11:00:00Z',
        status: 'error',
        failureCategory: 'authentication',
        detail: 'Token missing read_api scope.',
      },
      {
        id: 'audit-2',
        connectionId: 'provider-conn-github-1',
        providerFamily: 'github',
        displayName: 'Acme GitHub',
        hostBaseUrl: 'https://github.com',
        eventType: 'connectionVerified',
        summary: 'Connection verified for Acme GitHub.',
        occurredAt: '2026-04-08T10:00:00Z',
        status: 'success',
        failureCategory: null,
        detail: null,
      },
    ])
  })

  it('loads and renders provider lifecycle and verification events', async () => {
    const wrapper = await mountTrail()
    await flushPromises()

    expect(listProviderAuditTrailMock).toHaveBeenCalledWith('client-1', 20)
    expect(wrapper.text()).toContain('Operational Audit Trail')
    expect(wrapper.text()).toContain('Platform GitLab')
    expect(wrapper.text()).toContain('Verification failed for Platform GitLab.')
    expect(wrapper.text()).toContain('authentication')
    expect(wrapper.text()).toContain('Acme GitHub')
    expect(wrapper.text()).toContain('Connection verified for Acme GitHub.')

    const entries = wrapper.findAll('[data-testid^="provider-audit-entry-"]')
    expect(entries).toHaveLength(2)
    expect(entries[0].text()).toContain('Platform GitLab')
    expect(entries[1].text()).toContain('Acme GitHub')
  })

  it('shows an empty state when no operational events are available', async () => {
    listProviderAuditTrailMock.mockResolvedValue([])

    const wrapper = await mountTrail()
    await flushPromises()

    expect(wrapper.text()).toContain('No provider audit events yet')
  })
})