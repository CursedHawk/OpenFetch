SELECT t0."Number" AS c0, t0."CreatedAt" AS c1, t0."Id" AS c2
FROM "sales"."Orders" AS t0
WHERE ((t0."TenantId" = @p0) AND (NOT (t0."IsDeleted" = @p1)))
  AND (t0."CreatedAt" BETWEEN @p2 AND @p3)
ORDER BY t0."Id" ASC NULLS LAST
LIMIT @p4
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Boolean True
-- @p2 = DateTimeOffset 2026-01-02T00:00:00.0000000+00:00
-- @p3 = DateTimeOffset 2026-01-04T22:00:00.0000000+00:00
-- @p4 = Int32 101
