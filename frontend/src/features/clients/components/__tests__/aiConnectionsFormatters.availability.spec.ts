// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'

import type { AiConnectionDto } from '@/services/aiConnectionsService'
import { isConnectionUnavailable, unavailableReasonText, unavailableRemedyText } from '../aiConnectionsFormatters'

const profileWith = (availability: AiConnectionDto['availability']): AiConnectionDto =>
  ({ id: 'p1', displayName: 'Profile', providerKind: 'azureOpenAi', availability }) as AiConnectionDto

describe('whether a stored profile can serve a review', () => {
  // The provider field is enum-typed, so a profile stored against a family this build cannot name reports
  // another family there. Reading the state the server sent is what keeps such a profile from reading as sound.
  it('reads the state the server sent rather than the family the profile names', () => {
    expect(
      isConnectionUnavailable(
        profileWith({ state: 'unavailable', reason: 'providerFamilyAbsent', providerIdentity: 'ContosoLlm', unresolvedValues: [] }),
      ),
    ).toBe(true)
    expect(isConnectionUnavailable(profileWith({ state: 'available', unresolvedValues: [] }))).toBe(false)
    expect(isConnectionUnavailable(profileWith(undefined))).toBe(false)
  })
})

describe('what an operator is told about an unavailable profile', () => {
  it('names the stored identity an absent family has to be installed under', () => {
    const availability = {
      state: 'unavailable' as const,
      reason: 'providerFamilyAbsent' as const,
      providerIdentity: 'ContosoLlm',
      unresolvedValues: [],
    }

    expect(unavailableReasonText(availability)).toContain('ContosoLlm')
    expect(unavailableRemedyText(availability)).toContain('ContosoLlm')
    expect(unavailableRemedyText(availability)).toContain('Install')
  })

  // The two family reasons take different actions, so the sentences differ: one is a host install, the other an
  // allow-list edit. One shared sentence would send half the operators to the wrong screen.
  it('sends an allow-list refusal to the allow-list and an absent family to the host', () => {
    const absent = {
      state: 'unavailable' as const,
      reason: 'providerFamilyAbsent' as const,
      providerIdentity: 'ContosoLlm',
      unresolvedValues: [],
    }
    const refused = { ...absent, reason: 'providerFamilyNotPermitted' as const }

    expect(unavailableRemedyText(absent)).not.toBe(unavailableRemedyText(refused))
    expect(unavailableRemedyText(refused)).toContain('allow-list')
    expect(unavailableRemedyText(refused)).toContain('ContosoLlm')
    expect(unavailableReasonText(refused)).toContain('not permitted')
  })

  // A refused endpoint and a refused family are two allow-lists, so the remedy has to name the right one.
  it('sends an endpoint refusal to the endpoint allow-list', () => {
    const refused = {
      state: 'unavailable' as const,
      reason: 'endpointNotPermitted' as const,
      providerIdentity: null,
      unresolvedValues: [],
    }

    expect(unavailableReasonText(refused)).toContain('endpoint')
    expect(unavailableRemedyText(refused)).toContain('permitted endpoint list')
  })

  it('names every stored value it could not read, and the setting each one belongs to', () => {
    const availability = {
      state: 'unavailable' as const,
      reason: 'storedValueUnresolved' as const,
      providerIdentity: null,
      unresolvedValues: [
        { field: 'authMode' as const, value: 'MutualTls' },
        { field: 'protocolMode' as const, value: 'Grpc' },
      ],
    }

    const reason = unavailableReasonText(availability)

    expect(reason).toContain('MutualTls')
    expect(reason).toContain('Grpc')
    expect(reason).toContain('Authentication mode')
    expect(unavailableRemedyText(availability)).toContain('Edit the profile')
  })

  it('says nothing about a profile that has nothing standing in its way', () => {
    expect(unavailableReasonText(undefined)).toBe('')
    expect(unavailableRemedyText(undefined)).toBe('')
  })
})
