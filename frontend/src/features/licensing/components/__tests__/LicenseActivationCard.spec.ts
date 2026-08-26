// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { LicenseActivationRefusedError, type LicensingSummary } from '@/services/licensingService'

const summary = ref<LicensingSummary | null>(null)
const summaryStale = ref(false)
const activateMock = vi.fn()
const removeMock = vi.fn()
const reloadMock = vi.fn()

vi.mock('@/composables/useLicensing', () => ({
  useLicensing: () => ({
    summary,
    summaryStale,
    activate: activateMock,
    remove: removeMock,
    reload: reloadMock,
  }),
}))

function licensedSummary(): LicensingSummary {
  return {
    edition: 'commercial',
    activatedAt: '2026-01-02T09:15:00Z',
    capabilities: [],
    stage: 'active',
    notBefore: '2026-01-01T00:00:00Z',
    warningStartsAt: null,
    expiresAt: '2026-12-01T00:00:00Z',
    graceEndsAt: '2026-12-15T00:00:00Z',
    daysRemaining: 100,
    licensee: 'Contoso Engineering',
    licenseId: 'c9f5c9a2',
    limits: [],
    licensingIdentity: '5f2c0d1e',
    authorOverage: null,
    authorPeakMonth: null,
  }
}

async function mountCard() {
  const { default: LicenseActivationCard } =
    await import('@/features/licensing/components/LicenseActivationCard.vue')

  return mount(LicenseActivationCard)
}

async function pasteAndSubmit(wrapper: Awaited<ReturnType<typeof mountCard>>, token = 'a-license-document') {
  await wrapper.get('[data-testid="license-token-input"]').setValue(token)
  await wrapper.get('[data-testid="license-activate-button"]').trigger('click')
  await flushPromises()
}

