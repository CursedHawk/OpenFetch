# OpenFetch query language 1.0

**Status: stable.** This is the normative definition of the query document that `version: "1.0"` refers to, as implemented and certified by this repository. [OPENFETCH.md](../OPENFETCH.md) is the design rationale; where the two differ, this document governs the wire format.

## 1. Stability and versioning

- **Additive 1.x changes only.** Within 1.x a server may add new optional document properties, filter types, operators, aggregate functions, capabilities and response properties. It must never change or remove one. Clients must ignore unknown response properties.
- **Breaking changes require 2.0.** Removing or reinterpreting anything, or making an optional property required, is a breaking change. `capabilities.versions` lists every language version a server accepts.
- **Deployment contract changes.** The deployment contract changes when a dataset, field, operator or relationship is removed, a type changes, a limit is reduced, or authorization becomes stricter (spec §29.5). Servers should report such changes as contract changes. This repository's OpenAPI breaking-change test does exactly that.

## 2. Document

A query is a JSON object (UTF-8; duplicate property names are rejected) with these properties:

| Property | Type | Notes |
|---|---|---|
| `version` | `"1.0"` | Optional; any other value is rejected with `unsupported_version`. |
| `select` | array of field paths | Required, except in aggregate queries and named projections, where it is forbidden. |
| `where` | filter | |
| `orderBy` | array of `{field, direction?}` | `direction` is `asc` (the default) or `desc`. |
| `page` | `{first?, after?, offset?}` | `after` and `offset` are mutually exclusive. |
| `distinct` | boolean | Requires the dataset capability `distinct`. |
| `groupBy` | array of field paths | Aggregation; see §7. |
| `aggregates` | array of `{function, field?, alias}` | Aggregation; see §7. |
| `having` | filter over aliases or group fields | Aggregation; see §7. |
| `expand` | array of expansions | Collections; see §6. |
| `x-<vendor>-<name>` | any JSON | Extensions; see §10. |

Any other property, at any level, is rejected with `invalid_query` and a JSON Pointer to it.

## 3. Field paths

- **Paths.** A field path is a column name (`number`) or a dotted path. A dotted path either follows to-one relationships (`customer.name`, up to the relationship depth) or reaches into an owned value object (`home.city`).
- **Never names.** Paths are never physical names and are never interpreted as SQL.
- **Unknown fields.** A path that does not exist, is hidden, is not enabled for the usage (select, filter, sort, group or aggregate), or is not authorized for the caller is reported identically, as `unknown_field`.
- **Collections.** Paths never traverse one-to-many relationships.

## 4. Filters

Filters are a tagged union on `type`:

| `type` | Shape |
|---|---|
| `comparison` | `{field, operator, value}` |
| `logical` | `{operator: "and" \| "or", children: [filter, ...]}`, with at least one child |
| `not` | `{operand: filter}` |
| `any` | `{relationship, where?}`: true when at least one related row of a one-to-many relationship matches. "None" is `not any`; "all" is `not any(not …)`. `where` uses the related dataset's own fields. Not allowed in `having`. |

Comparison operators:

| Operator | Value |
|---|---|
| `eq`, `ne` | Scalar or `null` (`null` only on nullable fields; compiles to `IS [NOT] NULL`) |
| `gt`, `gte`, `lt`, `lte` | Scalar |
| `in`, `notIn` | Non-empty array, up to `maxInValues` entries |
| `between`, `notBetween` | `[low, high]`, both bounds inclusive |
| `contains`, `startsWith`, `endsWith` | String; wildcards are literal; case behaviour follows the column collation |

A column's operator set is the intersection of its type's operators and the operators the server enables for it. A disallowed operator produces `unsupported_operator`, with `allowedOperators` in the error.

`compareTo: {field}` replaces `value` for field-to-field comparison, when `features.fieldComparison` is enabled.

Values follow [the value profile](spec-profile.md).

## 5. Ordering and paging

