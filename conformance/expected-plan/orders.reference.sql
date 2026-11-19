SELECT t0."Id" AS c0, t0."Number" AS c1, t0."Status" AS c2, t0."Total" AS c3, t0."CreatedAt" AS c4
FROM "sales"."Orders" AS t0
WHERE ((t0."TenantId" = @p0) AND (NOT (t0."IsDeleted" = @p1)))
  AND ((t0."Status" = ANY(@p2)) AND (t0."CreatedAt" >= @p3))
ORDER BY t0."CreatedAt" DESC NULLS FIRST, t0."Id" ASC NULLS LAST
LIMIT @p4
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Boolean True
-- @p2 = Int32[] {Int32 0, Int32 1}
-- @p3 = DateTimeOffset 2026-01-01T00:00:00.0000000+00:00
-- @p4 = Int32 4
