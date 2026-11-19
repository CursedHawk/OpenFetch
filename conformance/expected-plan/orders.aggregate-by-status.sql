SELECT t0."Status" AS c0, COUNT(*) AS c1, SUM(t0."Total") AS c2, CAST(AVG(CAST(t0."Total" AS numeric(38,10))) AS numeric(38,10)) AS c3, MIN(t0."CreatedAt") AS c4, MAX(t0."Total") AS c5
FROM "sales"."Orders" AS t0
WHERE ((t0."TenantId" = @p0) AND (NOT (t0."IsDeleted" = @p1)))
GROUP BY t0."Status"
ORDER BY SUM(t0."Total") DESC NULLS FIRST, t0."Status" ASC NULLS LAST
LIMIT @p2
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Boolean True
-- @p2 = Int32 101
