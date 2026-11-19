# @openfetch/query

A reference TypeScript query builder for the [OpenFetch](../../README.md) JSON query protocol, language 1.0. It works with
the row types that `openapi-typescript` generates from an OpenFetch OpenAPI document:

```ts
import { query } from "@openfetch/query";
import type { components } from "./schema";

const body = query<components["schemas"]["OrdersRow"]>()
  .select("id", "number", "customer.name")
  .where(f => f.and(f.in("status", ["open", "processing"]), f.gte("createdAt", "2026-01-01T00:00:00Z")))
  .orderBy("createdAt", "desc")
  .first(50)
  .build();
// POST /query/orders with `body`
```

**Type checking.** Field paths, including to-one paths like `customer.name`, are checked against the row type, and so are
comparison values (enums, numbers, `null` only where nullable). Collections are available only through `any(...)` and
`expand(...)`. The server still enforces capabilities, operators, authorization and limits.

**What the builder covers:**
- `select`, `orderBy`
- `first`/`after`/`offset` paging
- `distinct`
- filters: `eq ne gt gte lt lte in notIn between notBetween contains startsWith endsWith`, `and`/`or`/`not`/`any`
- `expand`
- `groupBy`, `aggregate`, `having`
