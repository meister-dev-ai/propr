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

function readStringField(value: unknown): string | null {
  return typeof value === 'string' && value ? value : null
}

function readFirstFieldError(errors: unknown): string | null {
  if (!errors || typeof errors !== 'object') {
    return null
  }

  return Object.values(errors as Record<string, string[]>).flat()[0] ?? null
}

function extractApiErrorMessage(error: unknown): string | null {
  if (!error || typeof error !== 'object') {
    return null
  }

  const apiError = error as {
    error?: string
    detail?: string
    title?: string
    errors?: Record<string, string[]>
  }

  return (
    readStringField(apiError.error) ??
    readStringField(apiError.detail) ??
    readStringField(apiError.title) ??
    readFirstFieldError(apiError.errors)
  )
}

function getErrorMessage(error: unknown, fallback: string): string {
  return sanitizeSensitiveText(extractApiErrorMessage(error) ?? fallback)
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
