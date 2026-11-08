// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { AiProtocolMode, AiPurpose, AiReasoningLevelSource } from '@/services/aiConnectionsService'

export type ModelKind = 'chat' | 'embedding'

export type EditableModel = {
  localId: string
  existingId: string | null
  remoteModelId: string
  displayName: string
  kind: ModelKind
  tokenizerName: string
  maxInputTokens: string
  maxContextTokens: string
  embeddingDimensions: string
  supportsStructuredOutput: boolean
  supportsToolUse: boolean
  inputCostPer1MUsd: string
  outputCostPer1MUsd: string
  cachedInputCostPer1MUsd: string
  cacheWriteCostPer1MUsd: string
  supportsReasoning: boolean
  supportsPromptCaching: boolean
  /** Field this model needs echoed back to preserve its chain of thought; empty when it has no such requirement. */
  reasoningContentField: string
  /**
   * The level ProPR sends to this model for a call that no other place chooses a level for, or null for the
   * provider's own default. It is one of the reasoning levels while those are known.
   */
  defaultReasoningLevel: string | null
  /**
   * The reasoning levels the model accepts, from the lowest to the highest effort. Null while no source knows them;
   * the save then fills them from the provider family and the model catalog.
   */
  reasoningLevels: string[] | null
  /** Where the levels come from; 'operator' once an operator edits them. */
  reasoningLevelsSource: AiReasoningLevelSource | null
  /** Whether the save detects the levels again and replaces the current ones. */
  detectReasoningLevels: boolean
}

export type EditableBinding = {
  id: string | null
  purpose: AiPurpose
  configuredModelId: string
  protocolMode: AiProtocolMode
  isEnabled: boolean
}
