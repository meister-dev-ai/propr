// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { readSignInCallbackFragment, takeSignInCallbackFragment } from '@/features/auth/signInCallbackFragment'

describe('readSignInCallbackFragment', () => {
  it('reads the session of a successful sign-in', () => {
    expect(readSignInCallbackFragment('#accessToken=at-1&expiresIn=900&tokenType=Bearer')).toEqual({
      session: { accessToken: 'at-1', expiresIn: 900, tokenType: 'Bearer' },
      error: null,
      message: null,
      pendingLink: false,
    })
  })

  it('reads a fragment without its leading number sign', () => {
    expect(readSignInCallbackFragment('accessToken=at-1').session).toEqual({
      accessToken: 'at-1',
      expiresIn: undefined,
      tokenType: undefined,
    })
  })

  it('reads the code and the message of a refused sign-in', () => {
    expect(readSignInCallbackFragment('#error=invalid_state&message=Start%20again.')).toEqual({
      session: null,
      error: 'invalid_state',
      message: 'Start again.',
      pendingLink: false,
    })
  })

  it('reads a sign-in that waits for the confirmation of its link', () => {
    expect(readSignInCallbackFragment('#link=pending')).toEqual({
      session: null,
      error: null,
      message: null,
      pendingLink: true,
    })
  })

  it('takes the fragment of the current page and removes it from the address, keeping path and query', () => {
    window.history.replaceState(null, '', '/login/callback?view=1#accessToken=at-1&expiresIn=900')

    const fragment = takeSignInCallbackFragment()

    expect(fragment.session?.accessToken).toBe('at-1')
    expect(window.location.hash).toBe('')
    expect(window.location.pathname).toBe('/login/callback')
    expect(window.location.search).toBe('?view=1')
    expect(window.location.href).not.toContain('at-1')
  })
})
