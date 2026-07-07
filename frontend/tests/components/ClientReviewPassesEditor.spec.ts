// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import ClientReviewPassesEditor from '@/features/clients/components/ClientReviewPassesEditor.vue'
import type { AiConnectionDto } from '@/services/aiConnectionsService'
import type { components } from '@/types'

type ReviewPassEntry = components['schemas']['ReviewPassEntry']

const connections: AiConnectionDto[] = [
  {
    id: 'conn-a',
    displayName: 'Connection A',
    configuredModels: [
      { id: 'model-a1', remoteModelId: 'gpt-a1', displayName: 'A1 Chat', supportsChat: true, supportsEmbedding: false },
      { id: 'model-a-embed', remoteModelId: 'embed-a', displayName: 'A Embed', supportsChat: false, supportsEmbedding: true },
    ],
  },
  {
    id: 'conn-b',
    displayName: 'Connection B',
    configuredModels: [
      { id: 'model-b1', remoteModelId: 'gpt-b1', displayName: 'B1 Chat', supportsChat: true, supportsEmbedding: false },
    ],
  },
]

function mountEditor(modelValue: ReviewPassEntry[] = []) {
  return mount(ClientReviewPassesEditor, {
    props: { modelValue, connections },
  })
}

function lastEmit(wrapper: ReturnType<typeof mountEditor>): ReviewPassEntry[] | undefined {
  const events = wrapper.emitted('update:modelValue')
  return events ? (events[events.length - 1][0] as ReviewPassEntry[]) : undefined
}

async function addPass(wrapper: ReturnType<typeof mountEditor>) {
  await wrapper.find('[data-testid="review-passes-add"]').trigger('click')
}

async function selectPass(
  wrapper: ReturnType<typeof mountEditor>,
  rowIndex: number,
  connectionId: string,
  modelId: string,
) {
  await wrapper.findAll('[data-testid="review-pass-connection"]')[rowIndex].setValue(connectionId)
  await wrapper.findAll('[data-testid="review-pass-model"]')[rowIndex].setValue(modelId)
}

