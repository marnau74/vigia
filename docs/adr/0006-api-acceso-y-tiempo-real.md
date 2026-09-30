# 0006 · API: acceso, página pública y tiempo real

- **Estado:** aceptada
- **Fecha:** 2026-09-30

## Contexto

La API tiene tres públicos muy distintos: quien administra (que puede crear monitores que hacen
peticiones de red), cualquier persona de internet (que mira la página de estado) y el panel (que quiere
enterarse de las comprobaciones sin recargar). Y debe hablar con el worker sin que ninguno dependa de
que el otro esté arrancado (ADR 0001).

## Decisión

### Un solo usuario, sin tabla de usuarios

Vigía se ofrece como una herramienta de una persona o un equipo pequeño que comparte acceso: no hay
usuarios ni organizaciones. Una tabla de usuarios, registro, recuperación de contraseña y roles serían
cientos de líneas (y de superficie de ataque) sin ningún beneficio. El acceso es:

- Una contraseña cuyo **hash** (PBKDF2 con sal, el de ASP.NET Core Identity) va en la configuración
  (`Acceso__HashContrasena`). La contraseña nunca se guarda; el hash se genera con
  `dotnet run --project src/Vigia.Api -- hash-contrasena`. Sin hash configurado, no se puede entrar y la
  API lo dice claramente (503) en lugar de dejar la puerta abierta.
- `POST /api/acceso` devuelve un **JWT de una hora** firmado con HMAC-SHA256. Sin refresco: pasada la hora
  se vuelve a entrar. Con un único usuario, un refresh token añade estado y riesgo a cambio de evitar una
  contraseña por hora.
- **La API exige sesión por defecto** (política de respaldo): un endpoint nuevo es privado salvo que se
  marque de forma explícita como público. Un test recorre todos los endpoints registrados y comprueba que
  solo los dos públicos (acceso y página de estado) responden sin token, de modo que un endpoint olvidado
  no puede quedar abierto sin que falle un test.
- La validación del token fija el emisor, la audiencia, la firma y **solo el algoritmo HS256** (un
  token sin firma, `alg: none`, o con otro algoritmo no pasa; hay test de cada caso). La vigencia se
  comprueba con el reloj inyectado.
- **Fuerza bruta:** cinco intentos por minuto y cliente en `/api/acceso`; el sexto da 429 aunque la
  contraseña sea la buena. Todas las contraseñas erróneas (vacía, mal escrita) dan el mismo mensaje.
- La clave de firma (mínimo 32 caracteres) es obligatoria: fuera de desarrollo la API **no arranca** sin
  ella. En desarrollo se genera una aleatoria al arrancar.
- SignalR no puede mandar cabeceras desde el navegador, así que admite el token en la dirección
  (`access_token`) **solo para su hub**; un test comprueba que ese atajo no funciona en el resto de la API.

### La página de estado pública enseña lo mínimo

`/api/publico/estado/{slug}` no pide sesión y la puede ver cualquiera, así que devuelve únicamente
nombres de servicio, estado, disponibilidad, barras de 90 días y cuándo hubo incidentes. **No** devuelve
direcciones ni configuraciones (una URL interna es información para un atacante), identificadores
internos ni mensajes de error (la causa de un fallo puede contener direcciones, puertos o credenciales
que un servicio vuelca en sus errores). Un test siembra todo eso y comprueba que no aparece en la
respuesta.

- Un grupo **privado y uno inexistente son indistinguibles** (mismo 404, mismo cuerpo): no se revela
  qué grupos existen. Un identificador con forma inválida ni llega a la base de datos.
- El estado general es el peor de los servicios *confirmados*: un servicio «sospechoso» (un solo fallo) no
  cambia la página pública, que solo habla de caídas confirmadas; y sin datos dice «sin datos», nunca un
  «operativo» inventado.
- Se sirve con `Cache-Control: public, max-age=30` y un límite de 120 peticiones por minuto y cliente:
  cualquiera puede llamarla y un estado con 30 segundos de retraso no le importa a nadie.

### El panel lee agregados, no la tabla grande

