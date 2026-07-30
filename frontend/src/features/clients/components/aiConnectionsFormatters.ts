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

/**
 * Every provider family the system can name, with its label. This is the LABEL CATALOGUE, not the offer list:
 * which families a given client may actually pick comes from the server, because a family can be named here
 * before this build has a driver for it. Rendering a label for such a family still matters — a profile carrying
 * one has to read as itself rather than as "Unknown".
 */
export const providerOptions: Array<{ value: AiProviderKind; label: string }> = [
  { value: 'azureOpenAi', label: 'Azure OpenAI / AI Foundry' },
  { value: 'openAi', label: 'OpenAI (non-Azure)' },
  { value: 'liteLlm', label: 'LiteLLM' },
  { value: 'openAiCompatible', label: 'OpenAI-compatible (custom base URL)' },
  { value: 'anthropic', label: 'Anthropic (native)' },
  { value: 'awsBedrock', label: 'AWS Bedrock' },
  { value: 'googleVertex', label: 'Google Gemini / Vertex AI' },
]

// Sections group the purpose rows in the editor so the flat list stays readable as purposes grow.
export type PurposeSection = 'generation' | 'support' | 'memory' | 'insights'

export const purposeSectionOrder: PurposeSection[] = ['generation', 'support', 'memory', 'insights']

export const purposeSectionLabels: Record<PurposeSection, string> = {
  generation: 'Review generation',
  support: 'Review support',
  memory: 'Memory & embeddings',
  insights: 'Code Insights',
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
  { value: 'insightsClassification', label: 'Insights Classification', description: 'Classifies collected findings for quality analytics. Falls back to Review Triage when unset.', defaultEnabled: false, section: 'insights' },
]

export const protocolOptionLabels: Record<AiProtocolMode, string> = {
  auto: 'Automatic',
  responses: 'Responses',
  chatCompletions: 'Chat Completions',
  embeddings: 'Embeddings',
  anthropicMessages: 'Anthropic Messages',
  bedrockConverse: 'Bedrock Converse',
  googleGenerateContent: 'Google generateContent',
}

export const enabledBindings = (profile: AiConnectionDto) => (profile.purposeBindings ?? []).filter((binding) => binding.isEnabled)

/**
 * What to tell an operator about the two fields whose correct value differs most between provider families.
 * A Bedrock access key and an Anthropic key are both "the secret", but pasted into the wrong shape one of them
 * fails with a signing error that reads like a permissions problem — so the form says which shape it wants.
 */
export interface ProviderGuidance {
  namePlaceholder: string
  baseUrlPlaceholder: string
  baseUrlHint: string
  credentialHint: string
  /** A query parameter this family cannot work without, so the form can stop presenting it as optional. */
  requiredQueryParam: string
  queryParamPlaceholder: string
}

const defaultGuidance: ProviderGuidance = {
  namePlaceholder: 'OpenAI (prod)',
  baseUrlPlaceholder: 'https://api.openai.com/v1',
  baseUrlHint: 'Azure-hosted endpoints, including Azure AI Foundry OpenAI endpoints, belong under Azure OpenAI / AI Foundry.',
  credentialHint: '',
  requiredQueryParam: '',
  queryParamPlaceholder: 'api-version=2024-10-21',
}

const guidanceByProvider: Partial<Record<AiProviderKind, ProviderGuidance>> = {
  azureOpenAi: {
    ...defaultGuidance,
    namePlaceholder: 'Azure OpenAI (prod)',
    baseUrlPlaceholder: 'https://your-resource.openai.azure.com/',
    baseUrlHint: 'The Azure AI resource endpoint, not a deployment URL.',
  },
  openAiCompatible: {
    ...defaultGuidance,
    namePlaceholder: 'DeepSeek via opencode Zen',
    baseUrlPlaceholder: 'https://opencode.ai/zen/v1',
    baseUrlHint: 'Whatever serves an OpenAI-compatible /chat/completions at this URL, vendor or self-hosted.',
  },
  liteLlm: {
    ...defaultGuidance,
    namePlaceholder: 'LiteLLM gateway',
    baseUrlPlaceholder: 'https://gateway.example.com/v1',
    baseUrlHint: 'The gateway URL; models are named as the gateway exposes them.',
  },
  anthropic: {
    ...defaultGuidance,
    namePlaceholder: 'Claude (native)',
    baseUrlPlaceholder: 'https://api.anthropic.com/v1',
    baseUrlHint: 'Any host that speaks the Messages API works, including a gateway in front of it.',
    credentialHint: 'Sent as the x-api-key header, which is what Anthropic reads.',
  },
  awsBedrock: {
    ...defaultGuidance,
    namePlaceholder: 'Bedrock (eu-central-1)',
    baseUrlPlaceholder: 'https://bedrock-runtime.eu-central-1.amazonaws.com',
    baseUrlHint: 'The host names the region inference runs in, which is what pins where the data goes.',
    credentialHint: 'Store the access key as accessKeyId:secretAccessKey, adding :sessionToken for temporary credentials.',
    queryParamPlaceholder: 'region=eu-central-1',
  },
  googleVertex: {
    ...defaultGuidance,
    namePlaceholder: 'Gemini on Vertex (europe-west4)',
    baseUrlPlaceholder: 'https://europe-west4-aiplatform.googleapis.com',
    baseUrlHint:
      'A Vertex host names the location it serves. For the Gemini API use '
      + 'https://generativelanguage.googleapis.com instead.',
    credentialHint: 'Vertex takes the JSON key of a service account; the Gemini API takes a plain API key.',
    requiredQueryParam: 'project',
    queryParamPlaceholder: 'project=your-gcp-project',
  },
}

export const providerGuidance = (providerKind: AiProviderKind | undefined): ProviderGuidance =>
  (providerKind && guidanceByProvider[providerKind]) || defaultGuidance

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
