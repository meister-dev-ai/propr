// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { setupWorker } from 'msw/browser'
import { handlers } from './handlers'

export const worker = setupWorker(...handlers)

export async function startMockWorker(): Promise<void> {
  await worker.start({
    onUnhandledRequest: 'bypass',
    serviceWorker: { url: import.meta.env.BASE_URL + 'mockServiceWorker.js' },
  })

  // Ensure the service worker actually controls this page before the app bootstraps.
  // Otherwise the very first request (the session-restore /auth/refresh) can race ahead of
  // SW control, bypass MSW, and hit Vite's SPA fallback (200 HTML) instead of the mock.
  if (typeof navigator !== 'undefined' && navigator.serviceWorker && !navigator.serviceWorker.controller) {
    await new Promise<void>((resolve) => {
      const timeout = setTimeout(resolve, 1000)
      navigator.serviceWorker.addEventListener(
        'controllerchange',
        () => {
          clearTimeout(timeout)
          resolve()
        },
        { once: true },
      )
    })
  }
}
