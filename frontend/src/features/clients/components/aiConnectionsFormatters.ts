// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type {
  AiConnectionAvailabilityDto,
  AiConnectionDto,
  AiConnectionVocabularyField,
  AiProtocolMode,
  AiProviderConnectionFormDto,
  AiPurpose,
  AiVerificationStatus,
} from '@/services/aiConnectionsService'
import type { EditableBinding, EditableModel } from './aiConnectionsForm.types'
import type { AiModelCatalogEntryDto } from '@/services/modelCatalogService'

// Label, option and parse helpers for the AI-connections form.
//
// No provider family is named here. The family list, its labels, its credential and protocol modes and its
// connection-box text all come from the permitted-providers endpoint. An add-in installed after this build
// shipped would otherwise render as a bare key.

/** One value a picker offers, as the server described it. */
export interface ModeOption<TValue extends string> {
  value: TValue
  label: string
}

/**
 * Converts the server's vocabulary entries for one axis into picker options.
 *
 * An entry with no label falls back to its value. Operators match that stored value against an install or an
 * allow-list, so it is more useful than a placeholder.
 */
export const modeOptions = <TValue extends string>(
  reported: ReadonlyArray<{ value?: TValue | null; label?: string | null }> | null | undefined,
): Array<ModeOption<TValue>> =>
  (reported ?? [])
    .filter((entry): entry is { value: TValue; label?: string | null } => Boolean(entry.value))
    .map((entry) => ({ value: entry.value, label: entry.label || entry.value }))

/**
 * Picker options for one vocabulary axis. Superseded entries are dropped, except one already selected.
 *
 * A superseded entry is still read by the family but is no longer offered for a new connection. Keeping the
 * selected one means a stored profile opens on the value it holds. Dropping it would open that profile on a
 * different value, and saving would overwrite a credential the operator never touched.
 */
export const offeredModeOptions = <TValue extends string>(
  reported:
    | ReadonlyArray<{ value?: TValue | null; label?: string | null; isSuperseded?: boolean | null }>
    | null
    | undefined,
  selected: TValue | null | undefined,
): Array<ModeOption<TValue>> =>
  modeOptions((reported ?? []).filter((entry) => !entry.isSuperseded || entry.value === selected))

/** Returns the label for one vocabulary value, from the options the server described. */
export const modeLabel = <TValue extends string>(
  options: ReadonlyArray<ModeOption<TValue>>,
  value: TValue | null | undefined,
): string => (value ? options.find((option) => option.value === value)?.label ?? value : 'Unknown')

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

export const enabledBindings = (profile: AiConnectionDto) => (profile.purposeBindings ?? []).filter((binding) => binding.isEnabled)

/**
 * Placeholder and hint text for the three connection boxes the host keeps for every family: display name,
 * base URL and default query parameters.
 *
 * The base URL needs it most. The same box takes a resource endpoint on one family and a regional host on
 * another, and the wrong one fails with a provider error naming neither. Credential boxes are excluded. A
 * family declares those fields with its own labels and hints.
 */
export interface ProviderGuidance {
  namePlaceholder: string
  baseUrlPlaceholder: string
  baseUrlHint: string
  /** A query parameter this family requires, so the form does not present it as optional. */
  requiredQueryParam: string
  queryParamPlaceholder: string
}

/**
 * Fallback text for a box the family says nothing about. It names no family and gives no example address. An
 * example written for one family is misleading under another. The query-parameter placeholder describes the
 * shape of an entry. That shape is the same for every family.
 */
export const neutralGuidance: ProviderGuidance = {
  namePlaceholder: '',
  baseUrlPlaceholder: '',
  baseUrlHint: '',
  requiredQueryParam: '',
  queryParamPlaceholder: 'name=value',
}

/** Merges the selected family's box text over the family-neutral fallback. */
export const providerGuidance = (
  connectionForm: AiProviderConnectionFormDto | null | undefined,
): ProviderGuidance => ({
  namePlaceholder: connectionForm?.namePlaceholder || neutralGuidance.namePlaceholder,
  baseUrlPlaceholder: connectionForm?.baseUrlPlaceholder || neutralGuidance.baseUrlPlaceholder,
  baseUrlHint: connectionForm?.baseUrlHint || neutralGuidance.baseUrlHint,
  requiredQueryParam: connectionForm?.requiredQueryParam || neutralGuidance.requiredQueryParam,
  queryParamPlaceholder: connectionForm?.queryParamPlaceholder || neutralGuidance.queryParamPlaceholder,
})

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

