// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import '@/assets/styles/index.css'
import { createApp } from 'vue'
import App from './app/App.vue'
import router from './app/router'
import { vuetify } from './app/plugins/vuetify'
import { createRuntime } from './app/runtime/createRuntime'
import { provideRuntime, setActiveRuntime } from './app/runtime/runtimeContext'
import { useSession, LOGOUT_BROADCAST_KEY } from '@/composables/useSession'
import { refreshAccessToken, setOnSessionExpired } from '@/services/api'

const app = createApp(App)
app.use(vuetify)

const runtime = createRuntime()
setActiveRuntime(runtime)
provideRuntime(app, runtime)

if (runtime.isMock) {
  try {
    const { startMockWorker } = await import('./mocks/browser')
    await startMockWorker()
  } catch (error) {
    // The mock service worker needs a secure context (HTTPS or localhost). On a plain-HTTP
    // LAN IP (e.g. a phone hitting the dev server) registration fails — don't let that
    // white-screen the app; mount anyway so the failure is visible instead of blank.
    console.error('Mock service worker failed to start; continuing without it.', error)
  }
}

const { setAccessToken, loadClientRoles, clearTokens } = useSession()

// When a refresh fails terminally (session expired/revoked), clear state and go to login.
setOnSessionExpired(() => {
  clearTokens()
  if (router.currentRoute.value.name !== 'login') {
    void router.replace({ name: 'login' })
  }
})

// Another tab logged out → tear down this tab's session too.
window.addEventListener('storage', (event) => {
  if (event.key === LOGOUT_BROADCAST_KEY && event.newValue) {
    clearTokens()
    if (router.currentRoute.value.name !== 'login') {
      void router.replace({ name: 'login' })
    }
  }
})

// Restore the session from the httpOnly refresh cookie (shared across tabs, survives reload)
// BEFORE installing the router, so its initial navigation sees the authenticated state and
// doesn't bounce a valid session to /login.
const accessToken = await refreshAccessToken()
if (accessToken) {
  setAccessToken(accessToken)
  await loadClientRoles()
}

app.use(router)
app.mount('#app')
