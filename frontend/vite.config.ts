// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { loadEnv } from 'vite'
import vue from '@vitejs/plugin-vue'
import vuetify from 'vite-plugin-vuetify'
import path from 'node:path'
// defineConfig from vitest/config augments Vite's UserConfig with the `test` key.
import { defineConfig, configDefaults } from 'vitest/config'

function parseAllowedHosts(value?: string): string[] {
  return (value ?? '')
    .split(',')
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0)
}

export function rewriteApiProxyPath(requestPath: string): string {
  return requestPath.replace(/^\/api/, '')
}

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const allowedHosts = parseAllowedHosts(env.VITE_DEV_ALLOWED_HOSTS)

  return {
    plugins: [
      vue(),
      vuetify({ autoImport: true }),
    ],
    base: '/',
    resolve: {
      alias: {
        '@': path.resolve(__dirname, './src'),
      },
    },
    server: {
      port: 5173,
      allowedHosts: allowedHosts.length > 0 ? allowedHosts : undefined,
      proxy: {
        '/api': {
          target: 'http://localhost:8080',
          changeOrigin: true,
          rewrite: rewriteApiProxyPath,
        },
      },
    },
    build: {
      outDir: 'dist',
      target: 'esnext',
      rollupOptions: {
        output: {
          // Rolldown (Vite 8) requires manualChunks as a function; object form was removed.
          manualChunks: (id) => {
            if (/[\\/]node_modules[\\/](vue|vue-router)[\\/]/.test(id)) {
              return 'vue-core'
            }
          },
        },
      },
    },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['tests/setup.ts'],
      // Mounting a view costs more than exercising it, so per-test wall time tracks how many jsdom
      // environments are competing rather than how much work the test does. At the 5s default a test
      // that passes on its own times out when the suite runs alongside a build, and it is a different
      // test each time. Give the budget enough headroom that a timeout means a hang, not a busy machine.
      testTimeout: 15000,
      exclude: [...configDefaults.exclude, 'tests/e2e/**'],
      server: {
        deps: {
          inline: ['vuetify'],
        },
      },
      env: {
        VITE_API_BASE_URL: 'http://localhost/api',
      },
      coverage: {
        provider: 'v8',
        reporter: ['text', 'json', 'html'],
      },
    },
  }
})
