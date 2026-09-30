# 0004 · Datos de las comprobaciones (particiones, agregados, retención) y planificador del worker

- **Estado:** aceptada
- **Fecha:** 2026-09-30

## Contexto

Un monitor de 60 segundos genera 1.440 comprobaciones al día; 500 monitores, 720.000 filas al día y más
de 260 millones al año. Nadie necesita el detalle de cada comprobación de hace un año: se necesita el
detalle reciente (para diagnosticar) y el resumen histórico (para la disponibilidad y las gráficas).
Además, alguien tiene que decidir *cuándo* toca cada comprobación, sin lanzarlas todas a la vez, sin
solaparlas y sin abrir miles de sockets simultáneos.

## Decisión

### Los resultados van en una tabla particionada por mes

`resultados` es una tabla particionada (`PARTITION BY RANGE (momento)`), una tabla física por mes.

- **Borrar es instantáneo.** La retención (14 días de detalle) se cumple con `DROP TABLE` de las
  particiones de meses enteros vencidos, que no recorre filas ni deja huecos ni exige un `VACUUM`
  largo. En la partición que queda partida por el límite se borran las filas antiguas con un `DELETE`
  normal (pocas). Un test borra una partición de 200.000 filas en menos de 3 segundos.
- **Las consultas por fecha leen poco.** «Las últimas 24 horas» solo toca el mes actual y, a veces, el
  anterior (un test lo comprueba en el plan de ejecución).
- **Índices pequeños:** un btree `(monitor_id, momento DESC)` para «los últimos resultados de este
  monitor» y un BRIN sobre `momento`, de unos kilobytes por partición, que sirve porque las filas se
  insertan en orden de tiempo.
- **La clave primaria es `(id, momento)`**, porque PostgreSQL exige que incluya la columna de partición.
- **No hay partición por defecto.** Una fila sin partición debe fallar (error 23514) en lugar de
  acumularse en un cajón desastre que luego impide crear la partición de ese mes. Por eso el worker crea
  las particiones por adelantado (mes anterior, actual y tres más) al arrancar y una vez al día, y un
  fallo del worker durante meses no deja sin sitio donde escribir.

EF Core no sabe expresar el particionado, así que esa tabla se crea con una migración escrita a mano y
el modelo la excluye de las migraciones automáticas.

### Tres niveles de detalle

| Nivel | Se conserva | Para qué |
|---|---|---|
| Resultado de cada comprobación | 14 días | Diagnosticar qué pasó y cuándo |
| Agregado por hora | 90 días | Gráficas y la disponibilidad de la página de estado |
| Agregado por día | Siempre | Tendencias e histórico largo |

Los agregados se calculan **en C# con el mismo dominio** que usa todo lo demás (`TiemposPorEstado`,
`CalculadoraDisponibilidad`, `Estadistica`) en lugar de con SQL: hay una sola definición de
«disponibilidad» y no dos que puedan diferir. El agregado guarda los segundos en cada estado (no un
porcentaje), que se pueden sumar sin perder exactitud: el día es la suma de sus horas.

- La latencia (media, p50, p95) se calcula **solo con las comprobaciones correctas y fuera de
  mantenimiento**: un fallo por tiempo agotado no dice nada de lo rápido que va el servicio.
- Del día se guarda el **peor p95 de sus horas**, no un p95 recalculado: un percentil de percentiles no
  significa nada, y lo que interesa es «¿cuál fue la peor hora?».
- Agregar una hora es idempotente (se vuelve a calcular y se sobrescribe), así que si el worker se cae
  simplemente retoma lo pendiente. Una hora solo se agrega cuando ha terminado.

### El estado se guarda con lo que produce, en una transacción

Cada comprobación guarda de una vez el resultado, los cambios de estado, el incidente y la fila de
seguimiento. Así nunca hay un incidente abierto sin su cambio de estado, ni un estado sin su
resultado, y si falla el guardado no queda nada a medias. Un índice único parcial garantiza en la
base de datos que un monitor no tiene dos incidentes abiertos, aunque el código se equivocara.

