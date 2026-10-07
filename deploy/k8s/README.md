# Despliegue de Identity en k3s

`identity.yaml` crea el ConfigMap, el Deployment (con las migraciones como init container), el Service y el Ingress en el namespace `quickpatch`. Es igual en QA y en producción; solo cambian los secretos.

## Secreto `identity-secretos`

Lo crea DevOps desde Ansible Vault. Nunca se guarda en el repositorio.

|Clave|Contenido|
|---|---|
|`migrator-connection`|Cadena de conexión con `identity_migrator` (dueño de las tablas; solo para las migraciones)|
|`app-connection`|Cadena de conexión con `identity_app` (sin `BYPASSRLS`)|
|`jwt-private-key`|Llave privada RSA en PEM con la que Identity firma los tokens (ADR-018)|
|`admin-email`, `admin-password`|Opcionales: administrador inicial del tenant (`admin_tenant`). Si existen, Identity lo crea al arrancar si todavía no existe|

```bash
kubectl -n quickpatch create secret generic identity-secretos \
  --from-literal=migrator-connection='Host=<vm-datos>;Port=5432;Database=db_identity;Username=identity_migrator;Password=<...>' \
  --from-literal=app-connection='Host=<vm-datos>;Port=5432;Database=db_identity;Username=identity_app;Password=<...>' \
  --from-file=jwt-private-key=identity-private.pem \
  --from-literal=admin-email='<correo>' --from-literal=admin-password='<contraseña>'
```

La llave pública correspondiente (`openssl rsa -in identity-private.pem -pubout`) es la que reciben los demás servicios.

## Primer despliegue

1. Base: `db_identity` con dueño `identity_migrator` y el rol `identity_app` (Ansible).
2. `kubectl apply -f deploy/k8s/identity.yaml`. El pod queda esperando la imagen (`:pendiente`).
3. Push a `release/*`: el pipeline publica la imagen y la fija en el Deployment; el init container aplica las migraciones.
4. Solo la primera vez, un administrador de la base ejecuta `db/roles.sql` (permisos de `identity_app`) y el alta del tenant (`db/seed-tenant.example.sql`, con el id de `Channel__TenantId`), y reinicia el servicio con `kubectl -n quickpatch rollout restart deployment/identity` para que cree el administrador inicial.
