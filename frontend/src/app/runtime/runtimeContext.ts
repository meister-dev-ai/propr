// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { inject, type App, type InjectionKey } from 'vue'
import { createRuntime, type Runtime } from './createRuntime'

export const runtimeInjectionKey: InjectionKey<Runtime> = Symbol('meisterpropr.runtime')

let activeRuntime: Runtime = createRuntime({ mode: 'live', apiBaseUrl: '/api' })

export function provideRuntime(app: App, runtime: Runtime): void {
  activeRuntime = runtime
  app.provide(runtimeInjectionKey, runtime)
}

export function setActiveRuntime(runtime: Runtime): void {
  activeRuntime = runtime
}

export function getActiveRuntime(): Runtime {
  return activeRuntime
}

export function resetActiveRuntime(): Runtime {
  activeRuntime = createRuntime({ mode: 'live', apiBaseUrl: '/api' })
  return activeRuntime
}

export function useRuntime(): Runtime {
  return inject(runtimeInjectionKey) ?? getActiveRuntime()
}
