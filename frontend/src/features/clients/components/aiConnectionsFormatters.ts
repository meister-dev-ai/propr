// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type {
  AiAuthMode,
  AiConnectionDto,
  AiProtocolMode,
  AiProviderKind,
  AiPurpose,
  AiVerificationStatus,
} from '@/services/aiConnectionsService'
import type { EditableBinding, EditableModel } from './aiConnectionsForm.types'
import type { AiModelCatalogEntryDto } from '@/services/modelCatalogService'

// Static option tables and pure label/parse helpers for the AI-connections form.
// Extracted from ClientAiConnectionsTab.vue so the component holds only state.

export const providerOptions: Array<{ value: AiProviderKind; label: string }> = [
  { value: 'azureOpenAi', label: 'Azure OpenAI / AI Foundry' },
  { value: 'openAi', label: 'OpenAI (non-Azure)' },
  { value: 'liteLlm', label: 'LiteLLM' },
  { value: 'openAiCompatible', label: 'OpenAI-compatible (custom base URL)' },
]

// Sections group the purpose rows in the editor so the flat list stays readable as purposes grow.
export type PurposeSection = 'generation' | 'support' | 'memory'

export const purposeSectionOrder: PurposeSection[] = ['generation', 'support', 'memory']

export const purposeSectionLabels: Record<PurposeSection, string> = {
  generation: 'Review generation',
  support: 'Review support',
  memory: 'Memory & embeddings',
}

export const purposeOptions: Array<{ value: AiPurpose; label: string; description: string; defaultEnabled: boolean; section: PurposeSection }> = [
  { value: 'reviewDefault', label: 'Review Default', description: 'Primary review generation and mentions.', defaultEnabled: true, section: 'generation' },
  { value: 'reviewLowEffort', label: 'Review Low Effort', description: 'Low-complexity file review.', defaultEnabled: true, section: 'generation' },
  { value: 'reviewMediumEffort', label: 'Review Medium Effort', description: 'Medium-complexity file review.', defaultEnabled: true, section: 'generation' },
  { value: 'reviewHighEffort', label: 'Review High Effort', description: 'High-complexity review and synthesis.', defaultEnabled: true, section: 'generation' },
  { value: 'proRvPrefilter', label: 'ProRV Prefilter', description: 'Optional focused-review guidance prefilter.', defaultEnabled: false, section: 'support' },
  { value: 'reviewTriage', label: 'Review Triage', description: 'Cheap per-file complexity triage. Falls back to Review Low Effort when unset.', defaultEnabled: false, section: 'support' },
  { value: 'reviewVerification', label: 'Review Verification', description: 'Evidence-gathering verification of candidate findings. Falls back to Review Triage when unset.', defaultEnabled: false, section: 'support' },
  { value: 'memoryReconsideration', label: 'Memory Reconsideration', description: 'Thread-memory reconsideration calls.', defaultEnabled: true, section: 'memory' },
  { value: 'embeddingDefault', label: 'Embedding Default', description: 'Embedding generation for memory and ProCursor.', defaultEnabled: true, section: 'memory' },
]

export const protocolOptionLabels: Record<AiProtocolMode, string> = {
  auto: 'Automatic',
  responses: 'Responses',
  chatCompletions: 'Chat Completions',
  embeddings: 'Embeddings',
}

export const enabledBindings = (profile: AiConnectionDto) => (profile.purposeBindings ?? []).filter((binding) => binding.isEnabled)

export const authOptionsForProvider = (providerKind: AiProviderKind): Array<{ value: AiAuthMode; label: string }> => {
  return providerKind === 'azureOpenAi'
    ? [
        { value: 'apiKey', label: 'API Key' },
        { value: 'azureIdentity', label: 'Azure Identity' },
      ]
    : [{ value: 'apiKey', label: 'API Key' }]
}

export const protocolOptions = (purpose: AiPurpose): Array<{ value: AiProtocolMode; label: string }> => {
  if (purpose === 'embeddingDefault') {
    return [
      { value: 'auto', label: protocolOptionLabels.auto },
      { value: 'embeddings', label: protocolOptionLabels.embeddings },
    ]
  }

  return [
    { value: 'auto', label: protocolOptionLabels.auto },
    { value: 'responses', label: protocolOptionLabels.responses },
    { value: 'chatCompletions', label: protocolOptionLabels.chatCompletions },
  ]
}

export const providerLabel = (providerKind: AiProviderKind | undefined) => providerOptions.find((option) => option.value === providerKind)?.label ?? 'Unknown'

