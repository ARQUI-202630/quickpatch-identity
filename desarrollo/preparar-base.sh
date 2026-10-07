#!/usr/bin/env bash
# Prepara la base local después de `docker compose up -d`: migraciones, permisos y tenant de ejemplo.
set -euo pipefail
cd "$(dirname "$0")/.."
MIGRADOR="Host=localhost;Port=5432;Database=db_identity;Username=identity_migrator;Password=identity_migrator_dev"
dotnet tool restore
dotnet ef database update --project src/QuickPatch.Identity.Infrastructure --connection "$MIGRADOR"
docker compose exec -T postgres psql -U postgres -d db_identity -v ON_ERROR_STOP=1 < db/roles.sql
docker compose exec -T postgres psql -U postgres -d db_identity -v ON_ERROR_STOP=1 < db/seed-tenant.example.sql
echo "Base lista. Tenant de ejemplo: 00000000-0000-0000-0000-000000000001"
