# Provider conformance requirements

A provider is an `IQueryCompiler` implementation for a database. The normative requirements are below; the [certification suite](certification.md) is the executable check. A provider is conformant when the reference sample hosted on it passes the suite, and when its own snapshot tests cover the SQL it generates.

## MUST

1. **Identifiers.** Emit only identifiers taken from trusted metadata, always quoted. Escape the provider's quote character. Generate table aliases (`t0…`, `s1…`, `e1…`) and result aliases (`c0…`); never derive them from client input.
2. **Values.** Send every value as a parameter, including LIKE patterns (with `%`, `_`, the escape character, and any provider-specific wildcards such as `[` on SQL Server escaped) and `IN` lists.
   - **Shape-stable `IN` lists.** An `IN` list is one parameter, so the SQL text does not depend on list length: an array on PostgreSQL, JSON with `OPENJSON` on SQL Server.
   - **Lossless list types.** List values are compared at full fidelity; never truncate or round them to the column's declared size.
3. **Shape stability.** The SQL text must be a pure function of the query shape, and never of values, tenants or list lengths.
4. **Fail closed.** Refuse to compile a query, `EXISTS` subquery or expansion that is not marked `Secured`.
5. **Policy placement.**
   - The root policy is AND-ed outside the client filter.
   - Joined-dataset policies go in the `ON` clause.
   - Collection-target policies go inside the `EXISTS` or expansion subquery.
6. **Identical semantics across providers.** For the conformance corpus the provider must return byte-identical normalized results to the reference provider. This covers:
   - NULLs last for ascending order and first for descending;
   - 64-bit counts and integer sums;
   - `decimal(38,10)` averages;
   - an inclusive `between`;
   - SQL three-valued logic for `ne`/`notIn`;
   - case behaviour following the column collation (documented).
7. **Paging.** Emit `LIMIT first + 1` (or the equivalent) for `hasNext`, the bounded offset, and the expanded-OR keyset predicate for cursors.
8. **Inheritance and owned types.**
   - TPT: render the base-table join tree.
   - TPC: render the `UNION ALL` source.
   - TPH: honour the discriminator policy, which arrives as a normal trusted predicate.
9. **Session setup.**
   - Set security values on the query's own connection, before the query, in the same transaction.
   - Make sure they cannot outlive the request: transaction-local settings, or a teardown command (`CompileSessionTeardown`) plus a pooled-connection reset.
   - Validate setting keys.
10. **Limits.** Enforce the provider's parameter limit (`query_too_complex`). Never put provider error text into responses.

## SHOULD

- Use a read-only transaction where the database supports it.
- Prefer one parameter per value list, so plans can be reused.
- Document every place where the provider's semantics knowingly differ from the reference provider (for example native NULL ordering under `DISTINCT` on SQL Server).

## Certifying a provider

1. Implement `SqlCompiler`, which needs only the dialect hooks, or the whole `IQueryCompiler`.
2. Host the reference sample on the provider. See `tests/OpenFetch.Tests/HttpTests.cs` (`TestHost`) and the RLS samples in `samples/`.
3. Run `CertificationSuite.RunAsync(new HttpCertificationTarget(...))`, or `openfetch-certify --url …`, and require `CERTIFIED`.
4. Add per-provider plan snapshots (`conformance/expected-plan-<provider>/`) and tenant-isolation tests that use a one-connection pool.