describe('LicenseActivationCard', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    summary.value = {
      edition: 'community',
      activatedAt: null,
      capabilities: [],
      stage: 'none',
      notBefore: null,
      warningStartsAt: null,
      expiresAt: null,
      graceEndsAt: null,
      daysRemaining: null,
      licensee: null,
      licenseId: null,
      limits: [],
      licensingIdentity: null,
      authorOverage: null,
      authorPeakMonth: null,
    }
    summaryStale.value = false
    activateMock.mockResolvedValue({ isSummaryStale: false })
    removeMock.mockResolvedValue({ isSummaryStale: false })
    reloadMock.mockResolvedValue(undefined)
  })

  // An installation with no license needs to be told what makes the commercial edition available, because
  // there is nothing on the page that would otherwise say so.
  it('explains how activation works when no license is in force', async () => {
    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-activation-instruction"]').text())
      .toContain('Contact Meister DEV to obtain a license file')
  })

  it('leaves out the instruction once a license is in force', async () => {
    summary.value = licensedSummary()

    const wrapper = await mountCard()

    expect(wrapper.find('[data-testid="license-activation-instruction"]').exists()).toBe(false)
  })

  it('activates a pasted document without asking for confirmation', async () => {
    const wrapper = await mountCard()

    await pasteAndSubmit(wrapper)

    expect(activateMock).toHaveBeenCalledWith('a-license-document')
    expect(wrapper.get('[data-testid="license-activation-success"]').text())
      .toContain('The license was activated')
  })

  it('disables both document inputs until activation settles', async () => {
    let finishActivation: (() => void) | undefined
    activateMock.mockReturnValue(new Promise<{ isSummaryStale: boolean }>((resolve) => {
      finishActivation = () => resolve({ isSummaryStale: false })
    }))
    const wrapper = await mountCard()

    await wrapper.get('[data-testid="license-token-input"]').setValue('a-license-document')
    await wrapper.get('[data-testid="license-activate-button"]').trigger('click')
    await wrapper.vm.$nextTick()

    expect(wrapper.get('[data-testid="license-file-input"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-testid="license-token-input"]').attributes('disabled')).toBeDefined()

    finishActivation!()
    await flushPromises()

    expect(wrapper.get('[data-testid="license-file-input"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.get('[data-testid="license-token-input"]').attributes('disabled')).toBeUndefined()
  })

  it('does not activate while the current licensing state is unknown', async () => {
    summary.value = null
    const wrapper = await mountCard()

    await wrapper.get('[data-testid="license-token-input"]').setValue('a-license-document')

    expect(wrapper.get('[data-testid="license-activate-button"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="license-activate-button"]').trigger('click')
    await flushPromises()

    expect(activateMock).not.toHaveBeenCalled()
  })

  // A file the browser reads and a token an operator pastes reach the backend as the same value, so the file
  // path sends text rather than a multipart upload.
  it('reads a chosen file in the browser and sends its text', async () => {
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')

    Object.defineProperty(input.element, 'files', {
      value: [{ text: () => Promise.resolve('  document-from-a-file  ') }],
    })
    await input.trigger('change')
    await flushPromises()

    expect((wrapper.get('[data-testid="license-token-input"]').element as HTMLTextAreaElement).value)
      .toBe('document-from-a-file')

    await wrapper.get('[data-testid="license-activate-button"]').trigger('click')
    await flushPromises()

    expect(activateMock).toHaveBeenCalledWith('document-from-a-file')
  })

  // The same path over a real File, so the reading is exercised through the browser type rather than only
  // through a stub that happens to expose text().
  it('reads a real file the operator picked', async () => {
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')
    const file = new File(['  a-signed-document  '], 'license.lic', { type: 'text/plain' })

    Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
    await input.trigger('change')
    await flushPromises()

    expect((wrapper.get('[data-testid="license-token-input"]').element as HTMLTextAreaElement).value)
      .toBe('a-signed-document')
    expect(wrapper.find('[data-testid="license-file-error"]').exists()).toBe(false)
  })

  // A file the browser cannot read leaves the operator with a control that did nothing, so the card says so
  // and names the way round it.
  it('reports a file it could not read', async () => {
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')

    Object.defineProperty(input.element, 'files', {
      value: [{ text: () => Promise.reject(new Error('unreadable')) }],
      configurable: true,
    })
    await input.trigger('change')
    await flushPromises()

    expect(wrapper.get('[data-testid="license-file-error"]').text())
      .toBe('The file could not be read. Open it and paste its contents instead.')
    expect(activateMock).not.toHaveBeenCalled()
  })

  // The message asks for the contents to be pasted, so it cannot stay on screen beside the pasted document
  // and an enabled activation control.
  it('drops the read failure once the document is pasted instead', async () => {
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')

    Object.defineProperty(input.element, 'files', {
      value: [{ text: () => Promise.reject(new Error('unreadable')) }],
      configurable: true,
    })
    await input.trigger('change')
    await flushPromises()

    await wrapper.get('[data-testid="license-token-input"]').setValue('a-pasted-document')

    expect(wrapper.find('[data-testid="license-file-error"]').exists()).toBe(false)
  })

  // A screen reader reads the input's label and the button and would otherwise never reach the reason the
  // file was rejected, because nothing associates that text with the control.
  it('announces the read failure with the file input', async () => {
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')

    // Nothing has been rejected yet, so the input names only the element stating which file is picked.
    expect(input.attributes('aria-describedby'))
      .toBe(wrapper.get('[data-testid="license-file-input-name"]').attributes('id'))

    Object.defineProperty(input.element, 'files', {
      value: [{ text: () => Promise.reject(new Error('unreadable')) }],
      configurable: true,
    })
    await input.trigger('change')
    await flushPromises()

    const errorId = wrapper.get('[data-testid="license-file-error"]').attributes('id')
    expect(errorId).toBeTruthy()
    expect(input.attributes('aria-describedby')?.split(' ')).toContain(errorId)
  })

  it('clears a staged document when the chosen file cannot be read', async () => {
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')

    await wrapper.get('[data-testid="license-token-input"]').setValue('previously-staged-document')
    Object.defineProperty(input.element, 'files', {
      value: [{ text: () => Promise.reject(new Error('unreadable')) }],
      configurable: true,
    })
    await input.trigger('change')
    await flushPromises()

    expect((wrapper.get('[data-testid="license-token-input"]').element as HTMLTextAreaElement).value).toBe('')
    expect(wrapper.get('[data-testid="license-activate-button"]').attributes('disabled')).toBeDefined()
  })

  // Nothing was read, so nothing is staged. A file name beside a message asking for the contents to be
  // pasted instead would state otherwise.
  it('stops naming a file it could not read', async () => {
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')

    Object.defineProperty(input.element, 'files', {
      value: [{ name: 'unreadable.lic', text: () => Promise.reject(new Error('unreadable')) }],
      configurable: true,
    })
    await input.trigger('change')
    await flushPromises()

    expect(wrapper.get('[data-testid="license-file-input-name"]').text()).toBe('No file chosen')
  })

  // The native file control is replaced by one drawn like the rest of the application's buttons, so the card
  // has to say which file is staged. Nothing else on it names the file.
  it('names the file the operator picked', async () => {
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')

    Object.defineProperty(input.element, 'files', {
      value: [new File(['a-signed-document'], 'renewal.lic', { type: 'text/plain' })],
      configurable: true,
    })
    await input.trigger('change')
    await flushPromises()

    expect(wrapper.get('[data-testid="license-file-input-name"]').text()).toBe('renewal.lic')
  })

  // A file read that completes after a newer pick would otherwise stage one document while the card names
  // another, and the operator would submit content they never saw.
  it('keeps the newer file when an earlier read completes late', async () => {
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')

    let completeFirstRead: ((text: string) => void) | undefined
    Object.defineProperty(input.element, 'files', {
      value: [{
        name: 'first.lic',
        text: () => new Promise<string>((resolve) => { completeFirstRead = resolve }),
      }],
      configurable: true,
    })
    await input.trigger('change')

    Object.defineProperty(input.element, 'files', {
      value: [{ name: 'second.lic', text: () => Promise.resolve('second-document') }],
      configurable: true,
    })
    await input.trigger('change')
    await flushPromises()

    completeFirstRead!('first-document')
    await flushPromises()

    expect((wrapper.get('[data-testid="license-token-input"]').element as HTMLTextAreaElement).value)
      .toBe('second-document')
    expect(wrapper.get('[data-testid="license-file-input-name"]').text()).toBe('second.lic')
  })

  // Pasting supersedes a read still in flight, so the staged text is no longer the file's: the late read
  // leaves it alone and the card stops naming the file.
  it('keeps a pasted document when a file read completes late', async () => {
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')

    let completeRead: ((text: string) => void) | undefined
    Object.defineProperty(input.element, 'files', {
      value: [{
        name: 'staged.lic',
        text: () => new Promise<string>((resolve) => { completeRead = resolve }),
      }],
      configurable: true,
    })
    await input.trigger('change')

    await wrapper.get('[data-testid="license-token-input"]').setValue('pasted-document')
    completeRead!('file-document')
    await flushPromises()

    expect((wrapper.get('[data-testid="license-token-input"]').element as HTMLTextAreaElement).value)
      .toBe('pasted-document')
    expect(wrapper.get('[data-testid="license-file-input-name"]').text()).toBe('No file chosen')
  })

  it('states that no file is picked before one is', async () => {
    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-file-input-name"]').text()).toBe('No file chosen')
  })

  // The document is sent, so nothing is staged any more. A file name left beside the emptied box would
  // describe something the card no longer holds.
  it('stops naming the file once the license is activated', async () => {
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')

    Object.defineProperty(input.element, 'files', {
      value: [new File(['a-signed-document'], 'renewal.lic', { type: 'text/plain' })],
      configurable: true,
    })
    await input.trigger('change')
    await flushPromises()

    await wrapper.get('[data-testid="license-activate-button"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="license-file-input-name"]').text()).toBe('No file chosen')
  })

  // A refused document is still the one staged, and the operator corrects and retries it, so the card keeps
  // naming it.
  it('keeps naming the file after a refused activation', async () => {
    activateMock.mockRejectedValue(new Error('The network is unreachable.'))
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')

    Object.defineProperty(input.element, 'files', {
      value: [new File(['a-signed-document'], 'renewal.lic', { type: 'text/plain' })],
      configurable: true,
    })
    await input.trigger('change')
    await flushPromises()

    await wrapper.get('[data-testid="license-activate-button"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="license-file-input-name"]').text()).toBe('renewal.lic')
  })

  // A refused activation is corrected and retried with the same file. The input has to be cleared after each
  // read, or picking that file again raises no change event and the retry silently does nothing.
  it('lets the same file be picked again after a refusal', async () => {
    const wrapper = await mountCard()
    const input = wrapper.get('[data-testid="license-file-input"]')
    const element = input.element as HTMLInputElement

    // jsdom reports an empty value for a file input whatever the component did, so reading it back would
    // hold whether or not the clear happened. The assignment is recorded instead.
    const assignedValues: string[] = []
    Object.defineProperty(element, 'value', {
      configurable: true,
      get: () => '',
      set: (next: string) => { assignedValues.push(next) },
    })

    Object.defineProperty(element, 'files', {
      value: [{ text: () => Promise.resolve('a-signed-document') }],
      configurable: true,
    })
    await input.trigger('change')
    await flushPromises()

    expect(assignedValues).toEqual([''])
  })

  // Replacing discards what the installation currently runs on, so it is confirmed before anything is sent.
  it('asks before replacing the license in force', async () => {
    summary.value = licensedSummary()
    const wrapper = await mountCard()

    await wrapper.get('[data-testid="license-token-input"]').setValue('a-replacement')
    await wrapper.get('[data-testid="license-activate-button"]').trigger('click')
    await flushPromises()

    expect(activateMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Replace the license on this installation?')

    await wrapper.get('.confirm-dialog-actions .btn-danger').trigger('click')
    await flushPromises()

    expect(activateMock).toHaveBeenCalledWith('a-replacement')
  })

  it('sends nothing when the replacement confirmation is cancelled', async () => {
    summary.value = licensedSummary()
    const wrapper = await mountCard()

    await wrapper.get('[data-testid="license-token-input"]').setValue('a-replacement')
    await wrapper.get('[data-testid="license-activate-button"]').trigger('click')
    await wrapper.get('.confirm-dialog-actions button:not(.btn-danger)').trigger('click')
    await flushPromises()

    expect(activateMock).not.toHaveBeenCalled()
  })

  // Removal names what stops working, because it takes commercial capabilities away from the whole
  // installation.
  it('names what a removal stops before removing', async () => {
    summary.value = licensedSummary()
    const wrapper = await mountCard()

    await wrapper.get('[data-testid="license-remove-button"]').trigger('click')

    expect(wrapper.text()).toContain('every commercial capability stops being available')
    expect(removeMock).not.toHaveBeenCalled()

    await wrapper.get('.confirm-dialog-actions .btn-danger').trigger('click')
    await flushPromises()

    expect(removeMock).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-testid="license-activation-success"]').text())
      .toContain('This installation runs as Community')
  })

  it('offers no removal control when there is no license to remove', async () => {
    const wrapper = await mountCard()

    expect(wrapper.find('[data-testid="license-remove-button"]').exists()).toBe(false)
  })

  // Each refusal reason needs a different action from the operator, so each renders its own message rather
  // than one generic refusal.
  it.each([
    ['malformed', 'Activate the file as it was delivered'],
    ['unsupportedSchemaVersion', 'Update this installation'],
    ['untrustedSigner', 'not signed by Meister DEV'],
    ['expired', 'Activate a renewed license'],
    ['notYetValid', 'term has not started yet'],
    ['noAnchorInThisBuild', 'cannot verify licenses'],
  ])('renders the message written for the %s refusal', async (reason, expected) => {
    activateMock.mockRejectedValue(
      new LicenseActivationRefusedError(reason as never, 'the backend wording'),
    )
    const wrapper = await mountCard()

    await pasteAndSubmit(wrapper)

    expect(wrapper.get('[data-testid="license-activation-error"]').text()).toContain(expected)
  })

  // A newer backend can send a reason this build does not know. Falling back to what it sent is better than
  // rendering nothing.
  it('falls back to the message the backend sent for an unknown reason', async () => {
    activateMock.mockRejectedValue(
      new LicenseActivationRefusedError('somethingNewer' as never, 'the backend wording'),
    )
    const wrapper = await mountCard()

    await pasteAndSubmit(wrapper)

    expect(wrapper.get('[data-testid="license-activation-error"]').text()).toBe('the backend wording')
  })

  it('reports a failure that is not a refusal', async () => {
    activateMock.mockRejectedValue(new Error('The network is unreachable.'))
    const wrapper = await mountCard()

    await pasteAndSubmit(wrapper)

    expect(wrapper.get('[data-testid="license-activation-error"]').text()).toBe('The network is unreachable.')
    expect(wrapper.find('[data-testid="license-summary-stale"]').exists()).toBe(false)
  })

  it('shows no stale notice when the state was read back after the activation', async () => {
    const wrapper = await mountCard()

    await pasteAndSubmit(wrapper)

    expect(wrapper.find('[data-testid="license-summary-stale"]').exists()).toBe(false)
  })

  // The activation and the read that follows it fail for different reasons. The license is in force once the
  // activation is accepted, so reporting a failed read as a refused document would have an operator submit the
  // same license again, which records a second activation for one license.
  it('reports an activation whose state read failed as one that happened', async () => {
    activateMock.mockResolvedValue({ isSummaryStale: true })
    const wrapper = await mountCard()

    await pasteAndSubmit(wrapper)

    expect(wrapper.find('[data-testid="license-activation-error"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="license-activation-success"]').text())
      .toContain('The license was activated')
    expect(wrapper.get('[data-testid="license-summary-stale"]').text())
      .toContain('could not be read after the change')
  })

  // The document was accepted, so it is no longer staged. Leaving it in the box would let a retry send the
  // same license a second time.
  it('clears the staged document when the state read after the activation failed', async () => {
    activateMock.mockResolvedValue({ isSummaryStale: true })
    const wrapper = await mountCard()

    await pasteAndSubmit(wrapper)

    expect((wrapper.get('[data-testid="license-token-input"]').element as HTMLTextAreaElement).value).toBe('')
    expect(wrapper.get('[data-testid="license-activate-button"]').attributes('disabled')).toBeDefined()
  })

  // The retry the notice offers reads the state again. It does not repeat the activation, which was written.
  it('re-reads the state from the stale notice without sending the document again', async () => {
    activateMock.mockResolvedValue({ isSummaryStale: true })
    const wrapper = await mountCard()
    await pasteAndSubmit(wrapper)

    await wrapper.get('[data-testid="license-summary-refresh"]').trigger('click')
    await flushPromises()

    expect(reloadMock).toHaveBeenCalledTimes(1)
    expect(activateMock).toHaveBeenCalledTimes(1)
    expect(wrapper.find('[data-testid="license-summary-stale"]').exists()).toBe(false)
  })

  it('keeps the stale notice when the re-read fails as well', async () => {
    activateMock.mockResolvedValue({ isSummaryStale: true })
    reloadMock.mockImplementation(async () => {
      summaryStale.value = true
    })
    const wrapper = await mountCard()
    await pasteAndSubmit(wrapper)

    await wrapper.get('[data-testid="license-summary-refresh"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="license-summary-stale"]').exists()).toBe(true)
  })

  it('reports a removal whose state read failed as one that happened', async () => {
    summary.value = licensedSummary()
    removeMock.mockResolvedValue({ isSummaryStale: true })
    const wrapper = await mountCard()

    await wrapper.get('[data-testid="license-remove-button"]').trigger('click')
    await wrapper.get('.confirm-dialog-actions .btn-danger').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="license-activation-error"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="license-activation-success"]').text())
      .toContain('This installation runs as Community')
    expect(wrapper.find('[data-testid="license-summary-stale"]').exists()).toBe(true)
  })

  it('sends nothing while the document is empty', async () => {
    const wrapper = await mountCard()

    await wrapper.get('[data-testid="license-activate-button"]').trigger('click')
    await flushPromises()

    expect(activateMock).not.toHaveBeenCalled()
  })
})
