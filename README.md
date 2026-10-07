# identity

**Tecnología:** ASP.NET Core

## Responsabilidad

Autenticación, usuarios, roles, tenants y contexto autenticado.

## Reglas

- Mantener el ownership definido en DD/SDD.
- No escribir directamente en tablas de otros servicios.
- Publicar/consumir eventos únicamente mediante contratos versionados.
- Mantener aislamiento multi-tenant cuando corresponda.

## Estructura

Cuatro capas, según el SDD (secciones 6.3 y 6.5):

```text
QuickPatch.Identity.slnx
src/
  QuickPatch.Identity.Api/              Endpoints, validación de entrada y raíz de composición
  QuickPatch.Identity.Application/      Casos de uso, comandos y consultas
  QuickPatch.Identity.Domain/           Entidades, value objects y reglas de negocio (sin dependencias externas)
  QuickPatch.Identity.Infrastructure/   PostgreSQL, Kafka, Redis y adaptadores externos
tests/
  unit/QuickPatch.Identity.UnitTests/
  integration/QuickPatch.Identity.IntegrationTests/
```

Dependencias permitidas: `Api → Application → Domain`; `Infrastructure → Application, Domain`. `Api` referencia `Infrastructure` solo para registrar sus servicios.

## Desarrollo local

Requiere el SDK de .NET indicado en `global.json`.

```bash
dotnet restore
dotnet format --verify-no-changes   # lint, igual que el CI
dotnet build -c Release
dotnet test tests/unit/QuickPatch.Identity.UnitTests
dotnet test tests/integration/QuickPatch.Identity.IntegrationTests
dotnet run --project src/QuickPatch.Identity.Api
```

## Capacidades implementadas

|Capacidad|Contrato|Historia|
|---|---|---|
|`POST /v1/auth/register/client`: registro de cliente (RF-01)|`openapi/identity.v1.yaml`|SCRUM-21 / SCRUM-63|
|`POST /v1/auth/register/technician`: registro de técnico o proveedor con perfil `pendiente` (RF-02); documento único por tenant|`openapi/identity.v1.yaml` 1.1.0|SCRUM-22|
|`POST /v1/auth/login`: login con bloqueo tras 5 fallos durante 15 min (RF-03, RN-U4) y rechazo de tenant inactivo (RN-T1)|`openapi/identity.v1.yaml`|SCRUM-23 / SCRUM-63|
|`GET /v1/users/me`: perfil del usuario del token; para técnicos y proveedores incluye `verificationStatus`|`openapi/identity.v1.yaml`|SCRUM-63|

> El registro de técnicos, el `FOR UPDATE` del login, la validación de tenant activo en el registro y el entorno local con Docker vienen del trabajo de José Castiblanco (@joseval29). Quedan pendientes, hasta que el equipo apruebe el DD 3.1: registro de empresa (`client_companies`), evento `technician.registered` (P-01) y refresh tokens.

## Seguridad

- **Token:** JWT RS256 con `sub`, `tenant_id`, `role`, `iss`, `aud`, `exp` y `jti`. Solo Identity tiene la llave privada (`Jwt__PrivateKeyPem`, secreto de k3s); los demás servicios reciben la pública en `Jwt__PublicKeyPem`. Para obtenerla: `openssl pkey -in privada.pem -pubout`.
- **Contraseñas:** BCrypt con factor 12 (`Passwords__WorkFactor`), RN-U2 y RNF-03. Correo inexistente y contraseña incorrecta responden igual (401) y gastan el mismo tiempo.
- **Tenant del canal (RN-U5):** registro y login usan `Channel__TenantId`, nunca un campo del cuerpo. En el MVP opera un solo tenant (DD 10.3); sin él configurado, esos endpoints responden 503.
- **RLS (DD 10.2):** `users` aislada por tenant; `tenants` visible solo para su propio tenant y de solo lectura para `identity_app`.
- **RNF-04:** cada 403 de autorización queda en un log WARNING con ruta, usuario, rol y `correlationId`; los logs salen en JSON.

## Desarrollo local

Requiere Docker y el SDK de .NET de `global.json`.

```bash
docker compose up -d                       # PostgreSQL 16 con los roles de desarrollo
bash desarrollo/preparar-base.sh           # migraciones, db/roles.sql y tenant de ejemplo
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out desarrollo/dev.pem   # llave local; *.pem no se versiona
export Jwt__PrivateKeyPem="$(cat desarrollo/dev.pem)"
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/QuickPatch.Identity.Api
```

`appsettings.Development.json` apunta a esa base y al tenant de ejemplo; sus contraseñas son solo locales.

## Base de datos

1. Migraciones: `dotnet tool restore` y `dotnet ef database update --project src/QuickPatch.Identity.Infrastructure --connection "<cadena>"`.
2. Roles y permisos: `db/roles.sql` (`identity_app` y `identity_platform`), después de las migraciones.
3. Alta del tenant del MVP: ejemplo en `db/seed-tenant.example.sql`.

## Configuración

|Clave|Para qué|
|---|---|
|`ConnectionStrings__Identity`|PostgreSQL con el usuario `identity_app`|
|`Jwt__PrivateKeyPem`, `Jwt__Issuer`, `Jwt__Audience`, `Jwt__Lifetime`|Emisión del token|
|`Channel__TenantId`|Tenant del canal (RN-U5)|
|`Passwords__WorkFactor`|Costo de BCrypt|

## Pruebas

- `tests/unit`: dominio, casos de uso y API completa con puertos en memoria y el emisor RS256 real.
- `tests/integration`: PostgreSQL 16 real con Testcontainers: migraciones, roles, BCrypt, bloqueo, tenant inactivo y RLS.

## Contenedor

- Imagen: `Dockerfile` en la raíz (multi-stage, usuario sin privilegios).
- Puerto: `8080`.
- Probes para k3s: `GET /health/live` (el proceso responde) y `GET /health/ready` (el servicio y sus dependencias están listos).

## Despliegue

- `deploy/k8s/identity.yaml`: ConfigMap, Deployment, Service e Ingress (`/v1/auth`, `/v1/users`) para k3s. Las migraciones se aplican con un *migration bundle* de EF Core (`/app/efbundle`, incluido en la imagen) como init container, con el rol `identity_migrator`.
- Secretos, primer despliegue y alta del tenant: `deploy/k8s/README.md`.
- **Administrador inicial:** si `Bootstrap__AdminEmail` y `Bootstrap__AdminPassword` están configurados (secreto), Identity crea al arrancar el `admin_tenant` del tenant del canal cuando todavía no existe. Ningún endpoint registra administradores.