export const authModeLabel = (authMode: AiAuthMode | undefined) => {
  switch (authMode) {
    case 'azureIdentity':
      return 'Azure Identity'
    case 'apiKey':
      return 'API Key'
    default:
      return 'Unknown'
  }
}

export const verificationLabel = (status: AiVerificationStatus | undefined) => {
  switch (status) {
    case 'verified':
      return 'Verified'
    case 'failed':
      return 'Verification Failed'
    default:
      return 'Not Verified'
  }
}

const verificationChipModifier = (status: AiVerificationStatus | undefined): string => {
  switch (status) {
    case 'verified':
      return 'chip-success'
    case 'failed':
      return 'chip-danger'
    default:
      return 'chip-muted'
  }
}

export const verificationChipClass = (status: AiVerificationStatus | undefined) => [
  'chip',
  'chip-sm',
  verificationChipModifier(status),
]

export const purposeLabel = (purpose: AiPurpose | undefined) => purposeOptions.find((option) => option.value === purpose)?.label ?? 'Unknown purpose'
export const purposeDescription = (purpose: AiPurpose | undefined) => purposeOptions.find((option) => option.value === purpose)?.description ?? ''

export const makeBindingDefaults = (): EditableBinding[] => purposeOptions.map((option) => ({
  id: null,
  purpose: option.value,
  configuredModelId: '',
  protocolMode: option.value === 'embeddingDefault' ? 'embeddings' : 'auto',
  isEnabled: option.defaultEnabled,
}))

export const parseMapText = (value: string): Record<string, string> | undefined => {
  const parsedEntries: Record<string, string> = {}

  for (const rawLine of value.split('\n')) {
    const line = rawLine.trim()
    if (!line) {
      continue
    }

    const separatorIndex = line.indexOf('=')
    const key = separatorIndex >= 0 ? line.slice(0, separatorIndex).trim() : line.trim()
    const entryValue = separatorIndex >= 0 ? line.slice(separatorIndex + 1).trim() : ''

    if (!key || !entryValue) {
      continue
    }

    parsedEntries[key] = entryValue
  }

  return Object.keys(parsedEntries).length > 0 ? parsedEntries : undefined
}

export const serializeMap = (map: Record<string, string> | null | undefined) =>
  Object.entries(map ?? {})
    .map(([key, value]) => `${key}=${value}`)
    .join('\n')

/**
 * Copies a catalog entry's facts onto a model form. Kept as a pure function so the mapping is testable and so
 * the picker never writes the form itself: the configured model stays the authority for what is actually used,
 * and an operator remains free to correct anything the catalog supplied.
 *
 * Only fields the form already has are filled. The workload is left alone, because the catalog source states no
 * chat-versus-embedding discriminator and guessing one would be worse than leaving the operator's choice.
 */
export const applyCatalogEntryToModel = (model: EditableModel, entry: AiModelCatalogEntryDto): void => {
  model.remoteModelId = entry.remoteModelId ?? model.remoteModelId
  model.displayName = entry.displayName ?? model.displayName
  model.supportsToolUse = entry.supportsToolUse ?? model.supportsToolUse
  model.supportsStructuredOutput = entry.supportsStructuredOutput ?? model.supportsStructuredOutput
  model.maxContextTokens = numberToField(entry.maxContextTokens, model.maxContextTokens)
  model.inputCostPer1MUsd = numberToField(entry.inputCostPer1MUsd, model.inputCostPer1MUsd)
  model.outputCostPer1MUsd = numberToField(entry.outputCostPer1MUsd, model.outputCostPer1MUsd)
  model.cachedInputCostPer1MUsd = numberToField(entry.cachedInputCostPer1MUsd, model.cachedInputCostPer1MUsd)
  model.cacheWriteCostPer1MUsd = numberToField(entry.cacheWriteCostPer1MUsd, model.cacheWriteCostPer1MUsd)
  model.supportsReasoning = entry.supportsReasoning ?? model.supportsReasoning
  model.supportsPromptCaching = entry.supportsPromptCaching ?? model.supportsPromptCaching
  // The quirk the normalizing stage acts on. A model that does not declare one must not keep a stale value from
  // whatever was previously selected.
  model.reasoningContentField = entry.reasoningContentField ?? ''
}

/** A value the catalog does not state leaves the existing entry alone: unknown is not zero. */
const numberToField = (value: number | null | undefined, current: string): string =>
  typeof value === 'number' ? String(value) : current
