// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import ModelCatalogSnapshotSection from '../ModelCatalogSnapshotSection.vue'

const importSnapshot = vi.fn()

vi.mock('@/services/modelCatalogService', () => ({
  importSnapshot: (...a: unknown[]) => importSnapshot(...a),
}))

/** Attaches a file to the input, since jsdom will not let a FileList be assigned directly. */
function attach(wrapper: ReturnType<typeof mount>, file: File): Promise<void> {
  const input = wrapper.get('[data-testid="snapshot-file"]').element as HTMLInputElement
  Object.defineProperty(input, 'files', { value: [file], configurable: true })
  return wrapper.get('[data-testid="snapshot-file"]').trigger('change')
}

const snapshot = () => new File(['{}'], 'models.dev.json', { type: 'application/json' })

describe('ModelCatalogSnapshotSection', () => {
  beforeEach(() => {
    importSnapshot.mockReset()
    importSnapshot.mockResolvedValue({ entriesWritten: 1021 })
  })

  it('cannot import until a file is chosen', () => {
    const wrapper = mount(ModelCatalogSnapshotSection)

    expect((wrapper.get('[data-testid="snapshot-import"]').element as HTMLButtonElement).disabled).toBe(true)
  })

  it('reports how many entries the import wrote', async () => {
    const wrapper = mount(ModelCatalogSnapshotSection)
    const chosen = snapshot()
    await attach(wrapper, chosen)

    await wrapper.get('[data-testid="snapshot-import"]').trigger('click')
    await flushPromises()

    expect(importSnapshot).toHaveBeenCalledWith(chosen)
    expect(wrapper.get('[data-testid="snapshot-result"]').text()).toContain('1,021')
  })

  // A malformed snapshot is operator error, so the server's stated cause has to reach the operator rather than
  // stopping at the log.
  it('surfaces the reason an import was rejected', async () => {
    importSnapshot.mockRejectedValue(new Error('The snapshot could not be read: unexpected token'))
    const wrapper = mount(ModelCatalogSnapshotSection)
    await attach(wrapper, snapshot())

    await wrapper.get('[data-testid="snapshot-import"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="snapshot-error"]').text()).toContain('unexpected token')
    expect(wrapper.find('[data-testid="snapshot-result"]').exists()).toBe(false)
  })

  // A screen reader reads the input's label and the button and would otherwise never reach the reason an
  // import was rejected, because nothing associates that text with the control.
  it('announces the rejection reason with the file input', async () => {
    importSnapshot.mockRejectedValue(new Error('The snapshot could not be read: unexpected token'))
    const wrapper = mount(ModelCatalogSnapshotSection)
    const input = wrapper.get('[data-testid="snapshot-file"]')
    await attach(wrapper, snapshot())

    // Nothing has been rejected yet, so the input names only the element stating which snapshot is chosen.
    expect(input.attributes('aria-describedby'))
      .toBe(wrapper.get('[data-testid="snapshot-file-name"]').attributes('id'))

    await wrapper.get('[data-testid="snapshot-import"]').trigger('click')
    await flushPromises()

    const errorId = wrapper.get('[data-testid="snapshot-error"]').attributes('id')
    expect(errorId).toBeTruthy()
    expect(input.attributes('aria-describedby')?.split(' ')).toContain(errorId)
  })

  // The native file control is replaced by one drawn like the rest of the application's buttons, so the
  // section has to say which snapshot is about to be imported.
  it('names the chosen snapshot', async () => {
    const wrapper = mount(ModelCatalogSnapshotSection)
    await attach(wrapper, snapshot())

    expect(wrapper.get('[data-testid="snapshot-file-name"]').text()).toBe('models.dev.json')
  })

  // Choosing another snapshot clears the messages, and while an import is in flight the messages on screen
  // belong to that request, so the picker is closed until it finishes.
  it('closes the picker while an import runs', async () => {
    let finishImport: (result: { entriesWritten: number }) => void = () => {}
    importSnapshot.mockReturnValue(new Promise((resolve) => {
      finishImport = resolve
    }))
    const wrapper = mount(ModelCatalogSnapshotSection)
    await attach(wrapper, snapshot())
    const picker = () => wrapper.get('[data-testid="snapshot-file"]').element as HTMLInputElement

    await wrapper.get('[data-testid="snapshot-import"]').trigger('click')

    expect(picker().disabled).toBe(true)

    finishImport({ entriesWritten: 3 })
    await flushPromises()

    expect(picker().disabled).toBe(false)
  })

  it('states that no snapshot is chosen before one is', () => {
    const wrapper = mount(ModelCatalogSnapshotSection)

    expect(wrapper.get('[data-testid="snapshot-file-name"]').text()).toBe('No file chosen')
  })

  // A refused import is corrected and retried with the same snapshot. The native input has to be cleared after
  // every pick, or choosing that file again raises no change event and the retry does nothing while the
  // previous error stays on screen. jsdom does not suppress the event for an unchanged selection, so the reset
  // is observed as the assignment to the input rather than through a second pick.
  it('clears the native input so the same snapshot can be picked again', async () => {
    const wrapper = mount(ModelCatalogSnapshotSection)
    const input = wrapper.get('[data-testid="snapshot-file"]').element as HTMLInputElement
    const assignedValues: string[] = []
    Object.defineProperty(input, 'value', {
      configurable: true,
      get: () => '',
      set: (next: string) => { assignedValues.push(next) },
    })

    await attach(wrapper, snapshot())

    expect(assignedValues).toEqual([''])
    // The file itself is held by the component, so clearing the input does not unstage the snapshot.
    expect(wrapper.get('[data-testid="snapshot-file-name"]').text()).toBe('models.dev.json')
  })

  it('explains that nothing is fetched automatically and overrides survive', () => {
    const text = mount(ModelCatalogSnapshotSection).text()

    expect(text).toContain('no outbound request')
    expect(text).toContain('overrides are left')
  })
})
