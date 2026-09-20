// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const getProviderAddInInventoryMock = vi.fn()
const listProviderAddInActivationsMock = vi.fn()
const activateProviderAddInMock = vi.fn()
const revokeProviderAddInMock = vi.fn()

vi.mock('@/services/providerAddInsService', () => ({
  getProviderAddInInventory: getProviderAddInInventoryMock,
  listProviderAddInActivations: listProviderAddInActivationsMock,
  activateProviderAddIn: activateProviderAddInMock,
  revokeProviderAddIn: revokeProviderAddInMock,
}))

async function mountView() {
  const { default: ProviderAddInsView } = await import(
    '@/features/provider-add-ins/views/ProviderAddInsView.vue'
  )

  return mount(ProviderAddInsView)
}

function loaded(overrides: Record<string, unknown> = {}) {
  return {
    key: 'meisterdev/example',
    label: 'Example',
    version: '2.1',
    contractVersion: '1.0',
    reachedHostPatterns: ['api.example.com'],
    requiredCapabilityKey: 'example-connections',
    filePath: '/app/provider-add-ins/example/example.dll',
    contentHash: 'abc123',
    origin: 'built-in',
    ...overrides,
  }
}

function awaiting(overrides: Record<string, unknown> = {}) {
  return {
    filePath: '/plugins/acme/acme.dll',
    contentHash: '9f3a',
    key: 'acme/gateway',
    label: 'Acme Gateway',
    version: '1.2.0',
    contractVersion: '1.0',
    reachedHosts: ['.acme.ai'],
    requiredCapability: null,
    assemblyName: 'Acme.Provider',
    assemblyVersion: '1.2.0.0',
    refusal: null,
    canBeActivated: true,
    ...overrides,
  }
}

