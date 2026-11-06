import { createAdminClient } from '@/services/api'
import type { components } from '@/types'

export type ExtensionConsent = components['schemas']['ExtensionConsentDto']
export type ExtensionGrant = components['schemas']['ExtensionGrantDto']

export async function getExtensionConsent(request: string): Promise<ExtensionConsent> {
  const result = await createAdminClient().GET('/auth/extension/consent', { params: { query: { request } }, credentials: 'include' })
  if (!result.data) throw new Error('This sign-in request expired or no longer matches your website session. Start again from the extension.')
  return result.data
}

export async function decideExtensionConsent(request: string, approve: boolean): Promise<string> {
  const result = await createAdminClient().POST('/auth/extension/consent', { body: { request, approve }, credentials: 'include' })
  if (!result.data?.redirectUrl) throw new Error('The authorization could not be completed. Start again from the extension.')
  return result.data.redirectUrl
}

export async function listExtensionGrants(): Promise<ExtensionGrant[]> {
  const result = await createAdminClient().GET('/auth/extension/grants')
  if (!result.data) throw new Error('Connected applications could not be loaded.')
  return result.data
}

export async function revokeExtensionGrant(id: string): Promise<void> {
  const result = await createAdminClient().DELETE('/auth/extension/grants/{id}', { params: { path: { id } } })
  if (!result.response.ok) throw new Error('The application could not be disconnected.')
}
