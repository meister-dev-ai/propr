import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import ModelCatalogPicker from '../ModelCatalogPicker.vue'
import { applyCatalogEntryToModel } from '../aiConnectionsFormatters'
import type { EditableModel } from '../aiConnectionsForm.types'
import type { AiModelCatalogEntryDto } from '@/services/modelCatalogService'

const listProviders = vi.fn()
const listModels = vi.fn()

vi.mock('@/services/modelCatalogService', () => ({
  listProviders: (...args: unknown[]) => listProviders(...args),
  listModels: (...args: unknown[]) => listModels(...args),
}))

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
    const wrapper = mount(ModelCatalogPicker, { props: { clientId: 'c1' } })

    // Browsing is opt-in: an operator typing a model id by hand must not pay for a catalog fetch.
    expect(listProviders).not.toHaveBeenCalled()

    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()

    expect(listProviders).toHaveBeenCalledWith('c1')
    expect(wrapper.find('[data-testid="catalog-list"]').exists()).toBe(true)
  })

  it('emits the chosen entry rather than mutating anything itself', async () => {
    const wrapper = mount(ModelCatalogPicker, { props: { clientId: 'c1' } })
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
    const wrapper = mount(ModelCatalogPicker, { props: { clientId: 'c1' } })
    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()

    await wrapper.get('[data-testid="catalog-search"]').setValue('reasoner')

    expect(wrapper.findAll('.catalog-entry')).toHaveLength(1)
  })

  it('marks a negotiated rate as such', async () => {
    listModels.mockResolvedValue([entry({ pricingLayer: 'tenantOverride', inputCostPer1MUsd: 0.1 })])
    const wrapper = mount(ModelCatalogPicker, { props: { clientId: 'c1' } })
    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('negotiated')
  })

  // A catalog that cannot be reached must not block the operator: hand-entry is still available, and the
  // message says so.
  it('reports a load failure without hiding the manual route', async () => {
    listProviders.mockRejectedValue(new Error('boom'))
    const wrapper = mount(ModelCatalogPicker, { props: { clientId: 'c1' } })

    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="catalog-error"]').text()).toContain('could not be loaded')
  })

  it('says an empty result still allows hand-entry', async () => {
    listModels.mockResolvedValue([])
    const wrapper = mount(ModelCatalogPicker, { props: { clientId: 'c1' } })
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
