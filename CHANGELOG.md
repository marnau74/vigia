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
- Cinco tipos de comprobación, cada uno con su configuración y su comprobador: **HTTP(S)** (código
  esperado, palabra clave, redirecciones, tiempos de DNS y de conexión), **TLS** (días hasta la
  caducidad, emisor, protocolo; caducado y no válido son fallo siempre), **DNS** (A, AAAA, CNAME, MX y
  TXT contra un servidor concreto, con registros esperados), **TCP** (varias direcciones por nombre) e
  **ICMP** (con mensaje explicativo si el entorno no permite pings).
- El tiempo máximo corta la comprobación y la cuenta como fallo, con el reloj inyectado: los tests
  avanzan el tiempo en lugar de esperar.
- **Protección contra SSRF** (ADR 0002): una única guardia resuelve el nombre una sola vez y conecta a
  la dirección ya validada (sin DNS rebinding), bloquea los rangos internos y de metadatos de las
  nubes (también escritos como IPv6 o como número), valida cada redirección y el esquema, y prohíbe el
  proxy. Solo se puede apuntar a la red privada con un permiso explícito en el monitor.
- 186 tests, con servidores reales en local (web con HTTPS, DNS por UDP, TCP) y pruebas de mutación
  sobre cada defensa.

