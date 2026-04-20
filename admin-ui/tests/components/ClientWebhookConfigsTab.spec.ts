// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const listWebhookConfigurationsMock = vi.fn()
const listWebhookDeliveriesMock = vi.fn()
const deleteWebhookConfigurationMock = vi.fn()
const notifyMock = vi.fn()

vi.mock('@/services/webhookConfigurationService', () => ({
  listWebhookConfigurations: listWebhookConfigurationsMock,
  listWebhookDeliveries: listWebhookDeliveriesMock,
  deleteWebhookConfiguration: deleteWebhookConfigurationMock,
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({
    notify: notifyMock,
  }),
}))

async function mountTab() {
  const { default: ClientWebhookConfigsTab } = await import('@/components/ClientWebhookConfigsTab.vue')
  return mount(ClientWebhookConfigsTab, {
    props: {
      clientId: 'client-1',
    },
    global: {
      stubs: {
        ProgressOrb: { template: '<div class="progress-orb-stub" />' },
        ModalDialog: {
          props: ['isOpen', 'title'],
          template: '<div v-if="isOpen"><slot /></div>',
        },
        ConfirmDialog: {
          props: ['open'],
          emits: ['confirm', 'cancel'],
          template: '<div v-if="open"><button class="confirm-delete" @click="$emit(\'confirm\')">confirm</button></div>',
        },
        WebhookConfigForm: {
          props: ['clientId', 'config'],
          emits: ['config-saved', 'cancel'],
          template: `
            <div class="webhook-form-stub">
              <button class="emit-created" @click="$emit('config-saved', {
                id: 'webhook-config-2',
                clientId,
                provider: 'azureDevOps',
                organizationScopeId: 'scope-1',
                providerScopePath: 'https://dev.azure.com/example',
                providerProjectKey: 'Project Two',
                isActive: true,
                enabledEvents: ['pullRequestUpdated'],
                repoFilters: [],
                listenerUrl: 'https://propr.example.com/webhooks/v1/providers/ado/mock-path-key-2',
                generatedSecret: 'generated-secret',
                createdAt: '2026-04-07T09:00:00Z'
              })">emit created</button>
            </div>
          `,
        },
      },
    },
  })
}

describe('ClientWebhookConfigsTab', () => {
  beforeEach(() => {
    vi.clearAllMocks()

    listWebhookConfigurationsMock.mockResolvedValue([
      {
        id: 'webhook-config-1',
        clientId: 'client-1',
        provider: 'azureDevOps',
        organizationScopeId: 'scope-1',
        providerScopePath: 'https://dev.azure.com/example',
        providerProjectKey: 'Project One',
        isActive: true,
        enabledEvents: ['pullRequestCreated', 'pullRequestUpdated'],
        repoFilters: [
          {
            id: 'filter-1',
            repositoryName: 'Repository One',
            displayName: 'Repository One',
            canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' },
            targetBranchPatterns: ['main'],
          },
        ],
        listenerUrl: 'https://propr.example.com/webhooks/v1/providers/ado/mock-path-key-1',
        createdAt: '2026-04-07T09:00:00Z',
      },
    ])

    listWebhookDeliveriesMock.mockResolvedValue({
      items: [
        {
          id: 'delivery-1',
          webhookConfigurationId: 'webhook-config-1',
          receivedAt: '2026-04-07T09:05:00Z',
          eventType: 'git.pullrequest.updated',
          deliveryOutcome: 'accepted',
          httpStatusCode: 200,
          repositoryId: 'repo-1',
          pullRequestId: 42,
          sourceBranch: 'refs/heads/feature/test',
          targetBranch: 'refs/heads/main',
          actionSummaries: ['Submitted review intake job for PR #42 at iteration 7 via pull request updated.'],
          failureReason: null,
        },
      ],
    })

    deleteWebhookConfigurationMock.mockResolvedValue(undefined)
  })

  it('loads webhook configurations for the active client and shows the count', async () => {
    const wrapper = await mountTab()
    await flushPromises()

    expect(listWebhookConfigurationsMock).toHaveBeenCalled()
    expect(wrapper.text()).toContain('Webhook Configurations')
    expect(wrapper.text()).toContain('1 config')
    expect(wrapper.text()).toContain('Azure DevOps')
    expect(wrapper.text()).toContain('Project One')
  })

  it('loads and renders delivery history for the selected configuration', async () => {
    const wrapper = await mountTab()
    await flushPromises()

    await wrapper.get('[data-testid="webhook-config-row-webhook-config-1"]').trigger('click')
    await flushPromises()

    expect(listWebhookDeliveriesMock).toHaveBeenCalledWith('webhook-config-1')
    expect(wrapper.text()).toContain('git.pullrequest.updated')
    expect(wrapper.text()).toContain('Submitted review intake job for PR #42 at iteration 7 via pull request updated.')
  })

  it('shows the one-time secret receipt after a new configuration is created', async () => {
    const wrapper = await mountTab()
    await flushPromises()

    await wrapper.get('.create-webhook-config').trigger('click')
    await flushPromises()
    await wrapper.get('.emit-created').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('generated-secret')
    expect(wrapper.text()).toContain('https://propr.example.com/webhooks/v1/providers/ado/mock-path-key-2')
    expect(wrapper.text()).toContain('Project Two')
  })
})
