SELECT t0."Number" AS c0, t0."Total" AS c1, t0."Id" AS c2
FROM "sales"."Orders" AS t0
WHERE ((t0."TenantId" = @p0) AND (NOT (t0."IsDeleted" = @p1)))
  AND ((t0."Total" > @p2) AND (t0."Total" >= @p3) AND (t0."Total" < @p4) AND (t0."Total" <= @p5))
ORDER BY t0."Id" ASC NULLS LAST
LIMIT @p6
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Boolean True
-- @p2 = Decimal 100
-- @p3 = Decimal 200.5
-- @p4 = Decimal 900
-- @p5 = Decimal 800.5
-- @p6 = Int32 101
