// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { vi } from 'vitest'

/** Supplies the browser viewport API used by Vuetify overlays in jsdom. */
export function installControlTestViewport(): void {
  vi.stubGlobal('visualViewport', Object.assign(new EventTarget(), {
    width: 1024,
    height: 768,
    offsetLeft: 0,
    offsetTop: 0,
    pageLeft: 0,
    pageTop: 0,
    scale: 1,
  }))
}
