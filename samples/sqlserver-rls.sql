-- Sample SQL Server row-level security for OpenFetch (spec §16.5), used by the test suite. Batches are separated by GO.
-- OpenFetch calls sp_set_session_context on the query's own connection right before the query. SqlClient resets
-- SESSION_CONTEXT (sp_reset_connection) when a pooled connection is reused, so values do not leak between requests.
-- The API login must not be sysadmin/db_owner-bypassing; security policies apply to it. The password is for the test container only.

CREATE LOGIN openfetch_app WITH PASSWORD = 'OpenFetch_app_1!', CHECK_POLICY = OFF;
CREATE USER openfetch_app FOR LOGIN openfetch_app;
GRANT SELECT ON SCHEMA::sales TO openfetch_app;
GRANT SELECT ON SCHEMA::crm TO openfetch_app;
GO
CREATE SCHEMA rls;
GO
CREATE FUNCTION rls.tenant_predicate(@TenantId uniqueidentifier)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN SELECT 1 AS allowed
WHERE @TenantId = TRY_CONVERT(uniqueidentifier, CONVERT(nvarchar(64), SESSION_CONTEXT(N'openfetch.tenant_id')));
GO
CREATE SECURITY POLICY rls.tenant_isolation
    ADD FILTER PREDICATE rls.tenant_predicate(TenantId) ON sales.Orders,
    ADD FILTER PREDICATE rls.tenant_predicate(TenantId) ON crm.Customers
    WITH (STATE = ON);