describe('ClientReviewPassesEditor', () => {
  it('shows the empty-list state and no rows when there are no passes', () => {
    const wrapper = mountEditor([])

    expect(wrapper.findAll('[data-testid="review-pass-row"]')).toHaveLength(0)
    expect(wrapper.text()).toContain('degrades to a single baseline pass')
  })

  it('hydrates rows from the model value and pre-selects the owning connection', () => {
    const wrapper = mountEditor([{ ordinal: 0, configuredModelId: 'model-b1' }])

    const rows = wrapper.findAll('[data-testid="review-pass-row"]')
    expect(rows).toHaveLength(1)
    expect((rows[0].find('[data-testid="review-pass-connection"]').element as HTMLSelectElement).value).toBe('conn-b')
    expect((rows[0].find('[data-testid="review-pass-model"]').element as HTMLSelectElement).value).toBe('model-b1')
  })

  it('adds a pass and emits a contiguous list once a model is chosen', async () => {
    const wrapper = mountEditor([])

    await addPass(wrapper)
    expect(wrapper.findAll('[data-testid="review-pass-row"]')).toHaveLength(1)
    // A blank row is not yet a persisted pass.
    expect(lastEmit(wrapper)).toEqual([])

    await selectPass(wrapper, 0, 'conn-a', 'model-a1')
    expect(lastEmit(wrapper)).toEqual([{ ordinal: 0, configuredModelId: 'model-a1', lens: null }])
  })

  it('only lists chat models for the chosen connection', async () => {
    const wrapper = mountEditor([])
    await addPass(wrapper)
    await wrapper.findAll('[data-testid="review-pass-connection"]')[0].setValue('conn-a')

    const modelOptionValues = wrapper
      .findAll('[data-testid="review-pass-model"]')[0]
      .findAll('option')
      .map((option) => (option.element as HTMLOptionElement).value)

    expect(modelOptionValues).toContain('model-a1')
    expect(modelOptionValues).not.toContain('model-a-embed')
  })

  it('emits contiguous ordinals for multiple passes', async () => {
    const wrapper = mountEditor([])

    await addPass(wrapper)
    await selectPass(wrapper, 0, 'conn-a', 'model-a1')
    await addPass(wrapper)
    await selectPass(wrapper, 1, 'conn-b', 'model-b1')

    expect(lastEmit(wrapper)).toEqual([
      { ordinal: 0, configuredModelId: 'model-a1', lens: null },
      { ordinal: 1, configuredModelId: 'model-b1', lens: null },
    ])
  })

  it('removes a pass and re-indexes the remaining ordinals', async () => {
    const wrapper = mountEditor([
      { ordinal: 0, configuredModelId: 'model-a1' },
      { ordinal: 1, configuredModelId: 'model-b1' },
    ])

    await wrapper.findAll('[data-testid="review-pass-remove"]')[0].trigger('click')

    expect(lastEmit(wrapper)).toEqual([{ ordinal: 0, configuredModelId: 'model-b1', lens: null }])
  })

  it('reorders passes with the move controls', async () => {
    const wrapper = mountEditor([
      { ordinal: 0, configuredModelId: 'model-a1' },
      { ordinal: 1, configuredModelId: 'model-b1' },
    ])

    await wrapper.findAll('[data-testid="review-pass-down"]')[0].trigger('click')

    expect(lastEmit(wrapper)).toEqual([
      { ordinal: 0, configuredModelId: 'model-b1', lens: null },
      { ordinal: 1, configuredModelId: 'model-a1', lens: null },
    ])
  })

  it('hydrates the owning connection when connections arrive after the model value', async () => {
    // Client (and its persisted pass list) loads before AI connections: mount with an empty connection list.
    const wrapper = mount(ClientReviewPassesEditor, {
      props: { modelValue: [{ ordinal: 0, configuredModelId: 'model-b1' }], connections: [] as AiConnectionDto[] },
    })

    const rows = wrapper.findAll('[data-testid="review-pass-row"]')
    expect(rows).toHaveLength(1)
    // With no connections loaded yet the owning connection cannot be resolved.
    expect((rows[0].find('[data-testid="review-pass-connection"]').element as HTMLSelectElement).value).toBe('')

    // Connections arrive; the row must back-fill its owning connection without dropping the model.
    await wrapper.setProps({ connections })

    const hydratedRows = wrapper.findAll('[data-testid="review-pass-row"]')
    expect((hydratedRows[0].find('[data-testid="review-pass-connection"]').element as HTMLSelectElement).value).toBe('conn-b')
    expect((hydratedRows[0].find('[data-testid="review-pass-model"]').element as HTMLSelectElement).value).toBe('model-b1')
  })

  it('flags a row whose model is no longer available and does not re-persist the dead id', () => {
    // The persisted pass references a model that is not on any loaded connection (deleted/renamed).
    const wrapper = mountEditor([{ ordinal: 0, configuredModelId: 'model-gone' }])

    const row = wrapper.findAll('[data-testid="review-pass-row"]')[0]
    // The dead-id row is surfaced (kept visible) as a half-configured row with a reselect warning.
    expect(row.find('[data-testid="review-pass-unavailable"]').exists()).toBe(true)
    expect((row.find('[data-testid="review-pass-connection"]').element as HTMLSelectElement).value).toBe('')

    // The dead id is never emitted back, so a stale save cannot silently re-persist it.
    expect(lastEmit(wrapper)).not.toBeDefined()
  })

  it('does not flag an in-progress row (connection chosen, model not yet picked)', async () => {
    const wrapper = mountEditor([])
    await addPass(wrapper)
    await wrapper.findAll('[data-testid="review-pass-connection"]')[0].setValue('conn-a')

    const row = wrapper.findAll('[data-testid="review-pass-row"]')[0]
    expect(row.find('[data-testid="review-pass-unavailable"]').exists()).toBe(false)
    // A half-configured row is kept visible but not emitted as a pass.
    expect(lastEmit(wrapper)).toEqual([])
  })

  it('enforces a maximum of four passes', async () => {
    const wrapper = mountEditor([])

    for (let index = 0; index < 4; index += 1) {
      await addPass(wrapper)
    }

    expect(wrapper.findAll('[data-testid="review-pass-row"]')).toHaveLength(4)
    expect(wrapper.find('[data-testid="review-passes-add"]').attributes('disabled')).toBeDefined()

    // A further add attempt is a no-op.
    await addPass(wrapper)
    expect(wrapper.findAll('[data-testid="review-pass-row"]')).toHaveLength(4)
  })
})
