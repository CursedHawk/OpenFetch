SELECT t0."Number" AS c0, t1."Country" AS c1, t0."Id" AS c2, t1."Id" AS c3
FROM "sales"."Orders" AS t0
LEFT JOIN "crm"."Customers" AS t1 ON t0."CustomerId" = t1."Id" AND (t1."TenantId" = @p0)
WHERE ((t0."TenantId" = @p1) AND (NOT (t0."IsDeleted" = @p2)))
  AND ((t1."Country" IS NULL) OR (t1."Country" IS NOT NULL))
ORDER BY t0."Id" ASC NULLS LAST
LIMIT @p3
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p2 = Boolean True
-- @p3 = Int32 101
