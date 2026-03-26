import '@/assets/styles/globals.css'
import { createApp } from 'vue'
import App from './App.vue'
import router from './router'

const app = createApp(App)
app.use(router)

async function prepareApp() {
  if (import.meta.env.VITE_MOCK === 'true') {
    const { worker } = await import('./mocks/browser')
    await worker.start({
      onUnhandledRequest: 'bypass', serviceWorker: { url: import.meta.env.BASE_URL + 'mockServiceWorker.js' }
    })
  }

  app.mount('#app')
}

prepareApp()

