SELECT t1.[Country] AS c0, COUNT_BIG(*) AS c1, SUM(t0.[Total]) AS c2
FROM [sales].[Orders] AS t0
LEFT JOIN [crm].[Customers] AS t1 ON t0.[CustomerId] = t1.[Id] AND (t1.[TenantId] = @p0)
WHERE ((t0.[TenantId] = @p1) AND (NOT (t0.[IsDeleted] = @p2)))
GROUP BY t1.[Country]
HAVING ((COUNT_BIG(*) >= @p3) OR (t1.[Country] IS NULL))
ORDER BY CASE WHEN t1.[Country] IS NULL THEN 1 ELSE 0 END, t1.[Country] ASC
OFFSET @p4 ROWS FETCH NEXT @p5 ROWS ONLY
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p2 = Boolean True
-- @p3 = Int64 3
-- @p4 = Int32 0
-- @p5 = Int32 11
