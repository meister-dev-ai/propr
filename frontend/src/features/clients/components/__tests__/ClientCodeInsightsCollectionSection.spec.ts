// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { computed, ref } from 'vue'
import ClientCodeInsightsCollectionSection from '../ClientCodeInsightsCollectionSection.vue'
import { ClientDetailVmKey } from '@/features/clients/view-models/useClientDetailViewModel'

function mountSection(
  options: { licensed?: boolean; collecting?: boolean; upgradeMessage?: string } = {},
) {
  const licensed = options.licensed ?? true
  const collecting = options.collecting ?? false
  const saveCodeInsightsCollection = vi.fn()
  const editedCodeInsightsCollectionEnabled = ref(collecting)
  const vm = {
    client: ref({ id: 'c1', codeInsightsCollectionEnabled: collecting }),
    saving: ref(false),
    saveError: ref(''),
    editedCodeInsightsCollectionEnabled,
    isCodeInsightsAvailable: computed(() => licensed),
    codeInsightsUpgradeMessage: computed(() => options.upgradeMessage ?? ''),
    saveCodeInsightsCollection,
  }
  const wrapper = mount(ClientCodeInsightsCollectionSection, {
    global: { provide: { [ClientDetailVmKey as symbol]: vm } },
  })
  return { wrapper, vm, saveCodeInsightsCollection }
}

function checkbox(wrapper: ReturnType<typeof mountSection>['wrapper']) {
  return wrapper.get('#codeInsightsCollectionEnabled').element as HTMLInputElement
}

function saveButton(wrapper: ReturnType<typeof mountSection>['wrapper']) {
  return wrapper.get('.btn-primary').element as HTMLButtonElement
}

describe('ClientCodeInsightsCollectionSection', () => {
  it('defaults to not collecting and says why it is off', () => {
    const { wrapper } = mountSection()

    expect(checkbox(wrapper).checked).toBe(false)
    expect(wrapper.text()).toContain('spends model tokens')
    expect(wrapper.text()).toContain('off by default')
  })

  it('explains that collection is forward-only in both directions', () => {
    const { wrapper } = mountSection()

    expect(wrapper.text()).toContain('collects from now on')
    expect(wrapper.text()).toContain('without deleting what was already collected')
  })

  it('saves an opt-in through the view model', async () => {
    const { wrapper, vm, saveCodeInsightsCollection } = mountSection()

    await wrapper.get('#codeInsightsCollectionEnabled').setValue(true)
    expect(vm.editedCodeInsightsCollectionEnabled.value).toBe(true)

    await wrapper.get('.btn-primary').trigger('click')
    expect(saveCodeInsightsCollection).toHaveBeenCalledOnce()
  })

  it('keeps Save inert until the toggle actually differs from what is stored', async () => {
    const { wrapper } = mountSection({ collecting: false })

    expect(saveButton(wrapper).disabled).toBe(true)

    await wrapper.get('#codeInsightsCollectionEnabled').setValue(true)
    expect(saveButton(wrapper).disabled).toBe(false)

    await wrapper.get('#codeInsightsCollectionEnabled').setValue(false)
    expect(saveButton(wrapper).disabled).toBe(true)
  })

  it('on an unlicensed installation the control is inert and the reason is stated', async () => {
    const { wrapper, saveCodeInsightsCollection } = mountSection({
      licensed: false,
      upgradeMessage: 'Code Insights is currently disabled for this installation.',
    })

    // A disabled control with an explanation, not a dead one: the server enforces the same gate, so even a
    // forced flag could not start collection.
    expect(checkbox(wrapper).disabled).toBe(true)
    expect(saveButton(wrapper).disabled).toBe(true)
    expect(wrapper.get('[data-testid="code-insights-upgrade-note"]').text()).toContain(
      'currently disabled for this installation',
    )

    await wrapper.get('.btn-primary').trigger('click')
    expect(saveCodeInsightsCollection).not.toHaveBeenCalled()
  })

  it('falls back to its own explanation when the licence carries no message', () => {
    const { wrapper } = mountSection({ licensed: false })

    expect(wrapper.get('[data-testid="code-insights-upgrade-note"]').text()).toContain(
      'requires a commercial license',
    )
  })

  it('shows no upgrade note when the capability is available', () => {
    const { wrapper } = mountSection({ licensed: true })

    expect(wrapper.find('[data-testid="code-insights-upgrade-note"]').exists()).toBe(false)
  })

  it('surfaces a save failure', () => {
    const { wrapper, vm } = mountSection()
    vm.saveError.value = 'Failed to save the Code Insights setting.'

    return wrapper.vm.$nextTick().then(() => {
      expect(wrapper.get('.error').text()).toContain('Failed to save')
    })
  })
})
