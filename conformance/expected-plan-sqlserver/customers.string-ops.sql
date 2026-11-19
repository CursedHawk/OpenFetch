SELECT t0.[Name] AS c0, t0.[Country] AS c1, t0.[Id] AS c2
FROM [crm].[Customers] AS t0
WHERE (t0.[TenantId] = @p0)
  AND ((t0.[Name] LIKE @p1 ESCAPE '\') AND (t0.[Country] NOT IN (SELECT v FROM OPENJSON(@p2) WITH (v nvarchar(max) '$'))))
ORDER BY t0.[Id] ASC
OFFSET @p3 ROWS FETCH NEXT @p4 ROWS ONLY
-- @p0 = Guid aaaaaaaa-0000-0000-0000-000000000001
-- @p1 = String %1
-- @p2 = String ["DE","AT"]
-- @p3 = Int32 0
-- @p4 = Int32 101
