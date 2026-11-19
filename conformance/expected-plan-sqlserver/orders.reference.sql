SELECT t0.[Id] AS c0, t0.[Number] AS c1, t0.[Status] AS c2, t0.[Total] AS c3, t0.[CreatedAt] AS c4
FROM [sales].[Orders] AS t0
WHERE ((t0.[TenantId] = @p0) AND (NOT (t0.[IsDeleted] = @p1)))
  AND ((t0.[Status] IN (SELECT v FROM OPENJSON(@p2) WITH (v int '$'))) AND (t0.[CreatedAt] >= @p3))
ORDER BY t0.[CreatedAt] DESC, t0.[Id] ASC
OFFSET @p4 ROWS FETCH NEXT @p5 ROWS ONLY
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = Boolean True
-- @p2 = String [0,1]
-- @p3 = DateTimeOffset 2026-01-01T00:00:00.0000000+00:00
-- @p4 = Int32 0
-- @p5 = Int32 4
