SELECT t0."Id" AS c0, t0."Name" AS c1
FROM "crm"."Customers" AS t0
WHERE (t0."TenantId" = @p0)
ORDER BY t0."Id" DESC NULLS FIRST
LIMIT @p1
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Int32 3
