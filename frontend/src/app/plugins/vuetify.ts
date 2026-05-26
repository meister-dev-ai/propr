// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import 'vuetify/styles'
import '@mdi/font/css/materialdesignicons.css'
import { createVuetify } from 'vuetify'
import { aliases, mdi } from 'vuetify/iconsets/mdi'

export const vuetify = createVuetify({
  theme: {
    defaultTheme: 'meisterDark',
    themes: {
      meisterDark: {
        dark: true,
        colors: {
          background: '#0f1116',
          surface: '#161a22',
          primary: '#5b8def',
          secondary: '#7c8aa8',
          accent: '#9cdcfe',
          error: '#e06c75',
          info: '#56b6c2',
          success: '#98c379',
          warning: '#e5c07b',
        },
      },
    },
  },
  icons: {
    defaultSet: 'mdi',
    aliases,
    sets: { mdi },
  },
  defaults: {
    global: {
      rounded: 'lg',
    },
    VBtn: {
      variant: 'flat',
      rounded: 'pill',
      color: 'primary',
    },
    VCard: { variant: 'flat', rounded: 'lg' },
    VTextField: { variant: 'outlined', density: 'comfortable' },
    VTextarea: { variant: 'outlined', density: 'comfortable' },
    VSelect: { variant: 'outlined', density: 'comfortable' },
    VAutocomplete: { variant: 'outlined', density: 'comfortable' },
    VCombobox: { variant: 'outlined', density: 'comfortable' },
    VAlert: { variant: 'tonal', density: 'comfortable' },
    VChip: { rounded: 'pill', size: 'small' },
  },
})
