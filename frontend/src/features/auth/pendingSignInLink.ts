// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * The person chose to link a waiting tenant sign-in to an existing account and has to sign in to that account first.
 * The choice survives the sign-in, which can pass through the page of an identity provider, in the session storage of
 * the tab. The start page then opens the confirmation of the link instead.
 */
const PENDING_LINK_CONFIRMATION_KEY = 'meisterpropr_pending_sign_in_link_confirmation'

/** Remembers that the next sign-in in this tab continues with the confirmation of the waiting link. */
export function rememberPendingLinkConfirmation(): void {
  try {
    sessionStorage.setItem(PENDING_LINK_CONFIRMATION_KEY, 'true')
  } catch {
    // Without session storage, the person opens the confirmation from the settings instead.
  }
}

/** Returns whether a confirmation of a waiting link was remembered, and forgets it. */
export function takePendingLinkConfirmation(): boolean {
  try {
    const remembered = sessionStorage.getItem(PENDING_LINK_CONFIRMATION_KEY) === 'true'
    sessionStorage.removeItem(PENDING_LINK_CONFIRMATION_KEY)
    return remembered
  } catch {
    return false
  }
}