### El planificador: cola de prioridad, canal acotado y trabajadores fijos

- **`ColaDeVencimientos`** es una cola de prioridad por instante de vencimiento (sacar el siguiente es
  O(log n) y se sabe exactamente cuánto dormir). No lee el reloj: recibe la hora, así que se prueba sin
  esperar. Un monitor sacado de la cola no vuelve a entrar hasta que termina (**sin solapes**); el
  siguiente vencimiento se cuenta desde el previsto y no desde cuándo acabó (**ritmo fijo**), y si el
  worker se quedó atrás se salta lo perdido en lugar de recuperarlo a ráfagas; la primera comprobación
  de cada monitor se desplaza una fracción fija de su intervalo derivada de su id (**reparto**), para
  que 500 monitores de un minuto no se lancen todos a la vez.
- **Un bucle entrega los vencidos a N trabajadores por un canal con límite.** El número de
  comprobaciones simultáneas está acotado (20 por defecto) pase lo que pase: con 5.000 monitores no se
  abren 5.000 sockets, y si los trabajadores van más lentos que el ritmo de entrada, el canal lleno
  frena el bucle en lugar de acumular memoria.
- **Un fallo se repite una vez antes de contarlo**, tras una espera corta, salvo que dos comprobaciones
  no quepan en el intervalo o que el destino esté prohibido por la guardia SSRF (repetir no cambiaría
  nada). Un tropiezo puntual de red no debe sumar al contador de fallos seguidos.
- **El seguimiento de cada monitor se guarda en memoria** entre comprobaciones para no leerlo cada vez;
  es seguro porque nunca hay dos comprobaciones simultáneas del mismo monitor. Si guardar falla, esa
  copia se descarta y se recarga de la base de datos.
- **Los cambios de configuración llegan por `LISTEN/NOTIFY`**: la API avisa por el canal
  `monitores_cambiados` y el worker recarga al instante, sin preguntar cada pocos segundos. Un aviso
  perdido no se recupera, así que además hay una recarga periódica (cada minuto) como red de seguridad
  y se recarga al reconectar. Un monitor modificado se comprueba enseguida; uno pausado se retira.
- **El worker prepara la base de datos antes de escribir:** un servicio de arranque aplica las
  migraciones y crea las particiones, y los servicios alojados arrancan en orden.
- **Las decisiones que dependen del resultado** (avisos) cuelgan de `IManejadorDeEventos`, que se
  invoca tras guardar; su fallo se registra pero no invalida la comprobación ya guardada.

### Métricas

Un medidor `Vigia` (OpenTelemetry) publica la duración de las comprobaciones por tipo y resultado, los
incidentes abiertos y los errores internos al guardar.

## Alternativas descartadas

- **Un `DELETE` diario en una tabla normal.** Con cientos de millones de filas es lento, deja la tabla
  llena de huecos y obliga a `VACUUM`. `TimescaleDB` lo resolvería, pero añade una extensión que no
  está en cualquier PostgreSQL gestionado y aquí las particiones nativas bastan.
- **Agregados con SQL puro (`date_trunc` + `percentile_cont`).** Más corto, pero duplica en SQL la
  definición de disponibilidad y la de los percentiles.
- **Un temporizador por monitor (`Task.Delay` en bucle).** Simple, pero con miles de monitores son
  miles de tareas vivas, sin límite de concurrencia ni control del reparto.
- **Consultar la base de datos cada pocos segundos por cambios.** Latencia y carga innecesarias:
  PostgreSQL ya sabe avisar.

## Consecuencias

- Hay un trabajo más que mantener vivo: si el worker estuviera parado meses, faltarían particiones.
  Se mitiga creando tres meses por adelantado.
- El detalle de una comprobación desaparece a los 14 días; para verlo más tiempo hay que subir la
  retención o exportar antes.
- La primera versión del agregado calcula en la aplicación, lo que lee las filas de cada hora; es
  barato con 14 días de datos y se puede pasar a SQL si algún día pesara.
