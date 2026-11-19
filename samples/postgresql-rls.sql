-- Sample PostgreSQL row-level security for OpenFetch (spec §16.5), used by the test suite.
-- OpenFetch sets transaction-local settings (set_config(..., true)) on the query's own connection and transaction,
-- so values never leak through the connection pool. The API role must not be a superuser and must not have BYPASSRLS.
-- The password below is for the test container only.

CREATE ROLE openfetch_app LOGIN PASSWORD 'openfetch_app' NOSUPERUSER NOBYPASSRLS;
GRANT USAGE ON SCHEMA sales, crm TO openfetch_app;
GRANT SELECT ON sales."Orders", crm."Customers" TO openfetch_app;

ALTER TABLE sales."Orders" ENABLE ROW LEVEL SECURITY;
ALTER TABLE sales."Orders" FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON sales."Orders" FOR SELECT TO openfetch_app
    USING ("TenantId" = NULLIF(current_setting('openfetch.tenant_id', true), '')::uuid);

ALTER TABLE crm."Customers" ENABLE ROW LEVEL SECURITY;
ALTER TABLE crm."Customers" FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON crm."Customers" FOR SELECT TO openfetch_app
    USING ("TenantId" = NULLIF(current_setting('openfetch.tenant_id', true), '')::uuid);