Las disponibilidades (24 h, 7, 30 y 90 días) y las barras diarias salen de los agregados por hora y por
día (ADR 0004) sumados **en la base de datos** (una fila por monitor), y se calculan con el mismo código de
dominio que todo lo demás, truncando en lugar de redondear (99,995 % se enseña como 99,99 %, nunca como
100 %). Las horas y los días sin datos son huecos, no ceros ni cienes. A cambio, las cifras van con hasta
una hora de retraso (los agregados se calculan al cerrar cada hora).

El detalle de cada comprobación se consulta como mucho por tramos de 14 días, con un límite de 5.000
filas por petición, porque es lo que se conserva (ADR 0004) y para que ninguna petición pueda pedir
millones de filas.

### API y worker se hablan por PostgreSQL

Ninguno llama al otro. Se enteran por `LISTEN/NOTIFY` de la base de datos que ya comparten:

- **De la API al worker** (`monitores_cambiados`): al crear, modificar, pausar o borrar un monitor o una
  ventana de mantenimiento, la API guarda el cambio **y emite el aviso en la misma transacción**; como
  un `NOTIFY` solo se entrega al confirmar, el worker nunca recarga antes de que el cambio se pueda leer, y
  un cambio rechazado no avisa a nadie.
- **Del worker a la API** (`comprobaciones_guardadas`): tras guardar cada comprobación, el worker manda un
  JSON de unos 150 bytes (monitor, momento, si fue bien, latencia, estado y si abrió o cerró un incidente).
  La API lo reenvía por **SignalR** a los paneles conectados. Un aviso perdido solo significa que el panel se
  entera en la siguiente comprobación: lo guardado nunca depende de esto.
- Entre la escucha y el envío hay una cola acotada que, si los paneles fueran más lentos que las
  comprobaciones, descarta lo más antiguo: al panel le interesa lo último, no una cola de lo que ya pasó.
  Una carga que no se entiende (de otra versión, corrupta) se ignora sin tumbar el servicio.
- La escucha se reconecta sola si se cae; al volver, el panel debe refrescar lo que haya podido perder.

### Los clientes HTTP de las comprobaciones, sin reintentos automáticos

La plantilla de Aspire añade a **todos** los clientes HTTP un manejador de resiliencia (reintentos,
cortacircuitos, tiempos propios). Para un monitor es un error grave: un reintento oculto falsea la
latencia, cuenta como una comprobación lo que fueron tres y un cortacircuitos dejaría de comprobar un
servicio justo cuando está caído. Se descubrió porque «probar ahora» contra un destino bloqueado se
quedaba colgado esperando un reintento. Se quitó el valor por defecto de `ServiceDefaults` (quien
quiera resiliencia la pide en su propio cliente) y un test comprueba que una web que responde 503 recibe
**una sola** petición.

### Errores

Todos los errores son `application/problem+json` con un campo `codigo` estable que se puede programar
contra él. Un cuerpo malformado es un 400 (no un 500 que llene los registros de errores que no lo son), y
una configuración de monitor que no encaja con su tipo es siempre un 400 con un mensaje legible.

## Alternativas descartadas

- **Usuarios con ASP.NET Core Identity.** Mucha superficie para un único usuario.
- **Basic auth o una clave de API fija.** Sin caducidad ni forma de cerrar sesión; y la clave viajaría
  en cada petición y acabaría en registros.
- **Que la API llame al worker por HTTP (o al revés).** Acopla los procesos: si uno está caído, el otro
  falla; la base de datos ya es el punto común y siempre está.
- **Sondear la base de datos cada pocos segundos** para saber si hay algo nuevo: latencia y carga sin
  necesidad teniendo `LISTEN/NOTIFY`.
- **Calcular la disponibilidad al vuelo sobre los resultados.** Con meses de datos sería leer millones de
  filas por cada vez que alguien abre el panel.

## Consecuencias

- Una sola contraseña compartida: si se filtra, se cambia el hash y se reinicia; las sesiones en curso
  caducan solas en una hora.
- Detrás de un proxy inverso (fase 7), el límite por cliente necesitará leer la dirección real del cliente
  (`X-Forwarded-For`), o todos los visitantes contarían como uno.
- El panel verá las cifras de disponibilidad con hasta una hora de retraso, y el estado y la latencia en
  tiempo real.
