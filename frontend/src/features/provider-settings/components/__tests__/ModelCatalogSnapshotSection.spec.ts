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
    await attach(wrapper, snapshot())

    await wrapper.get('[data-testid="snapshot-import"]').trigger('click')
    await flushPromises()

    expect(importSnapshot).toHaveBeenCalledTimes(1)
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

  it('explains that nothing is fetched automatically and overrides survive', () => {
    const text = mount(ModelCatalogSnapshotSection).text()

    expect(text).toContain('no outbound request')
    expect(text).toContain('overrides are left')
  })
})
