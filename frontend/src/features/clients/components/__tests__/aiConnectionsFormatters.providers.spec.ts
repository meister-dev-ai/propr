// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'

import type { AiAuthMode } from '@/services/aiConnectionsService'
import { modeLabel, modeOptions, neutralGuidance, offeredModeOptions, providerGuidance } from '../aiConnectionsFormatters'

describe('the options a form renders from what the server described', () => {
  it('offers the shapes it is given, in the order it is given them', () => {
    // Which shapes a family offers, and what they are called, is the server's answer. An order of its own
    // would move the default the form lands on away from the one the server named first.
    expect(
      modeOptions<AiAuthMode>([
        { value: 'apiKey', label: 'API Key' },
        { value: 'sigV4', label: 'AWS Signature v4' },
      ]),
    ).toEqual([
      { value: 'apiKey', label: 'API Key' },
      { value: 'sigV4', label: 'AWS Signature v4' },
    ])
  })

  it('shows a value the server did not name as itself', () => {
    // The stored identity, which an operator matches against an install or an allow-list. A family or
    // a shape supplied by an add-in this build never saw must not read as a blank or as "Unknown".
    const options = modeOptions<AiAuthMode>([{ value: 'apiKey', label: '' }])

    expect(options).toEqual([{ value: 'apiKey', label: 'apiKey' }])
    expect(modeLabel(options, 'apiKey')).toBe('apiKey')
  })

  it('shows a value no option describes as itself', () => {
    expect(modeLabel<AiAuthMode>([{ value: 'apiKey', label: 'API Key' }], 'sigV4')).toBe('sigV4')
  })

  it('reports no value at all as unknown', () => {
    expect(modeLabel<AiAuthMode>([], undefined)).toBe('Unknown')
  })

  // A shape the family still reads but no longer offers belongs to the profiles that already hold it, so it is
  // left out of the offer unless it is the one selected.
  it('leaves out a superseded value, and keeps the one already selected', () => {
    const reported = [
      { value: 'apiKey' as AiAuthMode, label: 'API Key', isSuperseded: true },
      { value: 'sigV4' as AiAuthMode, label: 'AWS Signature v4' },
    ]

    expect(offeredModeOptions(reported, null).map((option) => option.value)).toEqual(['sigV4'])
    expect(offeredModeOptions(reported, 'sigV4' as AiAuthMode).map((option) => option.value)).toEqual(['sigV4'])
    expect(offeredModeOptions(reported, 'apiKey' as AiAuthMode).map((option) => option.value)).toEqual(['apiKey', 'sigV4'])
  })
})

describe('what the form says about the connection boxes', () => {
  it('shows what the family says about them', () => {
    // The base URL is where families differ most: a resource endpoint on one, a regional host on another, and
    // the wrong one fails with a provider error that names neither.
    const guidance = providerGuidance({
      namePlaceholder: 'Bedrock (eu-central-1)',
      baseUrlPlaceholder: 'https://bedrock-runtime.eu-central-1.amazonaws.com',
      baseUrlHint: 'The host names the region inference runs in, and that pins where the data goes.',
      queryParamPlaceholder: 'region=eu-central-1',
    })

    expect(guidance.baseUrlPlaceholder).toContain('bedrock-runtime')
    expect(guidance.baseUrlHint).toContain('region')
    expect(guidance.queryParamPlaceholder).toBe('region=eu-central-1')
  })

  it('names the parameter a family cannot work without', () => {
    // The form uses this to stop presenting a required setting inside a section labelled optional.
    expect(providerGuidance({ requiredQueryParam: 'project' }).requiredQueryParam).toBe('project')
  })

  it('claims no required parameter for a family that states none', () => {
    expect(providerGuidance({ baseUrlHint: 'Anything that speaks the protocol.' }).requiredQueryParam).toBe('')
  })

  it('falls back to family-neutral text for a family that says nothing', () => {
    // Not to another family's example address: showing one family's endpoint under another is worse than
    // showing none, because it reads as the address this connection wants.
    expect(providerGuidance(null)).toEqual(neutralGuidance)
    expect(providerGuidance({ namePlaceholder: 'Whatever (prod)' }).baseUrlPlaceholder).toBe('')
  })
})
