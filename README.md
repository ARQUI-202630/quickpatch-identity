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
|`GET /v1/admin/technicians`: técnicos y proveedores del tenant, paginados y filtrables por `verificationStatus` (RF-19); solo `admin_tenant`|`openapi/identity.v1.yaml` 1.3.0|SCRUM-39 / SCRUM-106|
|`POST /v1/admin/technicians/{userId}/approve`, `reject` y `suspend`: transiciones del DD 8.3.13 (409 `transicion-invalida` si no aplica), motivo de 5 a 500 caracteres al rechazar o suspender, auditoría y evento (RF-19, RF-20)|`openapi/identity.v1.yaml` 1.3.0|SCRUM-39, SCRUM-40 / SCRUM-106, SCRUM-109|
|`GET /internal/v1/users?ids=`: nombre y teléfono de hasta 20 usuarios del tenant del token, para que ServiceRequest arme el detalle de la solicitud (DD V4 8.3.4). Interno: el Ingress no publica `/internal` y el servicio que llama reenvía el JWT del usuario|`openapi/identity-internal.v1.yaml` 1.0.0|SCRUM-91|
|`POST /v1/providers/me/technicians`: el proveedor da de alta un técnico en su equipo (rol `tecnico`, `provider_id` del token, tenant del proveedor y estado `pendiente`, RN-TP3) y se publica `technician.registered` con `providerId`. El técnico queda sin contraseña utilizable hasta que exista el flujo del enlace de Communication (RF-16)|`openapi/identity.v1.yaml` 1.4.0|SCRUM-36 / SCRUM-97|
|`GET /v1/providers/me/technicians?status=activo\|inactivo`: solo el equipo del proveedor del token (RN-TP4), paginado; `activo` son los aprobados (RF-16)|`openapi/identity.v1.yaml` 1.4.0|SCRUM-36 / SCRUM-97|
|Cuenta corporativa: `POST /v1/auth/register/company` crea en una transacción la empresa cliente (`client_companies`) y su primer `empresa_contacto` en el tenant del canal (409 `nit-registrado` o `correo-registrado`). `GET /v1/companies/me`, `GET` y `POST /v1/companies/me/users` e invitación nueva: la empresa sale del usuario que llama (RN-CC3) y el usuario agregado define su contraseña con la invitación (RN-CC4). El token del `empresa_contacto` lleva `company_id` (RF-06)|`openapi/identity.v1.yaml` 1.8.0|SCRUM-26 / SCRUM-66|

> El registro de técnicos, el `FOR UPDATE` del login, la validación de tenant activo en el registro y el entorno local con Docker vienen del trabajo de José Castiblanco (@joseval29). Queda pendiente: refresh tokens.

## Eventos

|Evento|Cuándo|Contrato|Historia|
|---|---|---|---|
|`technician.verification-changed` v1 (clave: `technicianId`)|El admin del tenant aprueba, rechaza o suspende al técnico. Lleva el estado anterior y el nuevo, el motivo y `updatedAt` = momento del cambio (ADR-017); Matching actualiza su copia del estado.|`contracts/kafka/events/technician.verification-changed.v1.json` (v0.3.0)|SCRUM-39, SCRUM-40 / SCRUM-106, SCRUM-109|
|`technician.registered` v1 (clave: `technicianId`)|Un técnico o proveedor se registra y queda `pendiente`. Lleva la especialidad, el proveedor y el estado, con `updatedAt` como versión (ADR-017); Matching arma con él su proyección del técnico (P-01).|`contracts/kafka/events/technician.registered.v1.json` (v0.3.0)|SCRUM-22 / SCRUM-54|

Se publican con **Transactional Outbox** (ADR-007): el evento se guarda en `outbox_events` en la misma transacción del registro y un `BackgroundService` lo envía a Kafka (topic = tipo del evento, cabeceras `eventId` y `eventType`) y marca `published_at`. Si Kafka falla, suma un intento y lo reintenta en la siguiente vuelta. El servicio solo **inserta** en `outbox_events` con el tenant de su sesión (RLS); el publicador lee los de todos los tenants adoptando `identity_outbox` (`SET LOCAL ROLE`, BYPASSRLS). Sin `Kafka__BootstrapServers` (desarrollo local) los eventos se quedan en la tabla sin publicar.

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

### Usuarios de prueba (SCRUM-450)

