# 0007 · Panel y página de estado en Blazor

- **Estado:** aceptada
- **Fecha:** 2026-09-30

## Contexto

Hacen falta dos interfaces con necesidades opuestas: una **página de estado pública**, que la ve cualquiera
(rápida, sin JavaScript imprescindible, que funcione aunque el panel esté roto) y un **panel privado** que debe
actualizarse solo cuando el worker guarda una comprobación. Además tiene que ser accesible y verse bien en móvil,
en claro y en oscuro.

## Decisión

### Una aplicación Blazor con dos modos de dibujo

Una sola aplicación (`Vigia.Web`) con **renderizado en el servidor** y dos modos por página:

- **La página de estado pública** es *SSR estático*: el servidor devuelve el HTML ya hecho, sin circuito
  ni WebSocket. Se ve en cualquier navegador, carga rápido y se refresca sola con un `<meta refresh>` de un
  minuto en lugar de mantener una conexión abierta por cada visitante. Pide que no se indexe.
- **El panel privado** es *interactivo* (un circuito de Blazor Server) sin prerenderizado: como solo entra
  quien administra, no compensa renderizar dos veces (y llamar dos veces a la API) por un primer pintado que
  nadie va a indexar.

### La web no conoce la base de datos: solo habla con la API

`Vigia.Web` no referencia los datos, las comprobaciones ni el worker (un test de arquitectura lo
comprueba): todo lo que sabe lo pregunta a la API por HTTP, con los tipos de un proyecto pequeño
`Vigia.Contratos` compartido con ella. Así la API es la única puerta de entrada a los datos y el panel es
solo otro cliente, sustituible. El cliente (`IClienteDeApi`) es una interfaz para poder probar cada página
con una API de mentira, y **convierte todo en un resultado** (valor, error con código o «sesión caducada»):
una API caída, una respuesta con un formato raro o un 500 se ven como un mensaje en la página y no rompen el
circuito.

### Sesión: una cookie cifrada que lleva el token de la API

Al entrar, la web pide el token a la API y lo guarda **dentro de su propia cookie de sesión** (cifrada con
Data Protection, `HttpOnly`, `SameSite=Lax`); cada llamada a la API lo saca del usuario actual. El navegador
nunca ve el token: no va en el HTML, en el almacenamiento local ni en una cabecera que pueda leer un script.
La sesión **caduca con el token** (una hora): el ticket lleva su caducidad y un test avanza el reloj para
comprobar que, pasado el tiempo, la cookie deja de valer aunque el navegador la conserve.

- **Salir es un `POST` con protección antifalsificación**, nunca un enlace: un `GET` que cierre la sesión podría
  dispararse desde una imagen de otra web. Un test comprueba que sin el token se rechaza y que por `GET` no hace nada.
- **Después de entrar solo se vuelve a rutas de la propia web**: `//otra.web`, `/\otra.web` o una dirección
  absoluta se ignoran (redirección abierta). Hay tests con cada variante.
- El formulario de entrada lleva antifalsificación y el límite de intentos de la API (5 por minuto) se
  traduce en un mensaje claro.

### Tiempo real con SignalR, y qué pasa cuando se corta

El panel abre una conexión SignalR contra el hub de la API con el mismo token. Cada mensaje actualiza la fila
o la página sin recargar. Tres decisiones hacen que no engañe:

- **Un corte no pasa desapercibido:** el panel dice «En vivo» o «Sin conexión en vivo» y, al reconectar,
  **vuelve a pedir todo**, porque lo que ocurrió durante el corte no se repite.
- **Una comprobación fallida no enseña como latencia el tiempo que se esperó** (10 s de tiempo agotado no
  son una latencia), y abrir o cerrar un incidente recarga la página para traer la causa y la duración.
- **Sin tiempo real el panel funciona igual**, solo que sin actualizarse solo.

### Gráficas en SVG propio, sin dependencias de JavaScript

Las tres (mini-gráfica de 24 h, latencia p50/p95 y las 90 barras de disponibilidad) son componentes Blazor
que dibujan SVG: sin bibliotecas, sin interoperabilidad con JavaScript y probables con bUnit. Reglas comunes:

- **Un hueco en los datos corta la línea**: no se une lo que no se midió, ni una hora sin datos se pinta como
  un 100 %.
- El eje vertical usa un máximo «redondo» (100, 250, 500, 1000 ms…) para que se lea.
- Los textos de los ejes salen de datos de la API, así que se escapan.

### Accesibilidad

- **El estado nunca se indica solo con color**: cada estado lleva una palabra («Operativo», «Caído») y una
  forma distinta (círculo con marca, triángulo, cuadrado con aspa, pausa), y los días con caída en las barras
  llevan **trama** además del rojo. Un test comprueba que cada estado tiene una forma diferente.
- Las gráficas son `role="img"` con un título y una descripción que resume lo que muestran, y la de latencia
  ofrece **los mismos datos en una tabla** desplegable. Las horas con fallos llevan una cruz y su explicación.
- Cada campo de formulario tiene su etiqueta, la ayuda va enlazada con `aria-describedby` (los tests lo
  recorren campo por campo) y los errores salen en una región `role="alert"`.
- Las caídas y recuperaciones se anuncian en una región `aria-live="polite"`; hay «saltar al contenido»,
  foco visible, un único `h1` por página y `prefers-reduced-motion` respetado.
- Las tablas usan `scope` y `caption`; en pantallas estrechas pasan a una tarjeta por monitor con cada dato
  etiquetado (sin desplazamiento horizontal).
- Claro u oscuro según el sistema: sobrio, un único acento y colores de estado apagados.

### Formato fijo, no el de la cultura del servidor

Números y fechas se escriben a mano (coma decimal, «15 oct 2026, 10:30 UTC», siempre en UTC y diciéndolo):
en un contenedor la cultura suele ser la invariante y la página saldría con puntos decimales y meses en inglés
según dónde se despliegue.

## Alternativas descartadas

- **Blazor WebAssembly o una SPA de JavaScript.** Más peso, otro sistema de compilación, y la página pública
  dejaría de ser HTML puro. Aquí, el servidor ya tiene todo.
- **Que el panel lea la base de datos directamente.** Saltaría las reglas y los límites de la API y duplicaría
  consultas; además dejaría de poder cambiarse sin tocar los datos.
- **Guardar el token en `localStorage`.** Cualquier script de la página podría leerlo.
- **Una biblioteca de gráficas.** Dependencias y JavaScript para tres gráficas sencillas que así se prueban
  con bUnit.
- **Actualizar la página pública por SignalR.** Un circuito abierto por cada visitante no escala y no aporta:
  un estado con un minuto de retraso le da igual a quien lo mira.

## Consecuencias

- Hay dos procesos que desplegar además del worker (API y web); ya estaban separados y la web es solo un cliente.
- La sesión dura una hora y luego hay que entrar de nuevo: es el precio de no tener tokens de refresco con
  un único usuario (ADR 0006).
- Detrás de un proxy inverso (fase 7) habrá que marcar la cookie como `Secure` y reenviar la IP real del
  cliente.
