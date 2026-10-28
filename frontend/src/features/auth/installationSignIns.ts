// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import type { AuthOptions } from '@/services/authOptionsService'

/**
 * Whether the installation offers the password sign-in. Before the sign-in options are loaded, and when they cannot be
 * loaded, the page offers the password sign-in and the API decides.
 */
export function offersPasswordSignIn(options: AuthOptions | null | undefined): boolean {
  return options?.availableSignInMethods?.includes('password') ?? true
}

/**
 * Whether the installation turns off the sign-in through the identity providers of tenants: the license grants single
 * sign-on, and the installation does not offer the method `sso`.
 */
export function turnsOffTenantSignIn(options: AuthOptions | null | undefined): boolean {
  const isLicensed = options?.capabilities?.find((capability) => capability.key === 'sso-authentication')?.isAvailable === true
  return isLicensed && options?.availableSignInMethods?.includes('sso') !== true
}
