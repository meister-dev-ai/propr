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
import type { EditableBinding } from './aiConnectionsForm.types'

// Static option tables and pure label/parse helpers for the AI-connections form.
// Extracted from ClientAiConnectionsTab.vue so the component holds only state.

export const providerOptions: Array<{ value: AiProviderKind; label: string }> = [
  { value: 'azureOpenAi', label: 'Azure OpenAI / AI Foundry' },
  { value: 'openAi', label: 'OpenAI (non-Azure)' },
  { value: 'liteLlm', label: 'LiteLLM' },
]

export const purposeOptions: Array<{ value: AiPurpose; label: string; description: string; defaultEnabled: boolean }> = [
  { value: 'reviewDefault', label: 'Review Default', description: 'Primary review generation and mentions.', defaultEnabled: true },
  { value: 'proRvPrefilter', label: 'ProRV Prefilter', description: 'Optional focused-review guidance prefilter.', defaultEnabled: false },
  { value: 'reviewLowEffort', label: 'Review Low Effort', description: 'Low-complexity file review.', defaultEnabled: true },
  { value: 'reviewMediumEffort', label: 'Review Medium Effort', description: 'Medium-complexity file review.', defaultEnabled: true },
  { value: 'reviewHighEffort', label: 'Review High Effort', description: 'High-complexity review and synthesis.', defaultEnabled: true },
  { value: 'memoryReconsideration', label: 'Memory Reconsideration', description: 'Thread-memory reconsideration calls.', defaultEnabled: true },
  { value: 'embeddingDefault', label: 'Embedding Default', description: 'Embedding generation for memory and ProCursor.', defaultEnabled: true },
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

export const authModeLabel = (authMode: AiAuthMode | undefined) =>
  authMode === 'azureIdentity' ? 'Azure Identity' : authMode === 'apiKey' ? 'API Key' : 'Unknown'

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

export const verificationChipClass = (status: AiVerificationStatus | undefined) => [
  'chip',
  'chip-sm',
  status === 'verified' ? 'chip-success' : status === 'failed' ? 'chip-danger' : 'chip-muted',
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
