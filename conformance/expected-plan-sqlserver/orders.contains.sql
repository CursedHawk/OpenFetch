SELECT t0.[Number] AS c0, t0.[Id] AS c1
FROM [sales].[Orders] AS t0
WHERE ((t0.[TenantId] = @p0) AND (NOT (t0.[IsDeleted] = @p1)))
  AND (t0.[Number] LIKE @p2 ESCAPE '\')
ORDER BY t0.[Id] ASC
OFFSET @p3 ROWS FETCH NEXT @p4 ROWS ONLY
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Boolean True
-- @p2 = String %A-00%
-- @p3 = Int32 0
-- @p4 = Int32 101