/**
 * Reports whether the server marked the stored profile unusable.
 *
 * Read from the profile, not derived from its provider family. A family this build cannot name has no value in
 * the enum-typed `providerKind`, so that field reports a different family and says nothing about the profile.
 */
export const isConnectionUnavailable = (profile: AiConnectionDto): boolean =>
  profile.availability?.state === 'unavailable'

/** Label for each stored vocabulary position, so a reason line names the setting an operator edits. */
const vocabularyFieldLabels: Record<AiConnectionVocabularyField, string> = {
  authMode: 'Authentication mode',
  discoveryMode: 'Discovery mode',
  operationKind: 'Model workload',
  protocolMode: 'Protocol mode',
  configuredModelSource: 'Model source',
  purpose: 'Purpose',
  verificationStatus: 'Verification status',
  verificationFailureCategory: 'Verification failure category',
}

const unresolvedValueText = (availability: AiConnectionAvailabilityDto): string =>
  (availability.unresolvedValues ?? [])
    .map((unresolved) => {
      const field = unresolved.field ? vocabularyFieldLabels[unresolved.field] ?? unresolved.field : 'Stored value'
      return `${field} “${unresolved.value ?? ''}”`
    })
    .join(', ')

const identityText = (availability: AiConnectionAvailabilityDto): string =>
  availability.providerIdentity || 'unnamed'

/**
 * The short line under an unavailable profile, naming the value behind the state. The provider identity is
 * quoted as stored. Operators match that value against an install or an allow-list.
 */
export const unavailableReasonText = (availability: AiConnectionAvailabilityDto | undefined): string => {
  if (!availability) {
    return ''
  }

  switch (availability.reason) {
    case 'providerFamilyAbsent':
      return `Provider family “${identityText(availability)}” is not installed on this host.`
    case 'providerFamilyNotPermitted':
      return `Provider family “${identityText(availability)}” is not permitted for this tenant.`
    case 'endpointNotPermitted':
      return "This profile's endpoint is not on the tenant's permitted endpoint list."
    case 'storedValueUnresolved':
      return `This build cannot read ${unresolvedValueText(availability) || 'a value stored on this profile'}.`
    default:
      return 'This profile cannot be used as it is stored.'
  }
}

/**
 * The remedy shown under an unavailable profile. Each reason is separate because the actions differ. An absent
 * family is installed on the host. A refused family is added to the tenant's family allow-list, and a refused
 * endpoint to its endpoint allow-list.
 */
export const unavailableRemedyText = (availability: AiConnectionAvailabilityDto | undefined): string => {
  if (!availability) {
    return ''
  }

  switch (availability.reason) {
    case 'providerFamilyAbsent':
      return `Install the “${identityText(availability)}” provider family on this host, or point this profile at a family that is installed.`
    case 'providerFamilyNotPermitted':
      return `Add “${identityText(availability)}” to the tenant's provider allow-list, or point this profile at a permitted family.`
    case 'endpointNotPermitted':
      return "Add the endpoint to the tenant's permitted endpoint list, or point this profile at a permitted one."
    case 'storedValueUnresolved':
      return 'Edit the profile and choose a value this build offers.'
    default:
      return 'Edit the profile to correct what it is stored with.'
  }
}

export const purposeLabel = (purpose: AiPurpose | undefined) => purposeOptions.find((option) => option.value === purpose)?.label ?? 'Unknown purpose'
export const purposeDescription = (purpose: AiPurpose | undefined) => purposeOptions.find((option) => option.value === purpose)?.description ?? ''

/**
 * The protocol mode that leaves the format to the driver. Host-reserved, so it carries no family key and every
 * family serves it. A binding holds this value when it has no preference.
 */
export const autoProtocolMode: AiProtocolMode = 'Auto'

/** The host-reserved protocol mode for an embedding call, written the same way. */
export const embeddingsProtocolMode: AiProtocolMode = 'Embeddings'

export const makeBindingDefaults = (): EditableBinding[] => purposeOptions.map((option) => ({
  id: null,
  purpose: option.value,
  configuredModelId: '',
  protocolMode: option.value === 'embeddingDefault' ? embeddingsProtocolMode : autoProtocolMode,
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
