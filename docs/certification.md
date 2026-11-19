# Certification suite

`OpenFetch.Certification` (library) and `openfetch-certify` (.NET tool) check any OpenFetch server, written in any language, for compatibility and security. The checks are black-box over HTTP.

## What it checks

- **Conformance.** Every valid case in `conformance/valid` must return the committed `conformance/expected-results` (numbers compared by value; cursor tokens and model version masked). Every invalid case in `conformance/invalid` must fail with the expected `code` and `path`.
- **Security.** Each item below is one check:
  - Tenant isolation through select, filter probes, relationships, aggregates, `expand` and `any`.
  - A tenant A cursor is rejected for tenant B.
  - A tampered cursor is rejected.
  - A cursor reused for a different query shape is rejected.
  - Unknown datasets are reported as unknown.
  - Column authorization, using a policy-protected field without the policy.
  - Error oracle: another tenant's id must be indistinguishable from a non-existent id.
  - Injected values stay values.

## The reference sample

The server under test must expose the sample model at `{prefix}/orders` and `{prefix}/customers`. See `tests/OpenFetch.Tests/Domain/AppDbContext.cs` and `TestModel.Configure` for the exact attributes and seed.

| Dataset | Columns | Rules |
|---|---|---|
| `orders` | `id`, `number`, `status` (enum `open` / `processing` / `shipped` / `cancelled`), `total` (decimal, policy `Orders.FinancialData`), `createdAt` | To-one relationship `customer`; `summary` projection; aggregation enabled (`status` groupable, `total` and `createdAt` aggregatable). Row policy: the caller's tenant only, excluding soft-deleted rows. |
| `customers` | `id` (aggregatable), `name`, `country` (groupable) | One-to-many relationship `orders`. Row policy: the caller's tenant only. |

Seed data:

- Two tenants, `aaaaaaaa-0000-0000-0000-000000000001` and `bbbbbbbb-0000-0000-0000-000000000002`.
- For each tenant, 3 customers. The first two have country `CZ` (tenant A) or `SK` (tenant B); the third has none.
- For each tenant, orders 1–10:
  - number `SO-<A|B>-000n`
  - status = n mod 4 (0 → open, 1 → processing, 2 → shipped, 3 → cancelled)
  - total = 100·n + 0.50
  - createdAt = 2026-01-n 12:00Z
  - customer = n mod 3
  - order 10 is soft-deleted

Callers:

- **Tenant A** holds `Orders.FinancialData`.
- **Tenant B** does not.

## Running it

```sh
dotnet tool install -g OpenFetch.Certification.Tool
openfetch-certify --url https://api.example.test \
  --tenant-a "Authorization: Bearer <token for tenant A, with Orders.FinancialData>" \
  --tenant-b "Authorization: Bearer <token for tenant B>"
# prints per-category results, ends with CERTIFIED / NOT CERTIFIED; exit code 0 / 1
```

From .NET, call `await CertificationSuite.RunAsync(new HttpCertificationTarget(httpClient, callers))`. This repository certifies its own reference server on PostgreSQL and on SQL Server in `CertificationTests`, and checks that a server without tenant policies fails.
