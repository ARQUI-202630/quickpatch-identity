-- Solo desarrollo local (docker-compose.yml). Crea la base y los roles con contraseñas de desarrollo.
-- En QA y producción los roles los crea DevOps y las contraseñas salen del vault.
CREATE ROLE identity_migrator LOGIN BYPASSRLS PASSWORD 'identity_migrator_dev';
CREATE ROLE identity_app LOGIN NOBYPASSRLS PASSWORD 'identity_app_dev';
CREATE DATABASE db_identity OWNER identity_migrator;
