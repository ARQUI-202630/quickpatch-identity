# AGENTS — identity

Repositorio autónomo de un microservicio QUICKPATCH.

- Stack: ASP.NET Core / .NET 10.
- Responsabilidad funcional: ver `README.md`.
- Contratos versionados: `contracts/`.
- Persistencia: EF Core + Npgsql cuando aplique.
- Kafka únicamente mediante contratos versionados.
- Preservar `tenantId`, `eventId` y `correlationId`.
- No acceder directamente a tablas de otro servicio.
- No importar código interno de otro repositorio.
- Arquitectura canónica: ARQUI-202630/quickpatch.
