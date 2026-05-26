// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it } from 'vitest'
import { createRuntime } from '@/app/runtime/createRuntime'
import {
  getActiveRuntime,
  resetActiveRuntime,
  setActiveRuntime,
} from '@/app/runtime/runtimeContext'

describe('runtime mode selection', () => {
  beforeEach(() => {
    resetActiveRuntime()
  })

  it('creates mock and live runtime snapshots with stable defaults', () => {
    const mockRuntime = createRuntime({ mode: 'mock' })

    expect(mockRuntime.mode).toBe('mock')
    expect(mockRuntime.isMock).toBe(true)
    expect(mockRuntime.apiBaseUrl.length).toBeGreaterThan(0)

    expect(createRuntime({ mode: 'live', apiBaseUrl: '/backend' })).toMatchObject({
      mode: 'live',
      isMock: false,
      apiBaseUrl: '/backend',
    })
  })

  it('allows tests and bootstrapping code to override the active runtime', () => {
    const runtime = createRuntime({ mode: 'mock', apiBaseUrl: '/mock-api' })

    setActiveRuntime(runtime)

    expect(getActiveRuntime()).toMatchObject({
      mode: 'mock',
      apiBaseUrl: '/mock-api',
    })

    resetActiveRuntime()

    expect(getActiveRuntime()).toMatchObject({
      mode: 'live',
      apiBaseUrl: '/api',
    })
  })
})
