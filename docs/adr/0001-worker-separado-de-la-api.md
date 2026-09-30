# 0001 · El worker va separado de la API

- **Estado:** aceptada
- **Fecha:** 2026-09-30

## Contexto

Vigía tiene dos trabajos muy distintos:

- **Vigilar:** un proceso que debe estar siempre encendido, ejecutando comprobaciones a sus horas,
  aunque nadie mire el panel.
- **Servir:** una API y un panel que responden a personas, con su ciclo de despliegues, reinicios y picos.

Si todo viviera en un único proceso, desplegar una corrección de la interfaz **interrumpiría las
comprobaciones**: se perderían resultados durante el reinicio y, peor, una caída real justo entonces
pasaría desapercibida. Además, un fallo en un endpoint (una excepción, una fuga de memoria) podría
llevarse por delante al planificador.

## Decisión

Dos procesos, con una responsabilidad cada uno:

- **`Vigia.Worker`** planifica y ejecuta las comprobaciones, guarda los resultados, envía los avisos
  y hace la agregación y la retención. No depende de que la API esté viva.
- **`Vigia.Api`** ofrece el CRUD de monitores, el histórico, los agregados y el tiempo real. No
  ejecuta ninguna comprobación.

Se comunican **por la base de datos** (los resultados y los monitores viven en PostgreSQL) y por
**notificaciones puntuales** (por ejemplo, «este monitor ha cambiado, recárgalo»). No se llaman por
HTTP para lo esencial: si la API está caída, el worker sigue vigilando.

El worker es, por dentro, un servicio web mínimo: expone solo `/health` y `/alive`, para que Aspire,
Docker y el proxy sepan si está vivo. Los dos procesos no se referencian entre sí; un test de
arquitectura lo comprueba.

## Alternativas descartadas

- **Todo en un proceso** (planificador como `BackgroundService` dentro de la API): más sencillo de
  desplegar, pero acopla el ciclo de vida de la vigilancia al de la interfaz, que es justo lo que
  no se quiere.
- **Una cola de mensajes externa** (RabbitMQ, etc.) entre los dos: otro servicio que operar para un
  volumen de unos cientos de mensajes por minuto. PostgreSQL ya está y basta.
- **Que la API llame al worker por HTTP:** si el worker se reinicia justo entonces, se pierde la
  orden; con la base de datos como punto de encuentro, no.

## Consecuencias

- Hay dos procesos que desplegar y vigilar, en lugar de uno. Docker Compose lo hace trivial.
- Los cambios de configuración de un monitor llegan al worker por notificación, así que hay que
  diseñar esa recarga con cuidado (fase 3).
- El tiempo real hacia el panel pasa por la API: el worker publica los cambios de estado y la API los
  reenvía por SignalR (fase 5).
