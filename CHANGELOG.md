# Changelog

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); versionado
[semántico](https://semver.org/lang/es/).

## [Sin publicar]

### Añadido
- Esqueleto de la solución .NET 10 por capas: dominio, comprobaciones de red, datos, worker, API,
  entorno local con .NET Aspire (PostgreSQL 17 y Mailpit) y observabilidad con OpenTelemetry.
- El worker va separado de la API para que las comprobaciones no se interrumpan al desplegar la
  interfaz (ADR 0001). Ambos exponen `/health` y `/alive`.
- Normas comunes: nulos estrictos, avisos como errores, analizadores recomendados y versiones de
  NuGet centralizadas.
- Tests de arquitectura sobre la dirección de las dependencias entre proyectos y tests de salud de
  la API y el worker.
- CI con formato, compilación, tests por proyecto y control de privacidad; CodeQL y Dependabot.
