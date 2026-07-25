import { describe, expect, it } from 'vitest'

import { authOptionsForProvider, providerLabel, providerOptions } from '../aiConnectionsFormatters'

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
})
