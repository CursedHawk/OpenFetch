SELECT t0."Name" AS c0, t0."Country" AS c1, t0."Id" AS c2
FROM "crm"."Customers" AS t0
WHERE (t0."TenantId" = @p0)
  AND ((t0."Name" LIKE @p1 ESCAPE '\') AND (t0."Country" <> ALL(@p2)))
ORDER BY t0."Id" ASC NULLS LAST
LIMIT @p3
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = String %1
-- @p2 = String[] {String DE, String AT}
-- @p3 = Int32 101
