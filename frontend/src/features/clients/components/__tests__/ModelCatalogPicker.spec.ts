// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import ModelCatalogPicker from '../ModelCatalogPicker.vue'
import { applyCatalogEntryToModel } from '../aiConnectionsFormatters'
import type { EditableModel } from '../aiConnectionsForm.types'
import type { AiModelCatalogEntryDto } from '@/services/modelCatalogService'

// The picker takes its loaders as props so one component serves both the client and tenant scopes; the tests
// therefore hand it stubs directly rather than mocking a service module.
const listProviders = vi.fn()
const listModels = vi.fn()

const picker = () =>
  mount(ModelCatalogPicker, {
    props: {
      loadProviders: () => listProviders(),
      loadModels: (providerId: string) => listModels(providerId),
    },
  })

const entry = (overrides: Partial<AiModelCatalogEntryDto> = {}): AiModelCatalogEntryDto =>
  ({
    providerId: 'deepseek',
    providerName: 'DeepSeek',
    remoteModelId: 'deepseek-reasoner',
    displayName: 'DeepSeek Reasoner',
    family: 'deepseek',
    supportsToolUse: true,
    supportsStructuredOutput: true,
    supportsReasoning: true,
    supportsPromptCaching: false,
    reasoningContentField: 'reasoning_content',
    maxContextTokens: 131072,
    maxOutputTokens: 65536,
    inputCostPer1MUsd: 0.28,
    outputCostPer1MUsd: 0.42,
    cachedInputCostPer1MUsd: 0.028,
    cacheWriteCostPer1MUsd: null,
    openWeights: true,
    releaseDate: '2026-01-20',
    pricingLayer: 'global',
    ...overrides,
  }) as AiModelCatalogEntryDto

describe('ModelCatalogPicker', () => {
  beforeEach(() => {
    listProviders.mockReset()
    listModels.mockReset()
    listProviders.mockResolvedValue([{ providerId: 'deepseek', providerName: 'DeepSeek', modelCount: 2 }])
    listModels.mockResolvedValue([entry()])
  })

  it('loads the catalog only once the operator opens it', async () => {
    const wrapper = picker()

    // Browsing is opt-in: an operator typing a model id by hand must not pay for a catalog fetch.
    expect(listProviders).not.toHaveBeenCalled()

    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()

    expect(listProviders).toHaveBeenCalled()
    expect(wrapper.find('[data-testid="catalog-list"]').exists()).toBe(true)
  })

  it('emits the chosen entry rather than mutating anything itself', async () => {
    const wrapper = picker()
    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()

    await wrapper.get('.catalog-entry').trigger('click')

    const emitted = wrapper.emitted('pick')
    expect(emitted).toHaveLength(1)
    expect((emitted?.[0][0] as AiModelCatalogEntryDto).remoteModelId).toBe('deepseek-reasoner')
    // Picking closes the panel so the operator returns to the form they were filling.
    expect(wrapper.find('[data-testid="catalog-picker-panel"]').exists()).toBe(false)
  })

  it('filters by model id, name, or family', async () => {
    listModels.mockResolvedValue([entry(), entry({ remoteModelId: 'deepseek-chat', displayName: 'DeepSeek Chat' })])
    const wrapper = picker()
    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()

    await wrapper.get('[data-testid="catalog-search"]').setValue('reasoner')

    expect(wrapper.findAll('.catalog-entry')).toHaveLength(1)
  })

  it('marks a negotiated rate as such', async () => {
    listModels.mockResolvedValue([entry({ pricingLayer: 'tenantOverride', inputCostPer1MUsd: 0.1 })])
    const wrapper = picker()
    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('negotiated')
  })

  // A catalog that cannot be reached must not block the operator: hand-entry is still available, and the
  // message says so.
  it('reports a load failure without hiding the manual route', async () => {
    listProviders.mockRejectedValue(new Error('boom'))
    const wrapper = picker()

    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="catalog-error"]').text()).toContain('could not be loaded')
  })

  it('says an empty result still allows hand-entry', async () => {
    listModels.mockResolvedValue([])
    const wrapper = picker()
    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="catalog-empty"]').text()).toContain('by hand')
  })
})

