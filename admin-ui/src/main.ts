// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import '@/assets/styles/globals.css'
import { createApp } from 'vue'
import App from './App.vue'
import router from './router'
import { useSession } from '@/composables/useSession'

const app = createApp(App)
app.use(router)

async function prepareApp() {
  if (import.meta.env.VITE_MOCK === 'true') {
    const { worker } = await import('./mocks/browser')
    await worker.start({
      onUnhandledRequest: 'bypass', serviceWorker: { url: import.meta.env.BASE_URL + 'mockServiceWorker.js' }
    })
  }

  const { getAccessToken, loadClientRoles } = useSession()
  if (getAccessToken()) {
    await loadClientRoles()
  }

  app.mount('#app')
}

prepareApp()

