SELECT t0."Number" AS c0, t0."Total" AS c1
FROM "sales"."Orders" AS t0
WHERE ((t0."TenantId" = @p0) AND (NOT (t0."IsDeleted" = @p1)))
ORDER BY t0."Total" DESC NULLS FIRST, t0."Id" ASC NULLS LAST
LIMIT @p2
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Boolean True
-- @p2 = Int32 3
