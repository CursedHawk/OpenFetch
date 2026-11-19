SELECT t0."Id" AS c0, t0."Name" AS c1, t0."Id" AS c2
FROM "crm"."Customers" AS t0
WHERE (t0."TenantId" = @p0)
ORDER BY t0."Id" ASC NULLS LAST
LIMIT @p1
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Int32 101
-- expansion orders
SELECT x.c0, x.c1, x.c2, x.c3
FROM (SELECT e1."CustomerId" AS c0, e1."Number" AS c1, e1."Status" AS c2, e1."Total" AS c3, ROW_NUMBER() OVER (PARTITION BY e1."CustomerId" ORDER BY e1."Total" DESC NULLS FIRST, e1."Id" ASC NULLS LAST) AS rn
  FROM "sales"."Orders" AS e1
  WHERE (e1."CustomerId" = ANY(@p0)) AND ((e1."TenantId" = @p1) AND (NOT (e1."IsDeleted" = @p2))) AND (e1."Status" <> @p3)) x
WHERE x.rn <= @p4
ORDER BY x.c0, x.rn
-- @p0 = Guid[] {Guid 00000000-0000-0000-0000-000000000000}
-- @p1 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p2 = Boolean True
-- @p3 = Int32 3
-- @p4 = Int32 2
