# Decisiones de diseño

Cada decisión con consecuencias se escribe aquí, con su contexto, las alternativas descartadas y lo
que se pierde al elegir.

| | Decisión | Estado |
|---|---|---|
| [0001](0001-worker-separado-de-la-api.md) | El worker va separado de la API | Aceptada |
| [0002](0002-proteccion-contra-ssrf.md) | Protección contra SSRF en las comprobaciones | Aceptada |
| [0003](0003-maquina-de-estados-y-disponibilidad.md) | Máquina de estados de los monitores y cálculo de la disponibilidad | Aceptada |
| [0004](0004-datos-particionados-y-planificador.md) | Datos particionados por mes, agregados y planificador del worker | Aceptada |
| [0005](0005-avisos-con-bandeja-de-salida.md) | Avisos por correo y Telegram con bandeja de salida | Aceptada |
| [0006](0006-api-acceso-y-tiempo-real.md) | API: acceso con una contraseña, página pública mínima y tiempo real por PostgreSQL | Aceptada |
| [0007](0007-panel-blazor.md) | Panel y página de estado en Blazor: SSR público, panel interactivo, sesión y accesibilidad | Aceptada |
