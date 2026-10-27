// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/** The session a sign-in through an identity provider hands to the frontend. */
export interface SignInCallbackSession {
  accessToken: string
  expiresIn?: number
  tokenType?: string
}

/**
 * The outcome of a sign-in through an identity provider, as the API writes it into the URL fragment of the
 * frontend page the sign-in returns to. The refresh token is not part of it: the API sets it as an httpOnly
 * cookie.
 */
export interface SignInCallbackFragment {
  /** The session of a successful sign-in, or null for a refused one. */
  session: SignInCallbackSession | null
  /** The stable code of a refused sign-in. */
  error: string | null
  /** What the person signing in reads about a refused sign-in. */
  message: string | null
}

function toOptionalNumber(value: string | null): number | undefined {
  if (!value) return undefined
  const parsed = Number.parseInt(value, 10)
  return Number.isFinite(parsed) ? parsed : undefined
}

/** Reads the outcome of a sign-in from a URL fragment, with or without its leading number sign. */
export function readSignInCallbackFragment(hash: string): SignInCallbackFragment {
  const fragment = new URLSearchParams(hash.startsWith('#') ? hash.slice(1) : hash)
  const accessToken = fragment.get('accessToken')

  return {
    session: accessToken
      ? {
          accessToken,
          expiresIn: toOptionalNumber(fragment.get('expiresIn')),
          tokenType: fragment.get('tokenType') ?? undefined,
        }
      : null,
    error: fragment.get('error'),
    message: fragment.get('message'),
  }
}

/**
 * Reads the outcome of a sign-in from the URL fragment of the current page, and removes the fragment from the address
 * bar and from the history entry at once, keeping the path and the query. The access token then exists in memory
 * only, whatever happens in the asynchronous work after this call.
 */
export function takeSignInCallbackFragment(): SignInCallbackFragment {
  if (typeof window === 'undefined') {
    return readSignInCallbackFragment('')
  }

  const fragment = readSignInCallbackFragment(window.location.hash)
  if (window.location.hash) {
    window.history.replaceState(window.history.state, '', `${window.location.pathname}${window.location.search}`)
  }

  return fragment
}
