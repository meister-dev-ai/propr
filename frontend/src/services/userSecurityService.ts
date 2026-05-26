// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient } from '@/services/api'
import type { components } from '@/types'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type { RuntimeMode } from '@/app/runtime/createRuntime'
import { sanitizeSensitiveText } from '@/services/credentialSafety'

export type ChangePasswordRequest = components['schemas']['ChangePasswordRequest']

export class ApiRequestError extends Error {
  status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'ApiRequestError'
    this.status = status
  }
}

function getErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const apiError = error as {
      error?: string
      detail?: string
      title?: string
      errors?: Record<string, string[]>
    }

    if (typeof apiError.error === 'string' && apiError.error) {
      return sanitizeSensitiveText(apiError.error)
    }

    if (typeof apiError.detail === 'string' && apiError.detail) {
      return sanitizeSensitiveText(apiError.detail)
    }

    if (typeof apiError.title === 'string' && apiError.title) {
      return sanitizeSensitiveText(apiError.title)
    }

    if (apiError.errors && typeof apiError.errors === 'object') {
      const firstError = Object.values(apiError.errors).flat()[0]
      if (firstError) {
        return sanitizeSensitiveText(firstError)
      }
    }
  }

  return sanitizeSensitiveText(fallback)
}

async function changeMyPasswordInternal(request: ChangePasswordRequest): Promise<void> {
  const { error, response } = await createAdminClient({ baseUrl: getActiveRuntime().apiBaseUrl }).POST('/users/me/password', {
    body: request,
  })

  if (!response.ok) {
    throw new ApiRequestError(getErrorMessage(error, 'Failed to change password.'), response.status)
  }
}

export interface UserSecurityService {
  runtimeMode: RuntimeMode
  changeMyPassword: (request: ChangePasswordRequest) => Promise<void>
}

function createUserSecurityService(runtimeMode: RuntimeMode): UserSecurityService {
  return {
    runtimeMode,
    changeMyPassword: changeMyPasswordInternal,
  }
}

const liveUserSecurityService = createUserSecurityService('live')
const mockUserSecurityService = createUserSecurityService('mock')

export function resolveUserSecurityService(): UserSecurityService {
  return getActiveRuntime().mode === 'mock'
    ? mockUserSecurityService
    : liveUserSecurityService
}

export async function changeMyPassword(request: ChangePasswordRequest): Promise<void> {
  return resolveUserSecurityService().changeMyPassword(request)
}
