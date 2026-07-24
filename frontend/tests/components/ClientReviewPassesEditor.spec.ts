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

// The add/edit form lives in a modal now: adding a pass means opening it, filling the fields, and saving.
async function openAddModal(wrapper: ReturnType<typeof mountEditor>) {
  await wrapper.find('[data-testid="review-passes-add"]').trigger('click')
}

async function openEditModal(wrapper: ReturnType<typeof mountEditor>, rowIndex: number) {
  await wrapper.findAll('[data-testid="review-pass-edit"]')[rowIndex].trigger('click')
}

async function addRawPass(
  wrapper: ReturnType<typeof mountEditor>,
  connectionId: string,
  modelId: string,
  opts: { shadow?: boolean } = {},
) {
  await openAddModal(wrapper)
  if (opts.shadow) {
    await wrapper.find('[data-testid="review-pass-shadow"]').setValue(true)
  }
  await wrapper.find('[data-testid="review-pass-connection"]').setValue(connectionId)
  await wrapper.find('[data-testid="review-pass-model"]').setValue(modelId)
  await wrapper.find('[data-testid="review-pass-save"]').trigger('click')
}

describe('ClientReviewPassesEditor', () => {
  it('shows the empty-list state and no rows when there are no passes', () => {
    const wrapper = mountEditor([])

    expect(wrapper.findAll('[data-testid="review-pass-row"]')).toHaveLength(0)
    expect(wrapper.text()).toContain('degrades to a single baseline pass')
  })

  it('hydrates rows from the model value and shows the model in the table + edit modal', async () => {
    const wrapper = mountEditor([{ ordinal: 0, configuredModelId: 'model-b1' }])

    const rows = wrapper.findAll('[data-testid="review-pass-row"]')
    expect(rows).toHaveLength(1)
    // The read-only row shows the resolved model's display name.
    expect(rows[0].text()).toContain('B1 Chat')

    // Editing pre-selects the owning connection + model in the modal.
    await openEditModal(wrapper, 0)
    expect((wrapper.find('[data-testid="review-pass-connection"]').element as HTMLSelectElement).value).toBe('conn-b')
    expect((wrapper.find('[data-testid="review-pass-model"]').element as HTMLSelectElement).value).toBe('model-b1')
  })

  it('adds a pass via the modal and emits only after saving a chosen model', async () => {
    const wrapper = mountEditor([])

    await openAddModal(wrapper)
    // Opening the modal neither creates a row nor emits.
    expect(wrapper.findAll('[data-testid="review-pass-row"]')).toHaveLength(0)
    expect(lastEmit(wrapper)).toBeUndefined()
    // Save is disabled until a model source is chosen.
    expect(wrapper.find('[data-testid="review-pass-save"]').attributes('disabled')).toBeDefined()

    await wrapper.find('[data-testid="review-pass-connection"]').setValue('conn-a')
    await wrapper.find('[data-testid="review-pass-model"]').setValue('model-a1')
    await wrapper.find('[data-testid="review-pass-save"]').trigger('click')

    expect(wrapper.findAll('[data-testid="review-pass-row"]')).toHaveLength(1)
    expect(lastEmit(wrapper)).toEqual([{ ordinal: 0, configuredModelId: 'model-a1', lens: null, scope: null, shadow: false, reasoningEffort: 'none' }])
  })

  it('only lists chat models for the chosen connection', async () => {
    const wrapper = mountEditor([])
    await openAddModal(wrapper)
    await wrapper.find('[data-testid="review-pass-connection"]').setValue('conn-a')

    const modelOptionValues = wrapper
      .find('[data-testid="review-pass-model"]')
      .findAll('option')
      .map((option) => (option.element as HTMLOptionElement).value)

    expect(modelOptionValues).toContain('model-a1')
    expect(modelOptionValues).not.toContain('model-a-embed')
  })

  it('emits contiguous ordinals for multiple passes', async () => {
    const wrapper = mountEditor([])

    await addRawPass(wrapper, 'conn-a', 'model-a1')
    await addRawPass(wrapper, 'conn-b', 'model-b1')

    expect(lastEmit(wrapper)).toEqual([
      { ordinal: 0, configuredModelId: 'model-a1', lens: null, scope: null, shadow: false, reasoningEffort: 'none' },
      { ordinal: 1, configuredModelId: 'model-b1', lens: null, scope: null, shadow: false, reasoningEffort: 'none' },
    ])
  })

  it('removes a pass and re-indexes the remaining ordinals', async () => {
    const wrapper = mountEditor([
      { ordinal: 0, configuredModelId: 'model-a1' },
      { ordinal: 1, configuredModelId: 'model-b1' },
    ])

    await wrapper.findAll('[data-testid="review-pass-remove"]')[0].trigger('click')

    expect(lastEmit(wrapper)).toEqual([{ ordinal: 0, configuredModelId: 'model-b1', lens: null, scope: null, shadow: false, reasoningEffort: 'none' }])
  })

  it('reorders passes with the move controls', async () => {
    const wrapper = mountEditor([
      { ordinal: 0, configuredModelId: 'model-a1' },
      { ordinal: 1, configuredModelId: 'model-b1' },
    ])

    await wrapper.findAll('[data-testid="review-pass-down"]')[0].trigger('click')

    expect(lastEmit(wrapper)).toEqual([
      { ordinal: 0, configuredModelId: 'model-b1', lens: null, scope: null, shadow: false, reasoningEffort: 'none' },
      { ordinal: 1, configuredModelId: 'model-a1', lens: null, scope: null, shadow: false, reasoningEffort: 'none' },
    ])
  })

  it('hydrates the owning connection when connections arrive after the model value', async () => {
    // Client (and its persisted pass list) loads before AI connections: mount with an empty connection list.
    const wrapper = mount(ClientReviewPassesEditor, {
      props: { modelValue: [{ ordinal: 0, configuredModelId: 'model-b1' }], connections: [] as AiConnectionDto[] },
    })

    expect(wrapper.findAll('[data-testid="review-pass-row"]')).toHaveLength(1)

    // Connections arrive; the row must back-fill its owning connection without dropping the model, so editing
    // opens with the connection resolved.
    await wrapper.setProps({ connections })
    await openEditModal(wrapper, 0)

    expect((wrapper.find('[data-testid="review-pass-connection"]').element as HTMLSelectElement).value).toBe('conn-b')
    expect((wrapper.find('[data-testid="review-pass-model"]').element as HTMLSelectElement).value).toBe('model-b1')
  })

  it('flags a row whose model is no longer available and does not re-persist the dead id', () => {
    // The persisted pass references a model that is not on any loaded connection (deleted/renamed).
    const wrapper = mountEditor([{ ordinal: 0, configuredModelId: 'model-gone' }])

    const row = wrapper.findAll('[data-testid="review-pass-row"]')[0]
    // The dead-id row is surfaced (kept visible) with a reselect warning.
    expect(row.find('[data-testid="review-pass-unavailable"]').exists()).toBe(true)

    // The dead id is never emitted back, so a stale save cannot silently re-persist it.
    expect(lastEmit(wrapper)).not.toBeDefined()
  })

  it('keeps save disabled until a model source is chosen', async () => {
    const wrapper = mountEditor([])
    await openAddModal(wrapper)

    expect(wrapper.find('[data-testid="review-pass-save"]').attributes('disabled')).toBeDefined()

    await wrapper.find('[data-testid="review-pass-connection"]').setValue('conn-a')
    await wrapper.find('[data-testid="review-pass-model"]').setValue('model-a1')

    expect(wrapper.find('[data-testid="review-pass-save"]').attributes('disabled')).toBeUndefined()
  })

  it('enforces a maximum of four passes', async () => {
    const wrapper = mountEditor([])

    // Four distinct passes: the two chat models under the default tuple, then again as shadow passes (a
    // distinct (model, lens, scope, shadow) tuple), so each save is a valid non-duplicate pass.
    await addRawPass(wrapper, 'conn-a', 'model-a1')
    await addRawPass(wrapper, 'conn-b', 'model-b1')
    await addRawPass(wrapper, 'conn-a', 'model-a1', { shadow: true })
    await addRawPass(wrapper, 'conn-b', 'model-b1', { shadow: true })

    expect(wrapper.findAll('[data-testid="review-pass-row"]')).toHaveLength(4)
    expect(wrapper.find('[data-testid="review-passes-add"]').attributes('disabled')).toBeDefined()

    // A further add attempt is a no-op (the button is disabled and openAdd guards on the max).
    await openAddModal(wrapper)
    expect(wrapper.findAll('[data-testid="review-pass-row"]')).toHaveLength(4)
  })
})
