-- ============================================================================
-- 006-rename-secret-column.sql
--
-- Renames tenants.key_vault_secret_name -> tenants.secret_name. Credential
-- storage no longer depends on Azure Key Vault (see ISecretStore/OpenBao) —
-- this column now just names a secret in whatever store is configured.
-- ============================================================================

EXEC sp_rename 'tenants.key_vault_secret_name', 'secret_name', 'COLUMN';
