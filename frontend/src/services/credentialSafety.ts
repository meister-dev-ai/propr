// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

const headerPatterns = [
  /(Authorization\s*:\s*Bearer\s+)([^\s,;]+)/gi,
  /(X-Ado-Token\s*[=:]\s*)([^\s,;]+)/gi,
  /(X-User-Pat\s*[=:]\s*)([^\s,;]+)/gi,
]

const jsonFieldPatterns = [
  /("accessToken"\s*:\s*")([^"]*)(")/gi,
  /("refreshToken"\s*:\s*")([^"]*)(")/gi,
  /("secret"\s*:\s*")([^"]*)(")/gi,
  /("password"\s*:\s*")([^"]*)(")/gi,
]

export function sanitizeSensitiveText(value: string): string {
  let sanitized = value

  for (const pattern of headerPatterns) {
    sanitized = sanitized.replace(pattern, '$1[redacted]')
  }

  for (const pattern of jsonFieldPatterns) {
    sanitized = sanitized.replace(pattern, '$1[redacted]$3')
  }

  return sanitized
}

export function sanitizeErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message) {
    return sanitizeSensitiveText(error.message)
  }

  if (typeof error === 'string' && error.length > 0) {
    return sanitizeSensitiveText(error)
  }

  return sanitizeSensitiveText(fallback)
}
