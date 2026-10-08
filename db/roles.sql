-- Roles y permisos de Identity Service (DD, secciones 10.2 y 10.4).
-- Lo ejecuta un administrador de la base DESPUÉS de las migraciones (que crean las tablas y las políticas RLS).
-- Las contraseñas no van aquí: se asignan aparte con ALTER ROLE ... LOGIN PASSWORD, desde el vault.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'identity_app') THEN
        CREATE ROLE identity_app NOLOGIN NOBYPASSRLS;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'identity_platform') THEN
        CREATE ROLE identity_platform NOLOGIN BYPASSRLS;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO identity_app, identity_platform;

-- Rol del servicio: todo bajo RLS. Sobre tenants solo lectura: ningún tenant puede modificarse a sí mismo.
GRANT SELECT ON tenants TO identity_app;
GRANT SELECT, INSERT, UPDATE ON users, technician_profiles TO identity_app;

-- Auditoría (DD 5.14): el servicio registra y consulta las de su tenant (RLS); nunca las modifica ni las borra.
GRANT SELECT, INSERT ON audit_logs TO identity_app;

-- Operaciones de plataforma (RF-21, DD 10.4): gestión de tenants y su auditoría (filas sin tenant).
-- El servicio adopta el rol con SET LOCAL ROLE solo dentro de la transacción de plataforma; NOINHERIT para que
-- identity_app no herede BYPASSRLS fuera de ella.
GRANT SELECT, INSERT, UPDATE ON tenants TO identity_platform;
GRANT SELECT ON users, technician_profiles TO identity_platform;
GRANT SELECT, INSERT ON audit_logs TO identity_platform;
GRANT identity_platform TO identity_app WITH INHERIT FALSE, SET TRUE;
