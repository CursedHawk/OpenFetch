SELECT t0.[Status] AS c0, COUNT_BIG(*) AS c1, SUM(t0.[Total]) AS c2, CAST(AVG(CAST(t0.[Total] AS decimal(38,10))) AS decimal(38,10)) AS c3, MIN(t0.[CreatedAt]) AS c4, MAX(t0.[Total]) AS c5
FROM [sales].[Orders] AS t0
WHERE ((t0.[TenantId] = @p0) AND (NOT (t0.[IsDeleted] = @p1)))
GROUP BY t0.[Status]
ORDER BY CASE WHEN SUM(t0.[Total]) IS NULL THEN 0 ELSE 1 END, SUM(t0.[Total]) DESC, t0.[Status] ASC
OFFSET @p2 ROWS FETCH NEXT @p3 ROWS ONLY
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Boolean True
-- @p2 = Int32 0
-- @p3 = Int32 101
