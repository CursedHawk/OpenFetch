SELECT t0.[Number] AS c0, t0.[Total] AS c1, t0.[Id] AS c2
FROM [sales].[Orders] AS t0
WHERE ((t0.[TenantId] = @p0) AND (NOT (t0.[IsDeleted] = @p1)))
  AND (t0.[Total] NOT BETWEEN @p2 AND @p3)
ORDER BY t0.[Id] ASC
OFFSET @p4 ROWS FETCH NEXT @p5 ROWS ONLY
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Boolean True
-- @p2 = Decimal 200
-- @p3 = Decimal 700
-- @p4 = Int32 0
-- @p5 = Int32 101
