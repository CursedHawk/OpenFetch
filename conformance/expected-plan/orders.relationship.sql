SELECT t0."Number" AS c0, t1."Name" AS c1, t1."Country" AS c2, t1."Id" AS c3
FROM "sales"."Orders" AS t0
LEFT JOIN "crm"."Customers" AS t1 ON t0."CustomerId" = t1."Id" AND (t1."TenantId" = @p0)
WHERE ((t0."TenantId" = @p1) AND (NOT (t0."IsDeleted" = @p2)))
  AND (t1."Name" = @p3)
ORDER BY t1."Name" ASC NULLS LAST, t0."Id" ASC NULLS LAST
LIMIT @p4
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p2 = Boolean True
-- @p3 = String Customer A1
-- @p4 = Int32 101
