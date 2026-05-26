// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

export type RuntimeMode = 'mock' | 'live'

export interface RuntimeOptions {
  /**
   * Override the resolved runtime mode. When undefined the factory inspects
   * `import.meta.env.VITE_MOCK` (mock when 'true', live otherwise).
   */
  mode?: RuntimeMode
  /**
   * Base URL used by live adapters. Defaults to `import.meta.env.VITE_API_BASE_URL`
   * or `/api` when no environment variable is set.
   */
  apiBaseUrl?: string
}

export interface Runtime {
  mode: RuntimeMode
  apiBaseUrl: string
  /**
   * True when mock handlers (MSW) are expected to intercept network calls before they leave the browser.
   */
  isMock: boolean
}

function resolveMode(explicit?: RuntimeMode): RuntimeMode {
  if (explicit) return explicit
  return import.meta.env.VITE_MOCK === 'true' ? 'mock' : 'live'
}

function resolveApiBaseUrl(explicit?: string): string {
  if (explicit && explicit.length > 0) return explicit
  const fromEnv = import.meta.env.VITE_API_BASE_URL as string | undefined
  return fromEnv && fromEnv.length > 0 ? fromEnv : '/api'
}

export function createRuntime(options: RuntimeOptions = {}): Runtime {
  const mode = resolveMode(options.mode)
  return {
    mode,
    apiBaseUrl: resolveApiBaseUrl(options.apiBaseUrl),
    isMock: mode === 'mock',
  }
}
