SELECT t1."Country" AS c0, COUNT(*) AS c1, SUM(t0."Total") AS c2
FROM "sales"."Orders" AS t0
LEFT JOIN "crm"."Customers" AS t1 ON t0."CustomerId" = t1."Id" AND (t1."TenantId" = @p0)
WHERE ((t0."TenantId" = @p1) AND (NOT (t0."IsDeleted" = @p2)))
GROUP BY t1."Country"
HAVING ((COUNT(*) >= @p3) OR (t1."Country" IS NULL))
ORDER BY t1."Country" ASC NULLS LAST
LIMIT @p4
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p2 = Boolean True
-- @p3 = Int64 3
-- @p4 = Int32 11
