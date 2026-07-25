import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import TenantModelCatalogSection from '../TenantModelCatalogSection.vue'
import type { AiModelCatalogOverrideDto } from '@/services/modelCatalogService'

const listTenantOverrides = vi.fn()
const listTenantProviders = vi.fn()
const listTenantModels = vi.fn()
const upsertTenantOverride = vi.fn()
const deleteTenantOverride = vi.fn()
const defineTenantModel = vi.fn()

vi.mock('@/services/modelCatalogService', () => ({
  listTenantOverrides: (...a: unknown[]) => listTenantOverrides(...a),
  listTenantProviders: (...a: unknown[]) => listTenantProviders(...a),
  listTenantModels: (...a: unknown[]) => listTenantModels(...a),
  upsertTenantOverride: (...a: unknown[]) => upsertTenantOverride(...a),
  deleteTenantOverride: (...a: unknown[]) => deleteTenantOverride(...a),
  defineTenantModel: (...a: unknown[]) => defineTenantModel(...a),
}))

const override = (o: Partial<AiModelCatalogOverrideDto> = {}): AiModelCatalogOverrideDto =>
  ({
    providerId: 'deepseek',
    remoteModelId: 'deepseek-reasoner',
    displayName: null,
    inputCostPer1MUsd: 0.1,
    outputCostPer1MUsd: null,
    cachedInputCostPer1MUsd: null,
    cacheWriteCostPer1MUsd: null,
    ...o,
  }) as AiModelCatalogOverrideDto

const section = () => mount(TenantModelCatalogSection, { props: { tenantId: 't1' } })

describe('TenantModelCatalogSection', () => {
  beforeEach(() => {
    for (const fn of [
      listTenantOverrides,
      listTenantProviders,
      listTenantModels,
      upsertTenantOverride,
      deleteTenantOverride,
      defineTenantModel,
    ]) {
      fn.mockReset()
    }
    listTenantOverrides.mockResolvedValue([])
    listTenantProviders.mockResolvedValue([{ providerId: 'deepseek', providerName: 'DeepSeek', modelCount: 1 }])
    listTenantModels.mockResolvedValue([
      { providerId: 'deepseek', remoteModelId: 'deepseek-reasoner', displayName: 'DeepSeek Reasoner', pricingLayer: 'global' },
    ])
    upsertTenantOverride.mockResolvedValue(undefined)
    deleteTenantOverride.mockResolvedValue(undefined)
    defineTenantModel.mockResolvedValue(undefined)
  })

  it('says every model is at list price when there are no overrides', async () => {
    const wrapper = section()
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-override-empty"]').text()).toContain('list price')
  })

  // The distinction the whole feature turns on: an unset price is inherited, not free.
  it('shows an unset price as inherited rather than as zero', async () => {
    listTenantOverrides.mockResolvedValue([override({ inputCostPer1MUsd: 0.1, outputCostPer1MUsd: null })])
    const wrapper = section()
    await flushPromises()

    const row = wrapper.get('[data-testid="tenant-override-table"] tbody tr').text()
    expect(row).toContain('$0.1')
    expect(row).toContain('inherited')
    expect(row).not.toContain('$0\n')
  })

  it('seeds a draft from a catalog pick so only the negotiated prices are typed', async () => {
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()
    await wrapper.get('.catalog-entry').trigger('click')

    const form = wrapper.get('[data-testid="tenant-override-form"]')
    expect(form.text()).toContain('deepseek-reasoner')
    // The prices start empty: the operator states what they negotiated, not the whole specification.
    expect((wrapper.get('[data-testid="override-inputCostPer1MUsd"]').element as HTMLInputElement).value).toBe('')
  })

  it('omits an empty price so the server reads it as inherit', async () => {
    const wrapper = section()
    await flushPromises()
    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()
    await wrapper.get('.catalog-entry').trigger('click')

    await wrapper.get('[data-testid="override-inputCostPer1MUsd"]').setValue('0.2')
    await wrapper.get('[data-testid="override-save"]').trigger('submit')
    await flushPromises()

    expect(upsertTenantOverride).toHaveBeenCalledWith('t1', {
      providerId: 'deepseek',
      remoteModelId: 'deepseek-reasoner',
      displayName: undefined,
      inputCostPer1MUsd: 0.2,
      outputCostPer1MUsd: undefined,
      cachedInputCostPer1MUsd: undefined,
      cacheWriteCostPer1MUsd: undefined,
    })
  })

  // A negative rate would invert a cost cap, so it is refused before it reaches the server too.
  it('refuses a negative price without calling the server', async () => {
    const wrapper = section()
    await flushPromises()
    await wrapper.get('[data-testid="catalog-picker-open"]').trigger('click')
    await flushPromises()
    await wrapper.get('.catalog-entry').trigger('click')

    await wrapper.get('[data-testid="override-inputCostPer1MUsd"]').setValue('-1')
    await wrapper.get('[data-testid="override-save"]').trigger('submit')
    await flushPromises()

    expect(upsertTenantOverride).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="tenant-catalog-error"]').text()).toContain('cannot be negative')
  })

  it('resets an override back to list pricing', async () => {
    listTenantOverrides.mockResolvedValue([override()])
    const wrapper = section()
    await flushPromises()

    await wrapper.get('.btn-danger').trigger('click')
    await flushPromises()

    expect(deleteTenantOverride).toHaveBeenCalledWith('t1', 'deepseek', 'deepseek-reasoner')
  })

  // A model the catalog has never described is the case #144 exists for: a private fine-tune, a newer release,
  // or a self-hosted model.
  it('defines a model the catalog does not list', async () => {
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="define-model-open"]').trigger('click')
    await wrapper.get('[data-testid="define-provider"]').setValue('deepseek')
    await wrapper.get('[data-testid="define-model-id"]').setValue('my-finetune-v2')
    await wrapper.get('[data-testid="define-input-cost"]').setValue('1.5')
    await wrapper.get('[data-testid="define-save"]').trigger('submit')
    await flushPromises()

    expect(defineTenantModel).toHaveBeenCalledWith(
      't1',
      expect.objectContaining({
        providerId: 'deepseek',
        remoteModelId: 'my-finetune-v2',
        inputCostPer1MUsd: 1.5,
        // An unstated value stays absent rather than becoming zero.
        outputCostPer1MUsd: undefined,
      }),
    )
  })

  it('requires a provider and model id before defining', async () => {
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="define-model-open"]').trigger('click')
    await wrapper.get('[data-testid="define-save"]').trigger('submit')
    await flushPromises()

    expect(defineTenantModel).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="tenant-catalog-error"]').text()).toContain('required')
  })

  // The server refuses a model the catalog already lists and names the right instrument, so that message must
  // reach the operator rather than being replaced by a generic failure.
  it('surfaces the servers reason when the catalog already describes the model', async () => {
    defineTenantModel.mockRejectedValue(
      new Error("The catalog already describes 'x'. Record a pricing override for it instead."),
    )
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="define-model-open"]').trigger('click')
    await wrapper.get('[data-testid="define-provider"]').setValue('deepseek')
    await wrapper.get('[data-testid="define-model-id"]').setValue('deepseek-chat')
    await wrapper.get('[data-testid="define-save"]').trigger('submit')
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-catalog-error"]').text()).toContain('pricing override')
  })

  it('reports a load failure', async () => {
    listTenantOverrides.mockRejectedValue(new Error('boom'))
    const wrapper = section()
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-catalog-error"]').text()).toContain('could not be loaded')
  })
})
