SELECT t0."Number" AS c0, t0."Status" AS c1, t0."Id" AS c2
FROM "sales"."Orders" AS t0
WHERE ((t0."TenantId" = @p0) AND (NOT (t0."IsDeleted" = @p1)))
ORDER BY t0."Id" ASC NULLS LAST
LIMIT @p2
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Boolean True
-- @p2 = Int32 6
