# 0008 · Producción en un VPS con Docker Compose y Caddy

- **Estado:** aceptada
- **Fecha:** 2026-09-30

## Contexto

Vigía tiene que estar **siempre encendido** (un monitor que duerme no vigila) y guardar datos que importan, así que no encaja en los
planes gratuitos que se apagan por inactividad. Quien lo despliegue es una persona sola, sin un equipo de operaciones, y
puede que sin dominio propio. Hay que repartir el riesgo entre lo que se expone a internet (mínimo), lo que guarda datos (aislado)
y lo que se puede reconstruir (todo lo demás).

## Decisión

### Un VPS con Docker Compose, no Kubernetes ni una plataforma gestionada

Cinco contenedores (PostgreSQL, worker, API, web y Caddy) en una sola máquina, descritos en un único fichero. Es suficiente para
cientos de monitores, se entiende entera de un vistazo, cuesta unos euros al mes y se reconstruye desde cero con dos comandos.
**No hay alta disponibilidad**: si el servidor cae, Vigía cae hasta que vuelve; para un monitor personal es el trato correcto, y por eso
existe un latido externo (abajo) que avisa de ello.

### Qué se ve desde internet: solo Caddy

- Solo Caddy publica puertos (80 y 443). La base de datos, el worker, la API y la web viven en una red interna que **ni siquiera tiene
  salida a internet**; se añade una segunda red con salida solo a quien la necesita (el worker para comprobar servicios, la API para
  «probar ahora» y Caddy para los certificados).
- **La API no se expone**: la web es su único cliente y habla con ella por la red interna. La página de estado y el panel pasan por la web.
- `/health` y `/alive` se responden con 404 desde fuera: son para Docker.
- Cabeceras de seguridad en Caddy (HSTS, CSP estricta que solo permite lo propio, sin marcos, sin referer…), verificadas con el panel real:
  el circuito de Blazor funciona por WebSocket sin infringir la política.
- Los contenedores se ejecutan **sin root** y con `no-new-privileges`. La única capacidad extra es `NET_RAW` en el worker, para los monitores de ping.

### HTTPS sin dominio: sslip.io

Let's Encrypt necesita un nombre, no una IP. Sin dominio propio, `203-0-113-7.sslip.io` resuelve a `203.0.113.7` y permite obtener un
certificado real sin comprar nada. Es un servicio gratuito de terceros con límites compartidos, así que se presenta como una solución
para empezar; el dominio es **una sola variable** (`DOMINIO`) y cambiar de una cosa a otra es editarla y reiniciar Caddy.

### Secretos: en el servidor, nunca en el repositorio

Las contraseñas, la clave de firma y los tokens viven en `/opt/vigia/.env` (permisos 600), que crea quien despliega. El repositorio solo
tiene `.env.example` con los nombres. La contraseña del panel se guarda como **hash** (nunca en claro). No hay un gestor de secretos porque
para una sola máquina y una sola persona sería más superficie que protección.

### Reconstruir es un comando (Ansible)

Un playbook **idempotente** deja un servidor vacío listo: actualizaciones automáticas, cortafuegos con solo 22, 80 y 443, `fail2ban`, SSH
solo con clave y sin root, Docker, el usuario sin contraseña que ejecuta la aplicación y las copias de seguridad. Si se pierde el servidor,
se ejecuta contra uno nuevo. El usuario de la aplicación pertenece al grupo `docker`, que equivale a administrador del servidor: por eso
no tiene contraseña, solo entra con una clave dedicada, y se le da `sudo` sin contraseña (no amplía lo que ya puede hacer quien tenga esa
clave y evita tener que abrir el acceso de root).

### Despliegue por versión

Un tag `vX.Y.Z` construye las tres imágenes (amd64 y arm64), las publica en GitHub Container Registry y, **solo si se activa
explícitamente** (variable `DESPLEGAR`), las despliega por SSH con la huella del servidor fijada en un secreto. Volver atrás es cambiar
`VERSION`. Las migraciones las aplica el worker al arrancar y la API y la web esperan a que esté sano.

### Copias de seguridad que se prueban

Un volcado comprimido cada noche, **verificado** antes de darlo por bueno y con rotación de 14 días; y **una restauración de prueba
cada semana** en una base temporal, porque una copia que nunca se ha restaurado no se sabe si sirve. Las copias en el mismo servidor no
protegen de perderlo, así que hay un destino `rsync` opcional para sacarlas fuera, al que solo se añaden copias: replicar también los
borrados haría que perder las copias locales borrase las de fuera. Restaurar es un script que para la aplicación,
sustituye la base de datos y la arranca.

### El monitor se vigila a sí mismo

Un flujo programado de GitHub Actions comprueba desde fuera cada diez minutos que la página de estado responde y, si no, falla y GitHub
avisa por correo. Es lo único que detecta que se ha caído el propio Vigía (un monitor no puede avisar de su propia caída). Viene
desactivado hasta que se configura, y GitHub lo desactiva tras 60 días sin actividad en el repositorio: hay que reactivarlo entonces.

### Lo que hace falta detrás de un proxy (y se hizo)

- La cookie de sesión es `Secure` fuera de desarrollo.
- Las claves de cifrado de la cookie y de los formularios se guardan en un **volumen**: sin eso, cada reinicio cerraría todas las sesiones.
  Al probar el despliegue real salió un 500 porque el volumen nuevo era de root y la web no es root; el directorio pertenece ahora al usuario sin privilegios.
- La IP real del cliente llega por `X-Forwarded-For` (solo se confía en ella porque a la web y a la API únicamente les llega la red interna) y la web
  se la pasa a la API en los dos endpoints sin sesión: sin eso, **todo el mundo compartiría un único límite de intentos** y una sola persona podría
  impedir entrar a las demás, o tumbar la página de estado con 120 peticiones por minuto.

## Alternativas descartadas

- **Planes gratuitos con suspensión** (Render, etc.): el worker dejaría de comprobar.
- **Kubernetes / Nomad:** para cinco contenedores en una máquina, más complejidad de operar que problema resuelto.
- **Exponer la API detrás de Caddy:** ampliaría lo atacable sin que nadie la necesite desde fuera.
- **Certificados autofirmados o HTTP sin más:** la cookie de sesión no viajaría con `Secure` y los navegadores avisarían del riesgo.
- **Guardar los secretos cifrados en el repositorio (SOPS, etc.):** más piezas y una clave maestra que custodiar, para un único servidor.

## Consecuencias

- Una máquina es un punto único de fallo: las copias fuera del servidor y el latido externo son la red de seguridad, no la redundancia.
- Quien despliegue necesita una cuenta en GitHub (para las imágenes y el flujo) y un proveedor de VPS; nada de esto se puede hacer sin ellas.
- Actualizar es bajar imágenes nuevas y reiniciar: hay unos segundos de corte por versión (los datos no se pierden y el worker retoma el calendario).
