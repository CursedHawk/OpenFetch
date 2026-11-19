SELECT t0.[Number] AS c0, t0.[Total] AS c1
FROM [sales].[Orders] AS t0
WHERE ((t0.[TenantId] = @p0) AND (NOT (t0.[IsDeleted] = @p1)))
ORDER BY t0.[Total] DESC, t0.[Id] ASC
OFFSET @p2 ROWS FETCH NEXT @p3 ROWS ONLY
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Boolean True
-- @p2 = Int32 0
-- @p3 = Int32 3
