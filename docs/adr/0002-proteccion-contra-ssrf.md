# 0002 · Protección contra SSRF en las comprobaciones

- **Estado:** aceptada
- **Fecha:** 2026-09-30

## Contexto

Vigía hace peticiones a direcciones que escribe una persona: «vigila esta URL». Es exactamente la
definición de una vulnerabilidad de **SSRF** (*server-side request forgery*): si alguien puede
hacer que el servidor conecte a donde él diga, el servidor se convierte en su intermediario dentro
de una red a la que él no llega. Con Vigía en un VPS, eso significa:

- `http://169.254.169.254/latest/meta-data/` — las credenciales de la máquina en muchas nubes.
- `http://localhost:5432` o `http://10.0.0.5:6379` — bases de datos y cachés internas, sin contraseña.
- Escaneo de la red interna: «¿responde 192.168.1.20:22?» es justo lo que hace un monitor TCP.

No basta con comprobar la URL al guardarla. Hay al menos cuatro maneras de saltarse esa comprobación:

1. **DNS rebinding:** un nombre que resuelve a una IP pública cuando se valida y a una interna
   cuando se conecta.
2. **Redirecciones:** una web pública devuelve `302 → http://169.254.169.254/…`.
3. **IPv4 escrita como IPv6** (`::ffff:127.0.0.1`, NAT64, 6to4) o en formas raras
   (`2130706433`, `0x7f000001`) para pasar una lista que solo mira `127.0.0.1`.
4. **Esquemas inesperados** (`file:`, `gopher:`, `ftp:`) o credenciales en la URL.

## Decisión

Una sola clase, `GuardiaDeDestinos`, decide a qué direcciones se puede conectar, y **todas** las
comprobaciones pasan por ella (HTTP, TLS, TCP, ICMP, DNS y hasta el servidor DNS al que se pregunta).

### Qué hace

- **Resuelve el nombre una sola vez y devuelve las direcciones ya validadas.** Quien conecta usa esas
  direcciones y no vuelve a resolver el nombre: no hay un segundo momento en el que el DNS pueda
  contestar otra cosa. En HTTP esto se logra con `SocketsHttpHandler.ConnectCallback`, que es quien
  abre la conexión; .NET nunca resuelve el nombre por su cuenta.
- **Rechaza todo el destino si una sola de las respuestas es interna.** Un nombre que mezcla
  direcciones públicas e internas es la firma de un ataque; no se «elige la buena».
- **Lista de rangos bloqueados** con motivo legible: loopback, redes privadas, enlace local, los
  metadatos de AWS, GCP, Azure, Oracle y Alibaba, espacio compartido de operador, multidifusión,
  reservadas, documentación, y sus equivalentes IPv6 (incluida `fd00:ec2::254` de AWS).
- **Desempaqueta las IPv4 escritas dentro de IPv6** (`::ffff:`, NAT64 `64:ff9b::/96` y 6to4
  `2002::/16`) y comprueba la de dentro. Las direcciones escritas como número o en hexadecimal las
  normaliza .NET antes de que lleguen a la lista.
- **Las redirecciones las sigue el comprobador, no el cliente HTTP.** Cada salto abre una conexión
  nueva, que pasa otra vez por la guardia; además se comprueba el esquema de cada destino y se
  limita el número de saltos.
- **Solo `http` y `https`, y sin usuario ni contraseña en la URL.**
- **Sin proxy del sistema:** un proxy conectaría al destino por su cuenta, sin pasar por la guardia.
- **La excepción es explícita:** `PermitirRedPrivada` en el monitor (por ejemplo, para vigilar la red de
  casa). Por defecto está prohibido, y quien lo activa lo hace sabiendo lo que abre.

### Cómo se sabe que funciona

- Un test por cada rango, con sus dos extremos y los valores justo fuera del rango.
- Tests de comportamiento con servidores reales en local: un servidor web que redirige a los
  metadatos de la nube, un DNS que contesta distinto la segunda vez, y comprobaciones de que **nunca se
  llegó a abrir la conexión** a la dirección prohibida.
- Pruebas de mutación: se rompe cada defensa a propósito y se comprueba que algún test falla.

## Alternativas descartadas

- **Validar la URL al guardarla:** es la defensa que se salta con DNS rebinding y con redirecciones.
- **Una lista de dominios permitidos:** no sirve para una herramienta cuyo propósito es vigilar lo que
  cada persona escriba.
- **Aislar el worker en una red sin acceso interno** (contenedor sin acceso a nada privado): es la
  mejor defensa en profundidad y se hará en el despliegue (fase 7), pero no debe ser la única: en
  local y en otros despliegues la red interna sí existe.
- **Validar solo con expresiones regulares sobre el texto de la IP:** falla con las formas
  alternativas (`0x7f.1`, IPv6 con IPv4 dentro).

## Consecuencias

- Vigilar un servicio interno exige marcar el monitor a propósito. Es una molestia deliberada.
- **Un nombre con muchas direcciones se valida entero cada vez** (una resolución por conexión). Las
  conexiones HTTP se reciclan cada dos minutos, así que un cambio de DNS también se nota.
- La lista de rangos es estática y hay que revisarla cuando se asignen rangos nuevos (por ejemplo,
  si una nube usa otra dirección de metadatos).
- El medidor de fases HTTP (`dns_ms`, `conexion_ms`) sale gratis de este diseño, porque la
  resolución y la conexión pasan por nuestro código.
