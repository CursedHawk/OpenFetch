# Signed cursor profile, version 1

This profile is the portable cursor format of spec §20.1. Any OpenFetch implementation that shares the key can issue and verify these cursors. Enable it with `services.AddOpenFetchSignedCursors("k1", keys)`; without it, cursors fall back to ASP.NET Core Data Protection, whose format is implementation-specific.

## Format

```
of1.<kid>.<payload>.<mac>
```

| Part | Content |
|---|---|
| `of1` | Profile identifier and version |
| `kid` | Key id: 1–32 ASCII letters or digits. Several keys may verify, which allows rotation; only the current key signs. |
| `payload` | base64url (RFC 4648 §5, no padding) of the UTF-8 JSON payload |
| `mac` | base64url of HMAC-SHA256(key, ASCII bytes of `of1.<kid>.<payload>`). Keys are at least 256 bits. |

Cursors are **authenticated, not encrypted**. The payload holds only values the caller already received, plus hashes.

## Payload

| Field | Type | Meaning |
|---|---|---|
| `m` | string | Model version: the structural hash of the `QueryModel` |
| `s` | string | Query-shape hash: the query's structure without values, including the policy shape |
| `c` | string | Scope hash: tenant, user name, user id, and the concrete row-policy values |
| `x` | integer | Expiry in Unix seconds (`limits.cursorLifetimeSeconds` after issue) |
| `k` | array | Values of the ordering fields of the last row, in order: the client ordering plus the key tie-breaker. They use the JSON value profile in [spec-profile.md](spec-profile.md). |

## Verification

Verification must happen in this order. Any failure produces `invalid_cursor`, except a scope mismatch, which produces `cursor_scope_mismatch`.

1. Split the cursor into exactly 4 parts. The prefix must be `of1` and the key id must be known.
2. Recompute the MAC and compare it in **constant time**.
3. Decode the payload JSON and require `x` to be in the future.
4. Require `m`, `s` and `c` to equal the values recomputed for the current request. A cursor issued under another tenant, policy scope, query shape or model version is therefore rejected (`cursor_scope_mismatch`).
5. Require `k` to have one entry per ordering field, and convert each entry with that field's trusted metadata.

## Test vector

Key id `k1` with key = the ASCII bytes of `openfetch-test-key-0123456789abcdef`, and this payload:

```json
{"m":"0123456789abcdef","s":"fedcba9876543210","c":"00112233445566aa","x":1800000000,"k":["2026-01-09T12:00:00Z","aaaaaaaa-1111-0000-0000-000000000009"]}
```

produces this cursor:

```text
of1.k1.eyJtIjoiMDEyMzQ1Njc4OWFiY2RlZiIsInMiOiJmZWRjYmE5ODc2NTQzMjEwIiwiYyI6IjAwMTEyMjMzNDQ1NTY2YWEiLCJ4IjoxODAwMDAwMDAwLCJrIjpbIjIwMjYtMDEtMDlUMTI6MDA6MDBaIiwiYWFhYWFhYWEtMTExMS0wMDAwLTAwMDAtMDAwMDAwMDAwMDA5Il19.Jzro2SrTQ3KyBeNv5d3MTNaCvYrLOYgJfeJmaj_t5Fo
```
