import { describe, expect, it } from 'vitest'

import { authOptionsForProvider, providerGuidance, providerLabel, providerOptions } from '../aiConnectionsFormatters'

describe('provider options', () => {
  it('offers the OpenAI-compatible custom base URL profile', () => {
    // The profile an operator picks to reach the compatible long tail: vendor APIs, aggregators, self-hosted.
    expect(providerOptions.map((option) => option.value)).toContain('openAiCompatible')
  })

  it('labels every offered provider, so none renders as Unknown', () => {
    for (const option of providerOptions) {
      expect(providerLabel(option.value)).toBe(option.label)
      expect(providerLabel(option.value)).not.toBe('Unknown')
    }
  })

  it('keeps the OpenAI-compatible profile on API-key auth only', () => {
    // Azure identity is specific to Azure-hosted endpoints; an arbitrary compatible server authenticates
    // with a key.
    expect(authOptionsForProvider('openAiCompatible').map((option) => option.value)).toEqual(['apiKey'])
  })

  it('tells a Bedrock operator the two things the form cannot infer', () => {
    // An access key pasted as one string signs nothing, and a URL without a region says nothing about where
    // the inference runs — both fail later as something that reads like a permissions problem.
    const guidance = providerGuidance('awsBedrock')

    expect(guidance.credentialHint).toContain('accessKeyId:secretAccessKey')
    expect(guidance.baseUrlPlaceholder).toContain('bedrock-runtime')
    expect(guidance.baseUrlHint).toContain('region')
  })

  it('falls back to the generic guidance for a family with nothing special to say', () => {
    expect(providerGuidance('liteLlm').baseUrlPlaceholder).toBe(providerGuidance(undefined).baseUrlPlaceholder)
  })
})
