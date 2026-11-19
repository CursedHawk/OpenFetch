SELECT t0."Id" AS c0
FROM "sales"."Orders" AS t0
WHERE ((t0."TenantId" = @p0) AND (NOT (t0."IsDeleted" = @p1)))
  AND ((t0."Number" = @p2) AND (t0."Status" <> @p3))
ORDER BY t0."Id" ASC NULLS LAST
LIMIT @p4
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Boolean True
-- @p2 = String SO-A-0001
-- @p3 = Int32 3
-- @p4 = Int32 101
