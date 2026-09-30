# Puesta en producción

Esta guía deja Vigía funcionando en un **VPS** (un servidor alquilado) con HTTPS, copias de seguridad y despliegue automático.
Sirve para un dominio propio y, si **no tienes dominio**, también: en ese caso se usa una dirección gratuita de
[sslip.io](https://sslip.io) que apunta a la IP de tu servidor (más abajo). Las decisiones de diseño están en el
[ADR 0008](adr/0008-produccion-con-compose.md).

Todo lo que sigue se ha probado en local con Docker (las tres imágenes, el arranque ordenado, el acceso a través de Caddy, las
copias y la restauración) salvo lo que necesita un servidor real o tus cuentas (Ansible contra la máquina, los certificados
de Let's Encrypt y el flujo de GitHub Actions). Si algo de esas partes falla a la primera, es lo primero que hay que mirar.

```
Internet ──443──▶ Caddy ──▶ web ──▶ api ──▶ PostgreSQL ◀── worker ──▶ los servicios que vigilas
(solo esto se ve)   (HTTPS)  (panel + página)            (comprobaciones, avisos)
```

## 1. Contratar el servidor

Cualquier VPS con Linux sirve; lo recomendable es uno **Debian 12 o Ubuntu 24.04** de **2 GB de memoria y 2 vCPU** (Hetzner, por
ejemplo, tiene opciones de unos 4-5 € al mes; si eliges una máquina ARM, las imágenes también se construyen para ARM).

1. Al crearlo, **añade tu clave SSH pública** (si no tienes: `ssh-keygen -t ed25519`). Así nunca hará falta una contraseña.
2. Anota su **IP pública** (por ejemplo `203.0.113.7`).
3. No actives nada más: el resto lo hace el playbook.

## 2. Elegir la dirección web

- **Con dominio propio:** crea un registro `A` (y `AAAA` si tienes IPv6) de un subdominio, p. ej. `estado.tudominio.com`, que apunte a
  la IP. Esa será tu `DOMINIO`.
- **Sin dominio:** usa la IP con guiones y `.sslip.io`. Para `203.0.113.7` es `203-0-113-7.sslip.io`. Ese nombre ya apunta a tu
  servidor y Let's Encrypt emite certificados para él sin más. Limitaciones: depende de un servicio gratuito de terceros
  (si no contestara, el certificado no se renovaría) y comparte los límites de Let's Encrypt con todos los que lo usan. Sirve
  perfectamente para empezar; el día que tengas dominio, cambias `DOMINIO` en `.env` y reinicias.

## 3. Preparar el servidor (una vez)

Desde tu ordenador, con [Ansible](https://docs.ansible.com/) (en Windows, desde WSL o con el contenedor de abajo):

```bash
cd infra/ansible
cp inventario.ejemplo.ini inventario.ini         # pon la IP de tu servidor
ansible-galaxy collection install -r requisitos.yml
ssh-keygen -t ed25519 -f vigia_despliegue -N ""  # el par de claves del despliegue (no lo reutilices para otra cosa)
export VIGIA_CLAVE_PUBLICA="$(cat vigia_despliegue.pub)"
ansible-playbook -i inventario.ini preparar-servidor.yml
```

Sin instalar Ansible (con Docker):

```bash
docker run --rm -it -v "$PWD/infra/ansible:/a" -v "$HOME/.ssh:/root/.ssh:ro" -e VIGIA_CLAVE_PUBLICA="$(cat vigia_despliegue.pub)" \
  python:3.12-slim sh -c "pip install -q ansible-core && cd /a && ansible-galaxy collection install -r requisitos.yml && ansible-playbook -i inventario.ini preparar-servidor.yml"
```

Esto deja el servidor así (y es **repetible**: si algún día lo pierdes, vuelves a ejecutarlo en uno nuevo):

- Actualizaciones de seguridad automáticas.
- Cortafuegos con solo los puertos 22, 80 y 443; `fail2ban` para SSH.
- SSH **solo con clave** y **sin entrar como root**.
- Docker y Docker Compose.
- Un usuario `vigia` (sin contraseña, solo con la clave de despliegue) que ejecuta la aplicación.
- Una copia de seguridad cada noche (03:15) y una **prueba de restauración cada domingo**.

> **Ojo:** tras este paso ya no se puede entrar como `root`. Entra como `vigia` (`ssh -i vigia_despliegue vigia@IP`), que tiene `sudo`.
> Guarda `vigia_despliegue` (la clave privada) en un sitio seguro: si la pierdes, pierdes el acceso por SSH (tendrías que usar la
> consola de emergencia de tu proveedor).

## 4. Crear los secretos del servidor

Los secretos viven **solo en el servidor**, en `/opt/vigia/.env`, y no pasan nunca por el repositorio.

```bash
ssh -i vigia_despliegue vigia@IP
cd /opt/vigia
# Si todavía no has desplegado, trae el modelo de .env (o cópialo a mano desde deploy/.env.example):
curl -fsSLo .env https://raw.githubusercontent.com/<tu-usuario>/vigia/main/deploy/.env.example
chmod 600 .env
```

Rellena el fichero (`nano .env`). Lo imprescindible:

| Variable | Qué poner |
|---|---|
| `DOMINIO` | Tu dominio o `203-0-113-7.sslip.io` (paso 2) |
| `PROPIETARIO` | Tu usuario de GitHub **en minúsculas** |
| `POSTGRES_PASSWORD` | `openssl rand -base64 32` |
| `ACCESO_CLAVE_JWT` | `openssl rand -base64 48` |
| `ACCESO_HASH` | El **hash** de la contraseña del panel (abajo) |

El hash de la contraseña del panel se genera en tu ordenador (la contraseña no se guarda en ninguna parte):

```bash
dotnet run --project src/Vigia.Api -- hash-contrasena     # te pide la contraseña y escribe el hash
```

Pega el resultado en `ACCESO_HASH`. **No** uses la contraseña de desarrollo (`vigia-local`).

## 5. Publicar las imágenes

Las imágenes se construyen en GitHub Actions cuando publicas una versión. Con el repositorio ya subido a GitHub:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

El flujo `publicar` construye `vigia-api`, `vigia-web` y `vigia-worker` (para amd64 y arm64) y las sube a
`ghcr.io/<tu-usuario>/`. La primera vez, en GitHub → tu perfil → *Packages*, abre cada una y ponla en **público** (*Package settings
→ Change visibility*) para que el servidor pueda bajarla sin iniciar sesión.

## 6. Primer despliegue

```bash
cd infra/ansible
ansible-playbook -i inventario.ini desplegar.yml -e version=1.0.0 --user vigia --private-key vigia_despliegue
```

(o, sin Ansible, por SSH: copia `deploy/docker-compose.yml`, `Caddyfile` y los `.sh` a `/opt/vigia` y ejecuta
`docker compose pull && docker compose up -d`). Al terminar, abre `https://<tu DOMINIO>/entrar`. La primera vez tarda unos
segundos más: Caddy pide el certificado.

Crea un grupo con página pública y tus primeros monitores; la página de estado queda en `https://<tu DOMINIO>/estado/<grupo>`.

## 7. Despliegue automático en cada versión (opcional)

Con esto, cada `git tag vX.Y.Z` construye y despliega solo. En GitHub → *Settings*:

- **Secrets and variables → Actions → Variables:** `DESPLEGAR` = `true`, `VIGIA_SERVIDOR` = la IP o el dominio (y `VIGIA_SSH_PUERTO` si no es el 22).
- **Secrets:** `VIGIA_SSH_CLAVE` = el contenido de `vigia_despliegue` (la clave **privada**), y `VIGIA_SSH_HUELLA` = la salida de
  `ssh-keyscan -t ed25519 IP` (así el flujo solo se conecta a tu servidor y nunca a «lo que conteste»).
- **Environments → produccion:** puedes exigir una aprobación manual antes de cada despliegue.

## 8. Que el monitor se vigile a sí mismo

Si Vigía se cae, nadie avisa. El flujo `latido` comprueba cada 10 minutos, **desde fuera**, que la página de estado responde; si no,
falla y GitHub te escribe. Actívalo definiendo la variable del repositorio `URL_ESTADO` = `https://<tu DOMINIO>/estado/<grupo>`.

## 9. Avisos por correo y Telegram

Se configuran en `/opt/vigia/.env` (cada canal solo se usa si está completo) y se aplican con `docker compose up -d`:

- **Correo:** `CORREO_SERVIDOR`, `CORREO_PUERTO` (587), `CORREO_USUARIO`, `CORREO_CONTRASENA`, `CORREO_REMITENTE` y al menos `AVISOS_CORREO_0`.
  Sirve cualquier SMTP (el de tu proveedor de correo, por ejemplo). **No** uses la contraseña principal de tu cuenta si existen contraseñas de aplicación.
- **Telegram:** crea un bot hablando con [@BotFather](https://t.me/BotFather) (te da el token → `TELEGRAM_TOKEN`), escríbele un mensaje
  cualquiera y abre `https://api.telegram.org/bot<TOKEN>/getUpdates` para ver tu `chat.id` → `TELEGRAM_CHAT_0`.

## 10. Copias de seguridad

- **Cada noche** se hace un volcado comprimido de la base de datos en `/var/backups/vigia` (14 días de rotación) y se **verifica** que es legible.
- **Cada domingo** `probar-restauracion.sh` restaura la última copia en una base temporal y comprueba que tiene datos: una copia
  que nunca se ha restaurado es una esperanza, no una copia. Si falla, `systemctl status vigia-prueba-restauracion` lo dice.
- **Una copia en el mismo servidor no protege de perder el servidor.** Copia fuera: define `DESTINO_RSYNC` (p. ej. `usuario@otra-maquina:/copias/vigia/`)
  en el servicio `vigia-copia` (`sudo systemctl edit vigia-copia` → `Environment=DESTINO_RSYNC=...`), o baja las copias periódicamente con `rsync`.
- Las copias contienen la configuración, los incidentes y los agregados; el detalle de cada comprobación solo se conserva 14 días (ADR 0004).

**Restaurar** (sustituye la base de datos actual; para la aplicación mientras dura):

```bash
cd /opt/vigia && ./restaurar.sh /var/backups/vigia/vigia-20261015T031500Z.dump
```

## 11. Operación del día a día

| Quiero… | Comando (en `/opt/vigia`) |
|---|---|
| Ver el estado | `docker compose ps` |
| Ver los registros | `docker compose logs -f worker` (o `api`, `web`, `caddy`) |
| Actualizar | publicar un tag nuevo, o `sed -i 's/^VERSION=.*/VERSION=1.0.1/' .env && docker compose pull && docker compose up -d` |
| Volver a la versión anterior | cambiar `VERSION` a la anterior y repetir lo de arriba |
| Cambiar la contraseña del panel | nuevo hash en `ACCESO_HASH` y `docker compose up -d api` (las sesiones abiertas caducan solas en una hora) |
| Cambiar de dominio | `DOMINIO` en `.env` y `docker compose up -d caddy` |
| Probar la copia de seguridad ya | `./backup.sh && ./probar-restauracion.sh` |

**Monitores de ping (ICMP):** el worker recibe la capacidad `NET_RAW` para poder hacer pings sin ser root. Si tu proveedor filtra ICMP saliente,
esos monitores fallarán aunque el destino esté bien (la causa aparece en el mensaje del monitor).

**Vigilar tu propia red:** por defecto Vigía se niega a conectarse a direcciones privadas (protección contra SSRF, ADR 0002). Un monitor de una
dirección interna necesita marcar «Permitir direcciones de la red privada», y aun así el servidor solo verá lo que el VPS pueda alcanzar.

## Lista de comprobación de seguridad

- [ ] `ACCESO_HASH` propio (no el de desarrollo) y `ACCESO_CLAVE_JWT` largo y aleatorio.
- [ ] `.env` con permisos `600` y fuera de cualquier repositorio.
- [ ] Solo los puertos 22, 80 y 443 abiertos (`sudo ufw status`).
- [ ] Entrar por SSH como `root` o con contraseña falla (`ssh root@IP` debe rechazarte).
- [ ] `https://<tu DOMINIO>/health` devuelve 404 (la salud no se ve desde fuera) y la API no contesta desde fuera.
- [ ] La última copia de seguridad es de hace menos de un día y la prueba de restauración del domingo salió bien.
- [ ] Las copias se guardan **también fuera** del servidor.
- [ ] El flujo `latido` está activo.