describe('applyCatalogEntryToModel', () => {
  const model = (): EditableModel => ({
    localId: 'l1',
    existingId: null,
    remoteModelId: '',
    displayName: '',
    kind: 'chat',
    tokenizerName: '',
    maxInputTokens: '',
    maxContextTokens: '',
    embeddingDimensions: '',
    supportsStructuredOutput: false,
    supportsToolUse: false,
    inputCostPer1MUsd: '',
    outputCostPer1MUsd: '',
    cachedInputCostPer1MUsd: '',
    cacheWriteCostPer1MUsd: '',
    supportsReasoning: false,
    supportsPromptCaching: false,
    reasoningContentField: '',
    defaultReasoningLevel: null,
    reasoningLevels: null,
    reasoningLevelsSource: null,
    detectReasoningLevels: false,
  })

  // The default level is one of the levels of the previous model, which the picked model need not accept.
  it('clears the default level together with the levels of the previous model', () => {
    const target = model()
    target.reasoningLevels = ['low', 'high']
    target.defaultReasoningLevel = 'high'

    applyCatalogEntryToModel(target, entry())

    expect(target.defaultReasoningLevel).toBeNull()
  })

  // The default level was chosen for the model picked again. The save detects the levels again and adds the default
  // level to them when they lack it.
  it('keeps the default level when the operator picks the same model again', () => {
    const target = model()
    target.remoteModelId = 'deepseek-reasoner'
    target.reasoningLevels = ['low', 'high']
    target.reasoningLevelsSource = 'catalog'
    target.defaultReasoningLevel = 'high'

    applyCatalogEntryToModel(target, entry())

    expect(target.defaultReasoningLevel).toBe('high')
    expect(target.reasoningLevels).toBeNull()
    expect(target.reasoningLevelsSource).toBeNull()
    expect(target.detectReasoningLevels).toBe(true)
  })

  // The save trims the remote model id, and the server matches a model by its id without regard to case.
  it('takes an id that differs only in case or surrounding spaces for the same model', () => {
    const target = model()
    target.remoteModelId = ' DeepSeek-Reasoner '
    target.defaultReasoningLevel = 'high'

    applyCatalogEntryToModel(target, entry())

    expect(target.remoteModelId).toBe('deepseek-reasoner')
    expect(target.defaultReasoningLevel).toBe('high')
  })

  // The levels belong to the model picked, and the save detects them from the catalog and the provider family.
  it('lets the save detect the reasoning levels of the model picked', () => {
    const target = model()
    target.reasoningLevels = ['max']
    target.reasoningLevelsSource = 'operator'

    applyCatalogEntryToModel(target, entry())

    expect(target.reasoningLevels).toBeNull()
    expect(target.reasoningLevelsSource).toBeNull()
    expect(target.detectReasoningLevels).toBe(true)
  })

  it('fills the fields the catalog states', () => {
    const target = model()

    applyCatalogEntryToModel(target, entry())

    expect(target.remoteModelId).toBe('deepseek-reasoner')
    expect(target.displayName).toBe('DeepSeek Reasoner')
    expect(target.supportsToolUse).toBe(true)
    expect(target.supportsStructuredOutput).toBe(true)
    expect(target.maxContextTokens).toBe('131072')
    expect(target.inputCostPer1MUsd).toBe('0.28')
    expect(target.cachedInputCostPer1MUsd).toBe('0.028')
  })

  // An unstated value is unknown, not zero; overwriting an operator's entry with a blank would lose their work.
  it('leaves a field alone when the catalog states nothing for it', () => {
    const target = model()
    target.maxContextTokens = '4096'
    target.outputCostPer1MUsd = '9'

    applyCatalogEntryToModel(target, entry({ maxContextTokens: null, outputCostPer1MUsd: null }))

    expect(target.maxContextTokens).toBe('4096')
    expect(target.outputCostPer1MUsd).toBe('9')
  })

  // The catalog source states no chat-versus-embedding discriminator, so the operator's choice stands.
  it('never changes the chosen workload', () => {
    const target = model()
    target.kind = 'embedding'

    applyCatalogEntryToModel(target, entry())

    expect(target.kind).toBe('embedding')
  })
})
