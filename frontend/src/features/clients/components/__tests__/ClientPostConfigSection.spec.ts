// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import ClientPostConfigSection from '../ClientPostConfigSection.vue'
import { ClientDetailVmKey, type CommentSeverity } from '@/features/clients/view-models/useClientDetailViewModel'

function mountSection(options: { enabled?: boolean } = {}) {
  const savePostConfiguration = vi.fn()
  const editedMinimumSeverityToPost = ref<CommentSeverity>('info')
  const editedAutoResolveSeverities = ref<CommentSeverity[]>([])
  const editedWithholdOutOfScopeFindings = ref(false)
  const vm = {
    client: ref({ id: 'c1' }),
    saveError: ref(''),
    editedMinimumSeverityToPost,
    editedAutoResolveSeverities,
    editedWithholdOutOfScopeFindings,
    savePostConfiguration,
    isPostConfigButtonEnabled: () => options.enabled ?? true,
  }
  const wrapper = mount(ClientPostConfigSection, {
    global: { provide: { [ClientDetailVmKey as symbol]: vm } },
  })
  return { wrapper, vm, savePostConfiguration }
}

describe('ClientPostConfigSection', () => {
  it('renders the minimum-severity select with all four severities and the auto-resolve checkboxes', () => {
    const { wrapper } = mountSection()
    expect(wrapper.findAll('#minimumSeverityToPost option')).toHaveLength(4)
    expect(wrapper.findAll('input[name="autoResolveSeverities"]')).toHaveLength(4)
  })

  it('binds the minimum-severity select to the view-model', async () => {
    const { wrapper, vm } = mountSection()
    await wrapper.find('#minimumSeverityToPost').setValue('warning')
    expect(vm.editedMinimumSeverityToPost.value).toBe('warning')
  })

  it('adds a severity to the auto-resolve set when its checkbox is checked', async () => {
    const { wrapper, vm } = mountSection()
    await wrapper.find('#autoResolve-warning').setValue(true)
    expect(vm.editedAutoResolveSeverities.value).toContain('warning')
  })

  it('binds the out-of-scope checkbox to the view-model', async () => {
    const { wrapper, vm } = mountSection()
    await wrapper.find('#withholdOutOfScopeFindings').setValue(true)
    expect(vm.editedWithholdOutOfScopeFindings.value).toBe(true)
  })

  it('leaves out-of-scope findings publishable by default', () => {
    const { wrapper } = mountSection()
    const checkbox = wrapper.find('#withholdOutOfScopeFindings').element as HTMLInputElement
    expect(checkbox.checked).toBe(false)
  })

  it('saves through the view-model when Save is clicked', async () => {
    const { wrapper, savePostConfiguration } = mountSection()
    await wrapper.find('.post-config-save-btn').trigger('click')
    expect(savePostConfiguration).toHaveBeenCalledTimes(1)
  })

  it('disables Save when the view-model reports no pending change', () => {
    const { wrapper } = mountSection({ enabled: false })
    expect((wrapper.find('.post-config-save-btn').element as HTMLButtonElement).disabled).toBe(true)
  })
})
