-- Add tenant_admin organization role for customer-wide bot monitoring within a tenant.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_uta_role')
    ALTER TABLE user_tenant_access DROP CONSTRAINT CK_uta_role;
GO

ALTER TABLE user_tenant_access
    ADD CONSTRAINT CK_uta_role CHECK (role IN ('org_admin', 'tenant_admin', 'cloud_admin', 'read_only'));
GO
