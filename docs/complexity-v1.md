# Complexity scoring, version 1

Spec §22. The score is computed on the **secured** query, after row-policy injection, so the predicates that will actually run are counted. Queries scoring above `limits.maxComplexity` (default 150) are rejected with `query_too_complex`. Capabilities report the algorithm version as `complexity.version: 1`.

| Element | Points |
|---|---|
| Root dataset | 1 |
| Each selected field | 1 |
| Each relationship join | 15 |
| `distinct` | 20 |
| Each `groupBy` field | 15 |
| Each aggregate | 10 |
| Each comparison (client, `having`, or policy) | 2 |
| Each `or` group (client or policy) | 5 |
| `contains` / `endsWith` (leading wildcard) | +25 |
| Client comparison on a non-indexed column | +20 |
| Ordering field on a non-indexed column (root key columns are exempt; not charged for aggregate queries, which sort grouped rows) | +30 |

A column counts as indexed when it is the leading column of an EF index or key.

Trusted policy comparisons are never charged the non-indexed penalty, so server policies do not eat into the client's budget.

Separate structural limits apply before scoring, each with its own error:

- `maxSelectedFields`
- `maxConditions` (client comparisons only)
- `maxInValues`
- `maxFilterDepth` (checked in the parser)
- `maxRelationships`
- `maxRelationshipDepth`
- `maxOffset`

Worked example. The spec §31 reference request on the sample model scores 84:

| Part | Points |
|---|---|
| Root | 1 |
| 5 selected fields | 5 |
| 2 client conditions on non-indexed columns | 2 × 22 = 44 |
| Tenant policy | 2 |
| Soft-delete policy | 2 |
| `createdAt` ordering: indexed only as the second column of `(TenantId, CreatedAt)`, so non-indexed | 30 |

Total: 1 + 5 + 44 + 2 + 2 + 30 = 84. Indexing `CreatedAt` as a leading column brings it down to 54.
