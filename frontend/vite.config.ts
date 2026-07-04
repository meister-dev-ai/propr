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
