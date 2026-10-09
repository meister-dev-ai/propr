// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { getSupportedAuthenticationKind, getSupportedAuthenticationKinds } from '@/services/providerActivationService'

describe('Azure DevOps authentication choices', () => {
  it.each(['https://dev.azure.com', 'https://dev.azure.com/org', 'https://org.visualstudio.com'])('offers explicit PAT with OAuth first for %s', (host) => {
    expect(getSupportedAuthenticationKinds('azureDevOps', host)).toEqual(['oauthClientCredentials', 'personalAccessToken'])
    expect(getSupportedAuthenticationKind('azureDevOps')).toBe('oauthClientCredentials')
  })

  it('retains Server PAT and Windows authentication choices', () => {
    expect(getSupportedAuthenticationKinds('azureDevOps', 'https://ado-server.example.com/tfs')).toEqual(['personalAccessToken', 'windowsUserAccount'])
  })
})
