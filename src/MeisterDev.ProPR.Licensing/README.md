# Licensing

This project defines the license document format: the claims a license carries, the compact-JWS primitives that
sign and verify it, the typed refusal reasons, the trust anchor a signing chain has to lead back to, and the
verifier that decides whether a document leads to it. The product references this project to verify a license
and the offline issuing tool references it to sign one, so both run the same format code. The project has no
package or project references and reads no configuration.

## Verification

`LicenseVerifier` requires two cryptographic conditions. Its signature verifies under the public key of the
certificate the document names as its signer, and that certificate builds a certificate path to the trust
anchor. Neither is sufficient on its own: a document is also rejected when its payload is malformed, its
schema is unsupported, its claim bounds are invalid, or the signing certificate is not an end-entity P-256
certificate. The constructor takes the anchor and the call takes the instant to judge the term at, so the type reads
no clock and no configuration of its own. Path building is offline: revocation is not checked and certificate
downloads are switched off.

Two rules decide what a result means:

- Certificate validity is evaluated at the license's issue instant, not at the current time. A signing
  certificate is valid for a shorter period than the licenses it signs, so a license runs for its full term
  after the certificate that signed it has expired.
- The license term is a status carried on a verified license, not a refusal. A license outside its term still
  has an established signer. The caller decides how to respond to the term, whether that is refusing activation
  or running on for a period after expiry.

## Trust anchor

`licensing-root.cer` holds the public part of the licensing root certificate. It is compiled into the assembly
as an embedded resource and read by `LicenseTrustAnchor`. The file is public material, not a secret, and it has
to stay unchanged. Nothing supplies or replaces it: no build property, environment variable, mounted file,
configuration key or database value. A new signing certificate issued under this
root is accepted by the existing path check, so rotating or adding a signer needs no change here. Editing
this file and rebuilding is required only to change the trusted root itself. A test pins the certificate's SHA-256 fingerprint, so a replacement also changes
that test.
