// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import ClientPostConfigSection from '../ClientPostConfigSection.vue'
import {
  ClientDetailVmKey,
  type CommentSeverity,
  type FindingPostDestination,
  type FindingRiskSeverity,
} from '@/features/clients/view-models/useClientDetailViewModel'
import { rowDestinations } from '@/features/clients/view-models/findingTypeDestinations'

function mountSection(
  options: { enabled?: boolean; limitError?: string; stored?: Record<string, FindingPostDestination> } = {},
) {
  const savePostConfiguration = vi.fn()
  const editedMaximumFindingsToPost = ref('20')
  const editedFindingBadgeStyle = ref('shieldsIo')
  const editedMinimumSeverityToPost = ref<CommentSeverity>('info')
  const editedAutoResolveSeverities = ref<CommentSeverity[]>([])
  const editedWithholdOutOfScopeFindings = ref(false)
  const editedMinimumRiskSeverityToPost = ref<FindingRiskSeverity>('low')
  const editedFindingTypeDestinations = ref<Record<string, FindingPostDestination>>(
    rowDestinations({ 'documentation-tests:missing': 'summary' }),
  )
  const vm = {
    client: ref({ id: 'c1', findingTypeDestinations: options.stored ?? { 'documentation-tests:missing': 'summary' } }),
    saveError: ref(''),
    editedFindingBadgeStyle,
    editedMinimumSeverityToPost,
    editedAutoResolveSeverities,
    editedWithholdOutOfScopeFindings,
    editedMinimumRiskSeverityToPost,
    editedFindingTypeDestinations,
    editedMaximumFindingsToPost,
    maximumFindingsToPostError: ref(options.limitError ?? ''),
    savePostConfiguration,
    isPostConfigButtonEnabled: () => options.enabled ?? true,
  }
  const wrapper = mount(ClientPostConfigSection, {
    global: { provide: { [ClientDetailVmKey as symbol]: vm } },
  })
  return { wrapper, vm, savePostConfiguration }
}

describe('ClientPostConfigSection', () => {
  it('offers image and plain gray finding badges', () => {
    const { wrapper } = mountSection()
    expect(wrapper.find('#findingBadgeStyle').exists()).toBe(true)
    expect(wrapper.findAll('#findingBadgeStyle option').map(option => option.text()))
      .toEqual(['Image badges (Shields.io)', 'Plain gray badges'])
  })

  it('binds the plain badge choice to the view-model', async () => {
    const { wrapper, vm } = mountSection()
    await wrapper.find('#findingBadgeStyle').setValue('plain')
    expect(vm.editedFindingBadgeStyle.value).toBe('plain')
    expect(wrapper.find('#findingBadgeStyle').attributes('aria-describedby')).toBe('findingBadgeStyleHelp')
  })

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

  it('binds the minimum-risk select to the view-model', async () => {
    const { wrapper, vm } = mountSection()
    expect(wrapper.findAll('#minimumRiskSeverityToPost option').map(option => option.attributes('value')))
      .toEqual(['low', 'medium', 'high', 'critical'])
    await wrapper.find('#minimumRiskSeverityToPost').setValue('high')
    expect(vm.editedMinimumRiskSeverityToPost.value).toBe('high')
  })

  it('offers the three destinations for every finding type, with two rows for documentation and tests', () => {
    const { wrapper } = mountSection()
    const selects = wrapper.findAll('.finding-type-destination select')
    expect(selects).toHaveLength(12)
    expect(selects.every(select => select.findAll('option').map(option => option.attributes('value')).join(',')
      === 'comment,summary,reviewOnly')).toBe(true)
    expect(wrapper.find('label[for="findingTypeDestination-documentation-tests-missing"]').text())
      .toBe('Missing tests or documentation')
    expect(wrapper.find('label[for="findingTypeDestination-documentation-tests"]').text())
      .toBe('Incorrect tests or documentation')
  })

  it('shows the stored destination of each row', () => {
    const { wrapper } = mountSection()
    const missing = wrapper.find('#findingTypeDestination-documentation-tests-missing').element as HTMLSelectElement
    const incorrect = wrapper.find('#findingTypeDestination-documentation-tests').element as HTMLSelectElement
    const security = wrapper.find('#findingTypeDestination-security').element as HTMLSelectElement
    expect(missing.value).toBe('summary')
    expect(incorrect.value).toBe('comment')
    expect(security.value).toBe('comment')
  })

  it('writes the chosen destination under the key of the row', async () => {
    const { wrapper, vm } = mountSection()

    await wrapper.find('#findingTypeDestination-naming-clarity').setValue('reviewOnly')
    await wrapper.find('#findingTypeDestination-documentation-tests').setValue('summary')

    expect(vm.editedFindingTypeDestinations.value['naming-clarity']).toBe('reviewOnly')
    expect(vm.editedFindingTypeDestinations.value['documentation-tests']).toBe('summary')
    expect(vm.editedFindingTypeDestinations.value['documentation-tests:missing']).toBe('summary')
    expect(vm.editedFindingTypeDestinations.value.security).toBe('comment')
  })

  it('shows a destination with a qualifier that no row offers as a note under the rows', () => {
    const { wrapper } = mountSection({ stored: { 'documentation-tests:missing': 'summary', 'security:incorrect': 'reviewOnly' } })

    const notes = wrapper.find('#findingTypeQualifiedDestinations')
    expect(notes.text()).toContain('take precedence over the row of their type')
    expect(notes.findAll('li').map(item => item.text())).toEqual(['Security, qualifier incorrect: ProPR review only'])
    expect(wrapper.findAll('.finding-type-destination select')).toHaveLength(12)
  })

  it('shows no note when every stored key has a row', () => {
    const { wrapper } = mountSection()
    expect(wrapper.find('#findingTypeQualifiedDestinations').exists()).toBe(false)
  })

  it('binds the maximum number of findings to post to the view-model', async () => {
    const { wrapper, vm } = mountSection()
    const input = wrapper.find('#maximumFindingsToPost')
    expect((input.element as HTMLInputElement).value).toBe('20')
    expect(input.attributes('placeholder')).toBe('No limit')

    await input.setValue('12')

    expect(String(vm.editedMaximumFindingsToPost.value)).toBe('12')
    expect(wrapper.find('#maximumFindingsToPostError').exists()).toBe(false)
  })

  it('shows why the maximum number of findings to post cannot be saved', () => {
    const { wrapper } = mountSection({ limitError: 'Maximum findings to post must be a whole number between 1 and 2147483647, or empty for no limit.' })
    expect(wrapper.find('#maximumFindingsToPostError').text()).toContain('Maximum findings to post must be a whole number')
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