describe('ProviderAddInsView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    getProviderAddInInventoryMock.mockResolvedValue({ loaded: [], rejected: [], awaiting: [] })
    listProviderAddInActivationsMock.mockResolvedValue([])
  })

  it('shows every value the loader recorded for a loaded family', async () => {
    getProviderAddInInventoryMock.mockResolvedValue({ loaded: [loaded()], rejected: [], awaiting: [] })

    const wrapper = await mountView()
    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain('meisterdev/example')
    expect(text).toContain('Example')
    expect(text).toContain('2.1')
    expect(text).toContain('api.example.com')
    expect(text).toContain('example-connections')
    expect(text).toContain('/app/provider-add-ins/example/example.dll')
    expect(text).toContain('abc123')
    expect(text).toContain('built-in')
  })

  it('shows a skipped assembly with its category, its reason and its file', async () => {
    getProviderAddInInventoryMock.mockResolvedValue({
      loaded: [],
      rejected: [
        {
          category: 'mis-packaged',
          reason: 'It ships its own copy of the contract.',
          filePath: '/plugins/broken/broken.dll',
          contentHash: 'def456',
          key: null,
          origin: 'external',
        },
      ],
      awaiting: [],
    })

    const wrapper = await mountView()
    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain('mis-packaged')
    expect(text).toContain('It ships its own copy of the contract.')
    expect(text).toContain('/plugins/broken/broken.dll')
  })

  // An operator opens this page because a family is missing, so the skipped list is what they meet first.
  it('puts the skipped list above the loaded one', async () => {
    getProviderAddInInventoryMock.mockResolvedValue({
      loaded: [loaded()],
      rejected: [
        {
          category: 'duplicate',
          reason: 'Already served by the built-in copy.',
          filePath: '/plugins/copy/copy.dll',
          contentHash: null,
          key: 'meisterdev/example',
          origin: 'external',
        },
      ],
      awaiting: [],
    })

    const wrapper = await mountView()
    await flushPromises()

    const text = wrapper.text()
    expect(text.indexOf('Skipped')).toBeLessThan(text.indexOf('Loaded'))
  })

  // Two add-ins may declare one name, so the key is what tells them apart and both are listed.
  it('lists two families sharing a name under their own keys', async () => {
    getProviderAddInInventoryMock.mockResolvedValue({
      loaded: [
        loaded({ key: 'meisterdev/first', label: 'Shared name' }),
        loaded({ key: 'meisterdev/second', label: 'Shared name' }),
      ],
      rejected: [],
      awaiting: [],
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('meisterdev/first')
    expect(wrapper.text()).toContain('meisterdev/second')
  })

  // The list reflects load time, not the contents of the directories, and the page has to say so or it reads
  // as a directory listing.
  it('states that the list reflects load time rather than the directories', async () => {
    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('read once, while this host starts')
  })

  // The decision is about a binary, so what an administrator is shown for one has to be enough to make it:
  // who it says it is, what it says it will contact, and the bytes the decision binds to.
  it('shows what an add-in states about itself before any of it has run', async () => {
    getProviderAddInInventoryMock.mockResolvedValue({ loaded: [], rejected: [], awaiting: [awaiting()] })

    const wrapper = await mountView()
    await flushPromises()

    const section = wrapper.get('[data-testid="add-ins-awaiting"]').text()
    expect(section).toContain('acme/gateway')
    expect(section).toContain('Acme Gateway')
    expect(section).toContain('1.2.0')
    expect(section).toContain('.acme.ai')
    expect(section).toContain('9f3a')
  })

  // Activating loads the add-in, so the page is read again: what the host is running has changed.
  it('activates an add-in and reads the page again', async () => {
    getProviderAddInInventoryMock.mockResolvedValue({ loaded: [], rejected: [], awaiting: [awaiting()] })
    activateProviderAddInMock.mockResolvedValue(loaded())

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="add-in-activate-9f3a"]').trigger('click')
    await flushPromises()

    expect(activateProviderAddInMock).toHaveBeenCalledWith('9f3a')
    expect(getProviderAddInInventoryMock).toHaveBeenCalledTimes(2)
  })

  // An add-in that cannot be activated says why instead of offering a button that would be refused.
  it('offers no activation for an add-in the host has already refused', async () => {
    getProviderAddInInventoryMock.mockResolvedValue({
      loaded: [],
      rejected: [],
      awaiting: [
        awaiting({
          canBeActivated: false,
          refusal: "The add-in states contract version '0.9' and this host carries '1.0'.",
        }),
      ],
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="add-in-activate-9f3a"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="add-ins-awaiting"]').text()).toContain("contract version '0.9'")
  })

  // The host words the refusal, so the page shows what it said rather than a restatement of it.
  it('shows the reason an activation was refused', async () => {
    getProviderAddInInventoryMock.mockResolvedValue({ loaded: [], rejected: [], awaiting: [awaiting()] })
    activateProviderAddInMock.mockRejectedValue(new Error('The file has changed since the host read it.'))

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="add-in-activate-9f3a"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="add-ins-error"]').text()).toContain('The file has changed')
  })

  // Withdrawing stops the next start taking that binary and does not stop the family serving now.
  it('lists an activation and says whether the host is running it', async () => {
    listProviderAddInActivationsMock.mockResolvedValue([
      {
        contentHash: '9f3a',
        key: 'acme/gateway',
        label: 'Acme Gateway',
        version: '1.2.0',
        filePath: '/plugins/acme/acme.dll',
        activatedByDisplayName: 'Ada',
        activatedAt: '2026-09-19T08:00:00Z',
        isServing: false,
      },
    ])

    const wrapper = await mountView()
    await flushPromises()

    const section = wrapper.get('[data-testid="add-ins-activations"]').text()
    expect(section).toContain('Ada')
    expect(section).toContain('acme/gateway')
    expect(wrapper.find('[data-testid="add-in-revoke-9f3a"]').exists()).toBe(true)
  })

  it('reports a failure to read the inventory', async () => {
    getProviderAddInInventoryMock.mockRejectedValue(new Error('Access denied.'))

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Access denied.')
  })
})