Con `ASPNETCORE_ENVIRONMENT=Development`, Identity crea al arrancar (si no existen) un usuario por rol en el tenant del canal, con los mismos casos de uso del registro (hash BCrypt y RLS). Se activa con `DevelopmentUsers:Enabled` de `appsettings.Development.json`; en QA y producción el entorno no es `Development` y el sembrado ni se registra. La contraseña es pública a propósito: **no la uses fuera de una base local**.

|Correo|Rol|Notas|
|---|---|---|
|`cliente@quickpatch.dev`|`cliente`|App móvil|
|`tecnico@quickpatch.dev`|`tecnico`|App móvil; perfil `pendiente`, documento `DEV-0001`|
|`proveedor@quickpatch.dev`|`proveedor`|App móvil; perfil `pendiente`, documento `DEV-0002`|
|`admin@quickpatch.dev`|`admin_tenant`|Panel web|
|`plataforma@quickpatch.dev`|`admin_plataforma`|Panel web (gestión de tenants)|

Contraseña de todos: `Quickpatch.Dev2026`. La especialidad del técnico y del proveedor es un identificador fijo de desarrollo (`00000000-0000-0000-0000-0000000000d1`), porque las categorías viven en la base de Catalog.

## Base de datos

1. Migraciones: `dotnet tool restore` y `dotnet ef database update --project src/QuickPatch.Identity.Infrastructure --connection "<cadena>"`.
2. Roles y permisos: `db/roles.sql` (`identity_app`, `identity_platform` e `identity_outbox`), después de las migraciones.
3. Alta del tenant del MVP: ejemplo en `db/seed-tenant.example.sql`.

## Configuración

|Clave|Para qué|
|---|---|
|`ConnectionStrings__Identity`|PostgreSQL con el usuario `identity_app`|
|`Jwt__PrivateKeyPem`, `Jwt__Issuer`, `Jwt__Audience`, `Jwt__Lifetime`|Emisión del token|
|`Channel__TenantId`|Tenant del canal (RN-U5)|
|`Passwords__WorkFactor`|Costo de BCrypt|
|`Kafka__BootstrapServers`|Kafka (VM6 en producción, VM7 en QA); vacío, no se publica|
|`Outbox__PublisherRole`, `Outbox__BatchSize`, `Outbox__PollInterval`|Publicador del Outbox (`identity_outbox`, 50 eventos, 1 s)|

## Pruebas

- `tests/unit`: dominio, casos de uso y API completa con puertos en memoria y el emisor RS256 real.
- `tests/integration`: PostgreSQL 16 y Kafka reales con Testcontainers: migraciones, roles, BCrypt, bloqueo, tenant inactivo, RLS y publicación de `technician.registered` por el Outbox.

## Contenedor

- Imagen: `Dockerfile` en la raíz (multi-stage, usuario sin privilegios).
- Puerto: `8080`.
- Probes para k3s: `GET /health/live` (el proceso responde) y `GET /health/ready` (el servicio y sus dependencias están listos).

## Despliegue

- `deploy/k8s/identity.yaml`: ConfigMap, Deployment, Service e Ingress (`/v1/auth`, `/v1/users`, `/v1/platform`, `/v1/admin/technicians`, `/v1/providers`, `/v1/companies`) para k3s. Las migraciones se aplican con un *migration bundle* de EF Core (`/app/efbundle`, incluido en la imagen) como init container, con el rol `identity_migrator`.
- Secretos, primer despliegue y alta del tenant: `deploy/k8s/README.md`.
- **Administradores iniciales:** si `Bootstrap__AdminEmail`/`Bootstrap__AdminPassword` o `Bootstrap__PlatformAdminEmail`/`Bootstrap__PlatformAdminPassword` están configurados (secreto), Identity crea al arrancar el `admin_tenant` o el `admin_plataforma` del tenant del canal cuando todavía no existen. Ningún endpoint registra administradores.
- **Gestión de tenants (RF-21, contratos 1.2.0 y 1.5.0):** `GET`, `POST` (crear, NIT único: 409 `nit-registrado`) y `PATCH /v1/platform/tenants/{id}`, solo `admin_plataforma`. Corren en una transacción que adopta `identity_platform` (`SET LOCAL ROLE`, BYPASSRLS) y registran el cambio en `audit_logs` (DD 10.4). `identity_app` es miembro de ese rol sin heredarlo (`db/roles.sql`).