- **Default page size.** `page.first` defaults to the dataset's default page size.
- **Determinism.** The server always appends a unique tie-breaker: the key, or for grouped results the group fields.
- **Cursor paging.** `endCursor` is returned when every ordering field is cursor-eligible, and `after` takes it.
- **Offset paging.** `offset` is bounded by `maxOffset`.
- **Cursor binding.** Cursors bind the query shape, model version, caller scope and expiry. They are opaque; the portable format is [cursor profile v1](cursor-profile-v1.md).

## 6. Collections

An expansion is `{relationship, select, where?, orderBy?, first?}`:

- `relationship` names a one-to-many relationship.
- `select`, `where` and `orderBy` use the related dataset's own fields.
- `first` (default 10) limits rows per parent.

The parent row gains an array property named after the relationship. Parent page size × `first`, summed over expansions, must not exceed `maxExpandedRows`.

## 7. Aggregation

An aggregate query uses `groupBy` (fields with the `group` capability) and/or `aggregates`:

| Function | Applies to | Result type |
|---|---|---|
| `count` | No field: counts rows; with a field: counts non-null values | 64-bit integer |
| `countDistinct` | Any field with the `aggregate` capability | 64-bit integer |
| `sum` | Numeric fields | Integer fields: 64-bit integer; otherwise the field's numeric type |
| `avg` | Numeric fields | Integer and decimal fields: decimal with 10 fractional digits; floating-point fields: number |
| `min`, `max` | Ordered types | The field's type |

- **Aliases.** Aliases must be field-name-like, unique, and must not shadow a group field.
- **Output.** Result rows contain the group fields followed by the aliases.
- **`having` and `orderBy`.** These may reference only aliases and group fields. A group field still needs its own `filter` or `sort` capability.
- **Paging.** Aggregate queries use offset paging only.
- **Security.** Row policies apply before grouping.

## 8. Named projections

`POST {prefix}/{dataset}/{projection}` returns exactly the fields of a server-defined projection.

- **Forbidden properties.** The document may not contain `select`, `groupBy`, `aggregates`, `having` or `expand`.
- **Inaccessible projections.** A projection whose fields the caller cannot all use is reported as an unknown dataset.

## 9. Responses and errors

**Success.** A successful response is:

```json
{"data": [...], "page": {"count": n, "hasNext": bool, "endCursor": string|null}, "meta": {"dataset": "...", "modelVersion": "..."}}
```

Rows contain only the selected fields. To-one relationships and owned values are nested objects; a missing related row is `null`.

**Errors.** Errors are Problem Details (`application/problem+json`) with a stable `code` and a JSON Pointer `path`:

| Code | Status | Meaning |
|---|---|---|
| `invalid_json` | 400 | |
| `invalid_query` | 400 | |
| `unsupported_version` | 400 | |
| `unsupported_operator` | 400 or 422 | |
| `unknown_dataset` | 404 | |
| `document_too_large` | 413 | |
| `unknown_field` | 422 | |
| `invalid_relationship` | 422 | |
| `invalid_value_type` | 422 | |
| `query_too_complex` | 422 | |
| `page_size_exceeded` | 422 | |
| `invalid_cursor` | 422 | |
| `cursor_scope_mismatch` | 422 | |
| `unsupported_provider_feature` | 422 | |
| `response_limit_exceeded` | 422 | |
| `unauthenticated` | 401 | |
| `forbidden` | 403 | |
| `rate_limited` | 429 | |
| `execution_failed` | 500 | |
| `query_timeout` | 504 | |

Inaccessible things are reported exactly like unknown ones.

## 10. Extensions

- **Naming.** Extension properties are named `x-<vendor>-<name>`: lowercase letters, digits and hyphens.
- **Placement.** They are allowed only at the document root.
- **Registration.** A server accepts only the extensions it registers and lists them in `capabilities.extensions` and in the OpenAPI query schema. Any other extension is rejected with `invalid_query`.
- **Semantics.** The core engine never interprets extensions; they are handed to the application. An extension must therefore never weaken security or change the meaning of standard properties. A feature that needs that is a new language version.
