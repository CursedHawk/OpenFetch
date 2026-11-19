SELECT t0."Name" AS c0, t0."Id" AS c1
FROM "crm"."Customers" AS t0
WHERE (t0."TenantId" = @p0)
  AND (EXISTS (SELECT 1 FROM "sales"."Orders" AS s1 WHERE s1."CustomerId" = t0."Id" AND ((s1."TenantId" = @p1) AND (NOT (s1."IsDeleted" = @p2))) AND (s1."Status" = @p3)))
ORDER BY t0."Id" ASC NULLS LAST
LIMIT @p4
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p2 = Boolean True
-- @p3 = Int32 0
-- @p4 = Int32 101
