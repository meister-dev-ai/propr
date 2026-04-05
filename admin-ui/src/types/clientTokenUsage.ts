// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

export interface ClientTokenUsageSample {
  connectionCategory?: number
  modelId: string
  date: string // ISO date string YYYY-MM-DD
  inputTokens: number
  outputTokens: number
}

export interface ClientTokenUsageResponse {
  clientId: string
  from: string // ISO date string YYYY-MM-DD
  to: string // ISO date string YYYY-MM-DD
  totalInputTokens: number
  totalOutputTokens: number
  samples: ClientTokenUsageSample[]
}
