// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import ClientBudgetTab from '../ClientBudgetTab.vue'
import { ClientDetailVmKey } from '@/features/clients/view-models/useClientDetailViewModel'

const ADMISSION_INPUT_TESTIDS = [
  'admission-max-changed-files',
  'admission-max-changed-lines',
  'admission-max-diff-bytes',
  'admission-max-reviews-per-hour',
  'admission-max-repository-megabytes',
]

function mountTab(
  options: { admissionEnabled?: boolean; admissionPolicyError?: string; saveError?: string; admissionSaveError?: string } = {},
) {
  const saveAdmissionPolicy = vi.fn()
  const edited = {
    editedAdmissionMaxChangedFiles: ref('150'),
    editedAdmissionMaxChangedLines: ref(''),
    editedAdmissionMaxDiffBytes: ref(''),
    editedAdmissionMaxReviewsPerPullRequestPerHour: ref('4'),
    editedAdmissionMaxRepositoryMegabytes: ref(''),
  }
  const vm = {
    client: ref({ id: 'c1' }),
    saveError: ref(options.saveError ?? ''),
    admissionSaveError: ref(options.admissionSaveError ?? ''),
    admissionPolicyError: ref(options.admissionPolicyError ?? ''),
    editedMonthlyBudgetSoftCapUsd: ref(''),
    editedMonthlyBudgetHardCapUsd: ref(''),
    editedPullRequestBudgetSoftCapUsd: ref(''),
    editedPullRequestBudgetHardCapUsd: ref(''),
    editedIncrementBudgetSoftCapUsd: ref(''),
    editedIncrementBudgetHardCapUsd: ref(''),
    ...edited,
    saveBudgetConfig: vi.fn(),
    saveAdmissionPolicy,
    isBudgetButtonEnabled: () => false,
    isAdmissionButtonEnabled: () => options.admissionEnabled ?? true,
    isBudgetingAvailable: ref(true),
    budgetingUpgradeMessage: ref(''),
  }
  const wrapper = mount(ClientBudgetTab, {
    global: { provide: { [ClientDetailVmKey as symbol]: vm } },
  })
  return { wrapper, vm, saveAdmissionPolicy, edited }
}

describe('ClientBudgetTab review limits', () => {
  it('shows a field for each of the five bounds, with a stored bound filled in', () => {
    const { wrapper } = mountTab()

    expect((wrapper.find('[data-testid="admission-max-changed-files"]').element as HTMLInputElement).value).toBe('150')
    expect(wrapper.find('[data-testid="admission-max-changed-lines"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="admission-max-diff-bytes"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="admission-max-reviews-per-hour"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="admission-max-repository-megabytes"]').exists()).toBe(true)
  })

  it('explains what a breach does: a refusal with a notice, and a wait for a push burst', () => {
    const { wrapper } = mountTab()
    const text = wrapper.text()

    expect(text).toContain('not reviewed')
    expect(text).toContain('posts the measured value')
    expect(text).toContain('starts by itself once the hour has passed')
  })

  it('binds an edit to the view-model and saves it', async () => {
    const { wrapper, edited, saveAdmissionPolicy } = mountTab()

    await wrapper.find('[data-testid="admission-max-changed-lines"]').setValue('20000')
    await wrapper.find('[data-testid="admission-save"]').trigger('click')

    // A number input coerces its value, which capFromInput accepts as a number.
    expect(Number(edited.editedAdmissionMaxChangedLines.value)).toBe(20_000)
    expect(saveAdmissionPolicy).toHaveBeenCalledOnce()
  })

  it('clears a bound when its field is emptied', async () => {
    const { wrapper, edited } = mountTab()

    await wrapper.find('[data-testid="admission-max-changed-files"]').setValue('')

    expect(edited.editedAdmissionMaxChangedFiles.value).toBe('')
  })

  it('offers no save while nothing was edited', () => {
    const { wrapper } = mountTab({ admissionEnabled: false })

    expect((wrapper.find('[data-testid="admission-save"]').element as HTMLButtonElement).disabled).toBe(true)
  })

  // The bounds are stored as 32-bit integers, so the spinner and a submitted form stop at the largest one the
  // backend can hold instead of offering a value it refuses.
  it('bounds every field at the largest value the backend stores', () => {
    const { wrapper } = mountTab()

    for (const testId of ADMISSION_INPUT_TESTIDS) {
      expect(wrapper.find(`[data-testid="${testId}"]`).attributes('max')).toBe('2147483647')
    }
  })

  // The failure has to reach an operator who is reading the Review limits fields, including one using a
  // screen reader, which needs the message announced and tied to the fields it is about.
  it('announces the validation message and ties every admission field to it', () => {
    const { wrapper } = mountTab({ admissionPolicyError: 'Repository size (MB) must be a whole number between 1 and 2147483647, or blank for no bound.' })

    const message = wrapper.find('[data-testid="admission-validation"]')
    expect(message.attributes('role')).toBe('alert')
    expect(message.text()).toContain('Repository size (MB)')

    const messageId = message.attributes('id')
    expect(messageId).toBeTruthy()
    for (const testId of ADMISSION_INPUT_TESTIDS) {
      // The field may point at other descriptions as well, so the message has to be among what it names.
      const described = wrapper.find(`[data-testid="${testId}"]`).attributes('aria-describedby')?.split(/\s+/) ?? []
      expect(described).toContain(messageId)
    }
  })

  // The failure has to appear beside the Save that produced it, and be announced to a screen reader.
  it('reports a failed admission save in the Review limits card', () => {
    const { wrapper } = mountTab({ admissionSaveError: 'Failed to save the review limits.' })

    const reported = wrapper.find('[data-testid="admission-save-error"]')
    expect(reported.text()).toBe('Failed to save the review limits.')
    expect(reported.attributes('role')).toBe('alert')
  })

  // The Budget card above has its own Save and its own error. Reporting that one here as well tells an
  // operator the review limits failed when the budget did.
  it('reports a failed budget save only in the Budget card', () => {
    const { wrapper } = mountTab({ saveError: 'Failed to save budget.' })

    expect(wrapper.find('[data-testid="admission-save-error"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('Failed to save budget.')
  })

  it('reports nothing in the Review limits card while a save has not failed', () => {
    const { wrapper } = mountTab()

    expect(wrapper.find('[data-testid="admission-save-error"]').exists()).toBe(false)
  })
})
