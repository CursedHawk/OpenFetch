SELECT COUNT_BIG(*) AS c0, COUNT_BIG(DISTINCT t1.[Id]) AS c1
FROM [sales].[Orders] AS t0
LEFT JOIN [crm].[Customers] AS t1 ON t0.[CustomerId] = t1.[Id] AND (t1.[TenantId] = @p0)
WHERE ((t0.[TenantId] = @p1) AND (NOT (t0.[IsDeleted] = @p2)))
  AND (t0.[Total] > @p3)
ORDER BY (SELECT NULL)
OFFSET @p4 ROWS FETCH NEXT @p5 ROWS ONLY
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p2 = Boolean True
-- @p3 = Decimal 150
-- @p4 = Int32 0
-- @p5 = Int32 101
