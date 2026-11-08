// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import type { LogicalModelResponse } from '@/services/logicalModelsService'
import type { EditableModel } from '../aiConnectionsForm.types'
import {
  configuredModelLevelChoice,
  logicalModelLevelChoice,
  logicalModelLevelLabel,
  sendableReasoningLevel,
  setReasoningLevels,
  withDefaultReasoningLevel,
} from '../aiConnectionsFormatters'

describe('sendableReasoningLevel', () => {
  it('sends a level the model accepts, and any level while its levels are unknown', () => {
    expect(sendableReasoningLevel('high', ['low', 'high'])).toBe('high')
    expect(sendableReasoningLevel('max', null)).toBe('max')
  })

  it('sends the nearest lower level of the shared order that the model accepts', () => {
    expect(sendableReasoningLevel('xhigh', ['none', 'low', 'high'])).toBe('high')
    expect(sendableReasoningLevel('medium', ['none', 'low', 'high'])).toBe('low')
  })

  it('sends no level when the model accepts no lower level, or for a name outside the shared order', () => {
    expect(sendableReasoningLevel('low', ['high'])).toBeNull()
    expect(sendableReasoningLevel('ultra', ['low', 'high'])).toBeNull()
    expect(sendableReasoningLevel(null, ['low'])).toBeNull()
  })
})

describe('setReasoningLevels', () => {
  const model = (defaultReasoningLevel: string | null): EditableModel => ({
    localId: 'l1',
    existingId: null,
    remoteModelId: 'gpt-6.1-terra',
    displayName: '',
    kind: 'chat',
    tokenizerName: '',
    maxInputTokens: '',
    maxContextTokens: '',
    embeddingDimensions: '',
    supportsStructuredOutput: true,
    supportsToolUse: true,
    inputCostPer1MUsd: '',
    outputCostPer1MUsd: '',
    cachedInputCostPer1MUsd: '',
    cacheWriteCostPer1MUsd: '',
    supportsReasoning: false,
    supportsPromptCaching: false,
    reasoningContentField: '',
    defaultReasoningLevel,
    reasoningLevels: ['low', 'medium', 'high'],
    reasoningLevelsSource: 'operator',
    detectReasoningLevels: false,
  })

  it('keeps the default level while the new levels hold it', () => {
    const target = model('medium')

    setReasoningLevels(target, ['medium', 'high'])

    expect(target.reasoningLevels).toEqual(['medium', 'high'])
    expect(target.defaultReasoningLevel).toBe('medium')
  })

  it('clears the default level when its level is removed', () => {
    const target = model('high')

    setReasoningLevels(target, ['low', 'medium'])

    expect(target.defaultReasoningLevel).toBeNull()
  })

  // The save adds the default level to the detected levels, so detecting the levels again keeps it.
  it('keeps the default level when the levels are to be detected again', () => {
    const target = model('high')

    setReasoningLevels(target, null)

    expect(target.reasoningLevels).toBeNull()
    expect(target.defaultReasoningLevel).toBe('high')
  })
})

// An operator who chose the default level stated that the model accepts it, so the server adds the default level to
// detected levels that lack it. The form does the same, so a discovery does not clear a default level the save keeps.
describe('withDefaultReasoningLevel', () => {
  it('adds the default level that the detected levels lack and sorts the list by the shared order', () => {
    expect(withDefaultReasoningLevel(['low', 'medium'], 'high')).toEqual(['low', 'medium', 'high'])
    expect(withDefaultReasoningLevel(['low', 'max'], 'none')).toEqual(['none', 'low', 'max'])
  })

  it('appends the default level to levels outside the shared order', () => {
    expect(withDefaultReasoningLevel(['fast', 'deep'], 'high')).toEqual(['fast', 'deep', 'high'])
  })

  it('gives the default level to a model whose detected levels are empty', () => {
    expect(withDefaultReasoningLevel([], 'high')).toEqual(['high'])
  })

  // The result is a new list, so the form does not share the list of the discovery response.
  it('keeps detected levels that hold the default level, and the levels of a model without a default level', () => {
    const detected = ['high', 'low']

    expect(withDefaultReasoningLevel(detected, 'low')).toEqual(['high', 'low'])
    expect(withDefaultReasoningLevel(detected, 'low')).not.toBe(detected)
    expect(withDefaultReasoningLevel(['low', 'high'], null)).toEqual(['low', 'high'])
  })
})

describe('reasoning level choices', () => {
  const referencedModel = { availability: 'available', reasoningLevels: ['low', 'high'], defaultReasoningLevel: 'low' }

  it('takes the levels and the default level of a configured model', () => {
    expect(configuredModelLevelChoice({ reasoningLevels: ['none', 'max'], defaultReasoningLevel: 'none' })).toEqual({
      levels: ['none', 'max'],
      inheritedLevel: 'none',
      inheritedFrom: "the model's default level",
    })
    expect(configuredModelLevelChoice(undefined)).toEqual({ levels: null, inheritedLevel: null, inheritedFrom: "the model's default level" })
  })

  it('inherits the level of the logical model before the default level of its model', () => {
    const logicalModel = { name: 'deep', reasoningLevel: 'high', referencedModel } as LogicalModelResponse

    expect(logicalModelLevelChoice(logicalModel)).toEqual({
      levels: ['low', 'high'],
      inheritedLevel: 'high',
      inheritedFrom: 'the logical model',
    })
  })

  it('inherits the default level of the model behind a logical model without a level', () => {
    const logicalModel = { name: 'deep', reasoningLevel: null, referencedModel } as LogicalModelResponse

    expect(logicalModelLevelChoice(logicalModel)).toEqual({
      levels: ['low', 'high'],
      inheritedLevel: 'low',
      inheritedFrom: "the model's default level",
    })
  })

  it('labels the level of a logical model in a table', () => {
    expect(logicalModelLevelLabel({ capability: 'chat', reasoningLevel: 'xhigh' })).toBe('xhigh')
    expect(logicalModelLevelLabel({ capability: 'chat', reasoningLevel: null })).toBe('Inherit')
    expect(logicalModelLevelLabel({ capability: 'embedding', reasoningLevel: null })).toBe('Not applicable')
  })
})
