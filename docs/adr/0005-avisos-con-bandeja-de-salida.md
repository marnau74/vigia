# 0005 · Avisos por correo y Telegram con bandeja de salida

- **Estado:** aceptada
- **Fecha:** 2026-09-30

## Contexto

Avisar de una caída es la razón de ser del programa, y tiene tres maneras de salir mal:

1. **No avisar.** El servidor de correo o Telegram pueden estar caídos justo cuando hace falta (a
   menudo por el mismo incidente que se quiere comunicar). Una caída que no avisa es peor que no tener
   monitor, porque da falsa tranquilidad.
2. **Avisar de lo que no pasó.** Enviar el aviso y que después falle el guardado del incidente (o al
   revés) deja un mensaje sin hecho, o un hecho sin mensaje.
3. **Avisar dos veces** (o veinte). Un reinicio del worker a mitad de una caída, dos instancias a la vez
   o un reintento de red que repite una petición producen mensajes duplicados, y quien los recibe
   aprende a ignorarlos.

## Decisión

### Bandeja de salida transaccional

Cuando una comprobación abre o cierra un incidente, los **avisos se guardan en la base de datos en la
misma transacción** que el resultado, el cambio de estado, el incidente y el seguimiento
(`RepositorioSeguimiento.GuardarAsync`). Después, un proceso independiente (`EnviadorDeAvisos`) vacía
la tabla `avisos`. Consecuencias:

- **No hay aviso sin hecho ni hecho sin aviso:** o queda todo guardado o no queda nada (un test fuerza
  un fallo de guardado y comprueba que no queda ni incidente ni aviso).
- **Enviar no bloquea las comprobaciones.** Un SMTP que tarda 30 segundos no retrasa ni una
  comprobación.
- **Un canal caído no pierde el aviso:** se reintenta con esperas crecientes (30 s, 2 min, 10 min,
  30 min, 2 h) y, pasados seis intentos, se **abandona a la vista** (`abandonado = true`, con el último
  error guardado y un log de nivel error) en lugar de insistir eternamente o desaparecer en silencio.
- **Cada canal es independiente.** Si Telegram falla, el correo ya enviado no se repite.

### Un aviso por transición, garantizado por la base de datos

La máquina de estados (ADR 0003) ya emite un único evento por apertura y otro por cierre de incidente,
y un test de diez mil secuencias aleatorias comprueba que los avisos que se derivan no se duplican ni
salen sin motivo. Pero la garantía final no depende del código: un **índice único**
`(incidente_id, tipo, canal, destino)` hace que la base de datos rechace un segundo aviso igual.

Solo avisan dos transiciones: abrir un incidente (caída) y cerrarlo porque el servicio **volvió**
(recuperación). Un fallo aislado, un servicio lento o un mantenimiento no avisan; y un incidente que se
cierra porque empieza una ventana de mantenimiento **no manda aviso de recuperación**, porque el
servicio no se ha recuperado, solo se ha dejado de contar.

### Reclamar con una reserva, sin bloquear

Varias instancias del worker pueden vaciar la bandeja a la vez. Cada una **reclama** un lote con un
único `UPDATE … WHERE id IN (SELECT … FOR UPDATE SKIP LOCKED) RETURNING id` que, además de elegir, aplaza
el próximo intento dos minutos (la *reserva*). El `UPDATE` confirma al instante, así que no se mantiene
ningún bloqueo mientras se habla con el servidor de correo. Si el proceso muere a mitad, el aviso
reservado **vuelve a tocar** al vencer la reserva en lugar de perderse.

### Entrega «al menos una vez», con el texto de ahora

El aviso se marca como enviado **después** de enviarlo. Si el proceso muere justo entre una cosa y la
otra, el aviso se repite al vencer la reserva. Es la elección correcta para una alerta (mejor dos
mensajes que ninguno) y solo puede ocurrir en ese instante; lo que no puede ocurrir es el duplicado
habitual por reinicios o instancias múltiples.

El texto se redacta **al enviar**, no al crear el aviso, con el estado actual del incidente: un
reintento tardío cuenta la duración real de la caída, y la hora va siempre en UTC y diciéndolo, porque
un aviso de madrugada no debe obligar a adivinar la zona horaria. El texto es el mismo para correo y
Telegram (texto plano, sin emojis ni formato).

### Los canales

- **Correo:** MailKit, texto plano, STARTTLS cuando el servidor lo ofrece. En local va a Mailpit.
  Se prueba contra un Mailpit real en contenedor: se envía y se lee de su bandeja.
- **Telegram:** `sendMessage` del bot, con un cliente HTTP **propio y sin reintentos automáticos**:
  repetir un `POST` por la red enviaría el mensaje dos veces (los reintentos los decide la bandeja).
  El token va en la dirección de la petición, así que **ningún error lo incluye**: los mensajes se
  construyen con el estado HTTP y la descripción de Telegram, nunca con la dirección (un test lo
  comprueba también en el caso de no poder conectar). Los mensajes se recortan a 4.096 caracteres.
- Un canal solo cuenta como destino si está **configurado por completo** (destinatarios y servidor;
  chats y token). Sin ningún destino, el worker lo dice al arrancar y los incidentes se guardan sin
  avisos: no se crean avisos que nunca podrían enviarse.

Los destinatarios y los secretos (contraseña SMTP, token del bot) se dan por configuración y variables
de entorno, nunca en un fichero del repositorio.

## Alternativas descartadas

- **Enviar desde el mismo código que registra la comprobación.** Más simple, pero un SMTP lento frena
  las comprobaciones y un fallo entre el envío y el guardado crea avisos huérfanos o pierde avisos.
- **Un bus de mensajes (RabbitMQ, etc.).** Otro servicio que operar para un volumen de unos pocos
  avisos al día; la base de datos ya da transacciones, persistencia y visibilidad.
- **Avisos por monitor (destinatarios en cada monitor).** Más flexible, pero multiplica la
  configuración; la v1 tiene un único equipo de guardia. Queda como ampliación.
- **Deduplicar con una ventana de tiempo** («no repetir el mismo aviso en una hora»). Esconde avisos
  legítimos y no impide un duplicado pasada la ventana; el índice único lo impide siempre y solo
  cuando es realmente el mismo aviso.

## Consecuencias

- Hay una tabla más que vigilar: los avisos abandonados piden revisión manual (se conservan 30 días y
  se ven en el log y en las métricas `vigia.avisos`).
- El aviso de caída no sale en el mismo instante de la comprobación sino dentro del intervalo de
  envío (5 s por defecto): un retraso aceptable a cambio de no perder nunca un aviso.
- Los avisos de la v1 son solo de caída y recuperación; los de caducidad de certificado y cambio de
  DNS (que también estaban previstos) necesitan su propia regla de «una vez por umbral» y quedan
  para una ampliación.
