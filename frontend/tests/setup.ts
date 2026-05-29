// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { vi, beforeEach, afterEach } from 'vitest'
import { config } from '@vue/test-utils'
import { createVuetify } from 'vuetify'
import * as vuetifyComponents from 'vuetify/components'
import * as vuetifyDirectives from 'vuetify/directives'

const vuetify = createVuetify({
  components: vuetifyComponents,
  directives: vuetifyDirectives,
})

if (typeof window !== 'undefined') {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  })
}

class ResizeObserverStub {
  observe = vi.fn()
  unobserve = vi.fn()
  disconnect = vi.fn()
}
;(globalThis as unknown as { ResizeObserver: typeof ResizeObserverStub }).ResizeObserver = ResizeObserverStub

// Mock global fetch
global.fetch = vi.fn()

// Mock sessionStorage
const sessionStorageStore: Record<string, string> = {}
const sessionStorageMock = {
  getItem: vi.fn((key: string) => sessionStorageStore[key] ?? null),
  setItem: vi.fn((key: string, value: string) => { sessionStorageStore[key] = value }),
  removeItem: vi.fn((key: string) => { delete sessionStorageStore[key] }),
  clear: vi.fn(() => { Object.keys(sessionStorageStore).forEach(k => delete sessionStorageStore[k]) }),
  length: 0,
  key: vi.fn(),
}
Object.defineProperty(global, 'sessionStorage', { value: sessionStorageMock, writable: true })

// Mock localStorage
const localStorageStore: Record<string, string> = {}
const localStorageMock = {
  getItem: vi.fn((key: string) => localStorageStore[key] ?? null),
  setItem: vi.fn((key: string, value: string) => { localStorageStore[key] = value }),
  removeItem: vi.fn((key: string) => { delete localStorageStore[key] }),
  clear: vi.fn(() => { Object.keys(localStorageStore).forEach(k => delete localStorageStore[k]) }),
  length: 0,
  key: vi.fn(),
}
Object.defineProperty(global, 'localStorage', { value: localStorageMock, writable: true })

// Reset mocks between tests
beforeEach(() => {
  vi.clearAllMocks()
  sessionStorageMock.clear()
  localStorageMock.clear()
})

// Vue Test Utils global config
config.global.mocks = {
  $route: { params: {}, query: {}, name: 'clients' },
  $router: { push: vi.fn(), replace: vi.fn(), back: vi.fn() },
}

// Stub router components so tests don't need a real router plugin
config.global.stubs = {
  RouterLink: { template: '<a><slot /></a>' },
  RouterView: { template: '<div />' },
}

// Register Vuetify globally so component tests exercise the production plugin
config.global.plugins = [...(config.global.plugins ?? []), vuetify]
