// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

const configuredApiBase = import.meta.env.VITE_API_BASE_URL?.trim()

export const API_BASE_URL = configuredApiBase && configuredApiBase.length > 0
  ? configuredApiBase.replace(/\/+$/, '')
  : '/api'
