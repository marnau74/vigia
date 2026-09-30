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
- Tests de las comprobaciones con servidores reales en local (web con HTTPS, DNS por UDP, TCP) y
  pruebas de mutación sobre cada defensa.
- **Dominio de los monitores**: monitores con sus reglas de validación (intervalo mínimo de 30 s, tiempo
  máximo menor que el intervalo, configuración correcta para cada tipo), grupos para las páginas de
  estado y ventanas de mantenimiento.
- **Máquina de estados** (desconocido, operativo, degradado, sospechoso, caído y mantenimiento): un
  aviso de caída y otro de recuperación por incidente, sin repeticiones; los fallos aislados no avisan;
  el mantenimiento no cuenta ni abre incidentes (ADR 0003). Se prueba con secuencias de resultados y
  con diez mil secuencias aleatorias contra un modelo de referencia.
- **Persistencia** con EF Core y PostgreSQL: monitores, grupos, seguimiento, incidentes (un índice único
  garantiza un solo incidente abierto por monitor), cambios de estado y ventanas de mantenimiento; la
  configuración de cada tipo se guarda como JSON.
- **Resultados particionados por mes** (ADR 0004): la retención de 14 días se cumple eliminando particiones
  enteras (una de 200.000 filas en menos de 3 s), las consultas por fecha solo tocan el mes que necesitan
  y un índice BRIN ocupa unos kilobytes. Las particiones se crean por adelantado.
- **Agregados por hora y por día** calculados con el mismo dominio que la disponibilidad, idempotentes y
  con latencias solo de comprobaciones correctas fuera de mantenimiento; se conservan 90 días por hora y
  el histórico por día.
- **Planificador del worker**: cola de prioridad sin solapes ni ráfagas, con reparto inicial, ritmo fijo y
  concurrencia acotada; reintento inmediato antes de contar un fallo; recarga instantánea de cambios con
  `LISTEN/NOTIFY`; agregación y retención de fondo; métricas OpenTelemetry (`Vigia`). Probado con una
  simulación de 50 monitores durante una hora con reloj simulado.
- **Avisos por correo (SMTP con MailKit) y Telegram** (ADR 0005) con bandeja de salida transaccional: los
  avisos se guardan en la misma transacción que el incidente que los provoca y un proceso aparte los envía,
  con reintentos espaciados y abandono a la vista tras seis intentos. Un índice único en la base de datos
  impide dos avisos iguales, varias instancias no envían el mismo aviso y una caída simulada genera un
  único aviso de caída y otro de recuperación. El cierre por mantenimiento no avisa de recuperación. El
  token de Telegram nunca aparece en los errores.
- **API** (ADR 0006): acceso con una contraseña (hash PBKDF2 en la configuración) y JWT de una hora con
  límite contra la fuerza bruta; sesión exigida por defecto en todos los endpoints salvo los dos públicos
  (un test lo comprueba recorriéndolos todos); CRUD de monitores y grupos, ventanas de mantenimiento,
  «probar ahora», y el histórico (resultados, latencia por hora, disponibilidad a 24 h/7/30/90 días, barras
  diarias e incidentes) leído de los agregados.
- **Página de estado pública** (`/api/publico/estado/{slug}`) que solo enseña nombres, estado, disponibilidad
  e incidentes: nunca direcciones, configuraciones ni mensajes de error; un grupo privado y uno inexistente
  son indistinguibles.
- **Tiempo real:** el worker avisa por `NOTIFY` de cada comprobación guardada y la API la reenvía por SignalR
  al panel; la API avisa al worker de cada cambio de monitores en la misma transacción que lo guarda.
- **Corregido:** los clientes HTTP de las comprobaciones recibían de ServiceDefaults un manejador de
  resiliencia (reintentos y cortacircuitos) que falseaba la latencia y los fallos; ahora cada comprobación
  es un único intento.
- **Panel y página de estado en Blazor** (ADR 0007): la página de estado es HTML dibujado en el servidor (sin
  circuito, se refresca sola) y el panel es interactivo: lista de monitores con estado, latencia,
  mini-gráfica de 24 h y disponibilidad, detalle con barras de 90 días, gráfica de latencia p50/p95,
  incidentes, «probar ahora», pausar, editar y borrar con confirmación, grupos y ventanas de mantenimiento.
  Se actualiza sin recargar por SignalR y, si se corta, lo dice y se pone al día al reconectar.
- **Sesión del panel** con cookie cifrada que guarda el token de la API (el navegador nunca lo ve) y caduca
  con él; salir es un `POST` con protección antifalsificación y la redirección tras entrar solo admite rutas
  propias.
- **Accesibilidad:** el estado siempre con palabra y forma (y las caídas con trama), gráficas en SVG propio
  con descripción y tabla de datos, etiquetas y ayudas enlazadas en los formularios, anuncios para lectores
  de pantalla, claro y oscuro, móvil sin desplazamiento horizontal.
- **Cálculo de la disponibilidad** sobre el tiempo pasado en cada estado, con el mantenimiento y el
  tiempo desconocido fuera, sin datos en lugar de un 100 % inventado y con el porcentaje truncado, no
  redondeado hacia arriba; percentiles p50 y p95 de la latencia.

