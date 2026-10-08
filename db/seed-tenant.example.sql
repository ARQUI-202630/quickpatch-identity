-- Ejemplo: alta del tenant del MVP (DD 10.3: en el MVP opera un solo tenant).
-- Lo ejecuta el rol identity_platform o un administrador; el id es el que se configura en Channel__TenantId.
INSERT INTO tenants (id, name, nit, status, created_at)
VALUES ('00000000-0000-0000-0000-000000000001', 'Empresa oferente de ejemplo', '900000000-0', 'activo', now())
ON CONFLICT (id) DO NOTHING;
