# Portable cursor profile, version 1

This profile is the portable cursor format of spec §20.1. Any OpenFetch implementation that holds the shared key can issue and read these cursors. Enable it with `services.AddOpenFetchPortableCursors("k1", keys)`; without it, cursors use ASP.NET Core Data Protection, whose format is implementation-specific. Both formats are encrypted and authenticated.

Cursors **must be confidential** as well as authenticated. The ordering values they carry can include the key tie-breaker or sort-only columns that the caller cannot select.

## Format

```
of1.<kid>.<base64url(nonce ‖ ciphertext ‖ tag)>
```

| Part | Content |
|---|---|
| `of1` | Profile identifier and version |
| `kid` | Key id: 1–32 ASCII letters or digits. Several keys may decrypt, which allows rotation; only the current key encrypts. |
| Encryption | AES-256-GCM with a 12-byte random nonce and a 16-byte tag. base64url is RFC 4648 §5, without padding. |
| Key | HKDF-SHA256 over the shared key (at least 256 bits), with an empty salt, info `openfetch cursor v1` and 32-byte output |
| Associated data | The ASCII bytes of `of1.<kid>` |

## Payload (UTF-8 JSON, encrypted)

| Field | Type | Meaning |
|---|---|---|
| `m` | string | Model version: the structural hash of the `QueryModel` |
| `s` | string | Query-shape hash: the query's structure without values, including the policy shape |
| `c` | string | Scope hash: tenant, user name, user id, and the concrete row-policy values |
| `x` | integer | Expiry in Unix seconds (`limits.cursorLifetimeSeconds` after issue) |
| `k` | array | Values of the ordering fields of the last row, in order: the client ordering plus the key tie-breaker. They use the JSON value profile in [spec-profile.md](spec-profile.md). |

## Verification

Verification must happen in this order. Any failure produces `invalid_cursor`, except a scope mismatch, which produces `cursor_scope_mismatch`.

1. Split the cursor into exactly 3 parts. The prefix must be `of1` and the key id must be known.
2. Decrypt with the derived key and the associated data. The tag must verify.
3. Decode the payload JSON and require `x` to be in the future.
4. Require `m`, `s` and `c` to equal the values recomputed for the current request (`cursor_scope_mismatch`).
5. Require `k` to have one entry per ordering field, and convert each entry with that field's trusted metadata.

## Test vector

Key id `k1` with shared key = the ASCII bytes of `openfetch-test-key-0123456789abcdef`, nonce = bytes `00 01 … 0b` (fixed for the vector only; real nonces are random), and this payload:

```json
{"m":"0123456789abcdef","s":"fedcba9876543210","c":"00112233445566aa","x":1800000000,"k":["2026-01-09T12:00:00Z","aaaaaaaa-1111-0000-0000-000000000009"]}
```

produces this cursor:

```text
of1.k1.AAECAwQFBgcICQoL0z74UXUcJp1pFznG2u35JfgqCjvoGUS0rbCN78BvaOoF1YvbQynNA8yhZlPKcK6Xr8OnkR5FehbBFGWY35gVaN2GAB6Rf3HxuNbMlabaVGBQSu4pZl8iVsKfkwX2oqFdrTgqDAR0jtn3OaPp8TYBqb6jWmiWtGGoJqXL__n_GKJMQe4obsiVdTEfvTTjhU43uzI43ijEcvbzJO42YJrx1rbHU1-9CDLtgA
```
