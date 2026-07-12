// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { AiProtocolMode, AiPurpose } from '@/services/aiConnectionsService'

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
}

export type EditableBinding = {
  id: string | null
  purpose: AiPurpose
  configuredModelId: string
  protocolMode: AiProtocolMode
  isEnabled: boolean
}
