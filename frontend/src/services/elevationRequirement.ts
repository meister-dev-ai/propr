// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * The refusal of content that needs an elevation. A platform administrator reads the content of a tenant only during
 * an elevation that covers the client, and a content endpoint answers 403 with the code `elevation_required`
 * otherwise. The answer names the tenant and the client, so a view can offer to start an elevation for them.
 */

/** The tenant and the client an elevation has to cover before the refused content can be read. */
export interface ElevationRequirement {
  tenantId: string
  clientId: string
}

/** The error code of a content read that needs an elevation. */
export const ELEVATION_REQUIRED_CODE = 'elevation_required'

/** Thrown by a content read that the server refused because no elevation covers the client. */
export class ElevationRequiredError extends Error {
  readonly requirement: ElevationRequirement

  constructor(requirement: ElevationRequirement) {
    super('This content belongs to a tenant. Start an elevation for the client to read it.')
    this.name = 'ElevationRequiredError'
    this.requirement = requirement
  }
}

/**
 * Returns the tenant and the client named by a refusal with the code `elevation_required`, or null for any other
 * answer.
 */
export function readElevationRequired(body: unknown): ElevationRequirement | null {
  if (!body || typeof body !== 'object') {
    return null
  }

  const candidate = body as { code?: unknown; tenantId?: unknown; clientId?: unknown }
  return candidate.code === ELEVATION_REQUIRED_CODE
    && typeof candidate.tenantId === 'string'
    && typeof candidate.clientId === 'string'
    ? { tenantId: candidate.tenantId, clientId: candidate.clientId }
    : null
}

/**
 * Throws for a failed response: an {@link ElevationRequiredError} when the server refused content for want of an
 * elevation, and an error naming the request otherwise.
 */
export async function throwForFailedResponse(response: Response, request: string): Promise<never> {
  if (response.status === 403) {
    const requirement = readElevationRequired(await response.clone().json().catch(() => null))
    if (requirement) {
      throw new ElevationRequiredError(requirement)
    }
  }

  throw new Error(`${request}: ${response.status}`)
}
