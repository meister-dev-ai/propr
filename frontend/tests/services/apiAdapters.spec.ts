// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, describe, expect, it } from 'vitest'
import { createRuntime } from '@/app/runtime/createRuntime'
import { resetActiveRuntime, setActiveRuntime } from '@/app/runtime/runtimeContext'
import { resolveAuthOptionsService } from '@/services/authOptionsService'
import { resolveTenantAuthService } from '@/services/tenantAuthService'
import { resolveUserSecurityService } from '@/services/userSecurityService'
import { resolveJobsService } from '@/services/jobsService'
import { resolveProviderConnectionsService } from '@/services/providerConnectionsService'
import { resolveClientTokenUsageService } from '@/services/clientTokenUsageService'
import { resolveProCursorService } from '@/services/proCursorService'

describe('runtime-selected API adapters', () => {
  afterEach(() => {
    resetActiveRuntime()
  })

  it('resolves mock adapters when the active runtime is mock', () => {
    setActiveRuntime(createRuntime({ mode: 'mock' }))

    expect(resolveAuthOptionsService().runtimeMode).toBe('mock')
    expect(resolveTenantAuthService().runtimeMode).toBe('mock')
    expect(resolveUserSecurityService().runtimeMode).toBe('mock')
    expect(resolveJobsService().runtimeMode).toBe('mock')
    expect(resolveProviderConnectionsService().runtimeMode).toBe('mock')
    expect(resolveClientTokenUsageService().runtimeMode).toBe('mock')
    expect(resolveProCursorService().runtimeMode).toBe('mock')
  })

  it('resolves live adapters when the active runtime is live', () => {
    setActiveRuntime(createRuntime({ mode: 'live' }))

    expect(resolveAuthOptionsService().runtimeMode).toBe('live')
    expect(resolveTenantAuthService().runtimeMode).toBe('live')
    expect(resolveUserSecurityService().runtimeMode).toBe('live')
    expect(resolveJobsService().runtimeMode).toBe('live')
    expect(resolveProviderConnectionsService().runtimeMode).toBe('live')
    expect(resolveClientTokenUsageService().runtimeMode).toBe('live')
    expect(resolveProCursorService().runtimeMode).toBe('live')
  })
})
