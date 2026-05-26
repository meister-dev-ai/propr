// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import '@/assets/styles/globals.css'
import '@/assets/styles/theme.css'
import { createApp } from 'vue'
import App from './app/App.vue'
import router from './app/router'
import { vuetify } from './app/plugins/vuetify'
import { createRuntime } from './app/runtime/createRuntime'
import { provideRuntime, setActiveRuntime } from './app/runtime/runtimeContext'
import { useSession } from '@/composables/useSession'

const app = createApp(App)
app.use(router)
app.use(vuetify)

const runtime = createRuntime()
setActiveRuntime(runtime)
provideRuntime(app, runtime)

async function prepareApp() {
  if (runtime.isMock) {
    const { startMockWorker } = await import('./mocks/browser')
    await startMockWorker()
  }

  const { getAccessToken, loadClientRoles } = useSession()
  if (getAccessToken()) {
    await loadClientRoles()
  }

  app.mount('#app')
}

prepareApp()
