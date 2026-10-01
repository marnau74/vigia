# 0003 · Máquina de estados de los monitores y cálculo de la disponibilidad

- **Estado:** aceptada
- **Fecha:** 2026-09-30

## Contexto

Un monitor recibe una comprobación cada 30 segundos o cada pocos minutos, y de esa corriente de
«bien» y «mal» hay que sacar tres cosas que importan a personas:

1. **¿Está caído?** Y, sobre todo, ¿hay que despertar a alguien?
2. **¿Cuánto ha estado caído?** (los incidentes)
3. **¿Qué disponibilidad tiene?** El famoso «99,9 %».

Los tres tienen trampas. Avisar al primer fallo llena el correo de falsas alarmas (una pérdida de
paquetes de un segundo no es una caída), y quien aprende a ignorar los avisos se pierde el que es de
verdad. Contar mal la disponibilidad convierte una página de estado en propaganda.

## Decisión

### La máquina de estados es código puro de dominio

`SeguimientoDeMonitor` recibe observaciones («bien, 80 ms», «mal, tiempo agotado») y devuelve una lista
de eventos (`EstadoCambiado`, `IncidenteAbierto`, `IncidenteCerrado`). No usa la red, el reloj ni la
base de datos, así que se prueba con secuencias de letras (`OOOFFFO…`) sin esperar nada. Es un
componente, no un servicio: quien lo usa (el worker) decide qué hacer con los eventos.

```
Desconocido ─OK→ Operativo ⇄ Degradado          (OK lento ↔ OK rápido)
cualquiera ─fallo→ Sospechoso ─OK→ Operativo
Sospechoso ─N fallos seguidos→ Caído            (abre incidente y avisa, una vez)
Caído ─OK→ Operativo o Degradado                (cierra el incidente y avisa, una vez)
cualquiera ─ventana de mantenimiento→ Mantenimiento ─fin→ Desconocido
cualquiera ─pausa, o hueco sin comprobaciones→ Desconocido   (cierra el incidente sin avisar)
```

- **«Sospechoso» absorbe el ruido.** Hacen falta N fallos *seguidos* (por monitor, entre 1 y 10, 3 por
  defecto) para dar el servicio por caído. Con N = 1 se cae al primer fallo, sin pasar por sospechoso.
- **Un aviso por transición, nunca por repetición.** Seguir fallando estando caído no genera eventos de
  estado ni avisos nuevos; solo actualiza la causa del incidente (siempre el último error). Un test
  recorre 10.000 secuencias aleatorias con un modelo de referencia y comprueba, en cada paso, que hay
  incidente abierto si y solo si el estado es «caído», y que «caído» ocurre justo al llegar a N fallos seguidos.
- **Las observaciones repetidas o anteriores a la última se ignoran.** Si el worker se reinicia y
  reintenta, no cuenta dos veces el mismo fallo.
- **Los reintentos inmediatos son cosa del worker:** antes de contar un fallo se repite la comprobación
  una vez tras una espera corta. El dominio no lo sabe; solo ve el resultado final.

### El mantenimiento

Una ventana de mantenimiento cubre a unos monitores durante `[inicio, fin)`. Durante ella:

- El estado es «mantenimiento»; las comprobaciones se siguen haciendo y guardando, pero **no cuentan**
  (ni fallos, ni incidentes, ni avisos).
- Si el monitor estaba caído al empezar, el incidente se **cierra por mantenimiento** (no por
  recuperación): no se avisa de una «recuperación» que no ha ocurrido.
- **Al terminar se vuelve a «desconocido», no a «operativo».** Nadie sabe cómo está el servicio hasta que
  se comprueba. Si sigue caído, se detecta por el camino normal (N fallos); si está sano, el primer éxito
  lo deja operativo sin ningún aviso. Volver a «operativo» a ciegas ocultaría un servicio que no volvió.

### Cuando no se mira

Un estado solo vale mientras lo respalda una comprobación. Cada una **vale tres intervalos** (con uno de 60 s,
tres minutos): margen para el retraso normal del planificador y para perder una comprobación suelta. Pasado ese
tiempo sin otra, no se sabe cómo está el servicio:

- **Al pausar un monitor**, el worker lo pasa a «desconocido» y, si estaba caído, **cierra el incidente
  «sin vigilancia»**, sin avisar: nadie ha visto que se recupere.
- **Si el worker estuvo parado** (o el monitor se reanuda), la primera comprobación anota antes el hueco: el
  monitor pasó a «desconocido» cuando venció la anterior. Si el servicio sigue caído, se abre otro incidente
  por el camino normal y se avisa: Vigía no lo vio durante el hueco y vuelve a confirmarlo.
- **La disponibilidad cuenta igual:** al agregar cada hora, el tiempo que no cubre ninguna comprobación es
  «desconocido», aunque el último estado registrado fuera otro. Sin esto, un monitor pausado una semana con el
  servicio en pie sumaba una semana de disponibilidad perfecta que nadie había medido.

### La disponibilidad

```
disponibilidad = tiempo en pie / (tiempo en pie + tiempo caído)
```

Se calcula sobre el **tiempo pasado en cada estado**, no sobre el número de comprobaciones. Así no
depende del intervalo (un monitor que comprueba cada 30 s y otro cada 5 min son comparables) y se puede
agregar por horas y por días sumando tiempos.

- **El mantenimiento no cuenta ni a favor ni en contra.** Es tiempo planificado.
- **El tiempo «desconocido» tampoco.** Contarlo como bueno sería inventarse un dato; contarlo como malo,
  penalizar por no haber mirado. Incluye el tiempo sin vigilar (arriba).
- **«Sospechoso» y «degradado» cuentan como en pie.** Un fallo aislado no es una caída hasta que se
  confirma; cuando se confirma, el tiempo caído se cuenta desde que se abre el incidente.
- **Sin datos, el resultado es «sin datos»**, no 100 %.
- **El porcentaje se trunca, no se redondea.** 99,9996 % se enseña como 99,99 %: mostrar 100 % habiendo
  tenido una caída es la trampa clásica de las páginas de estado.

Para 30 días, el 99,9 % permite 43 minutos y 12 segundos de caída; hay un test con esa cuenta.

### Los percentiles

La latencia se resume con la media y los percentiles p50 y p95 por el método del rango más cercano. La
media esconde los picos (diecinueve respuestas de 100 ms y una de 5 s dan una media de 345 ms que no
describe a ninguna); el p95 sí muestra que hay un problema, y por eso el panel enseña los dos.

## Alternativas descartadas

- **Avisar al primer fallo:** falsas alarmas a todas horas.
- **Una ventana de tiempo** («caído si más del 50 % de las comprobaciones de los últimos 5 minutos
  fallaron»): más difícil de razonar y de probar que «N fallos seguidos», y con el intervalo entra en juego
  una segunda variable.
- **Disponibilidad como «comprobaciones correctas / totales»:** depende del intervalo y trata igual un
  fallo aislado que cinco minutos de caída confirmada.
- **Volver a «operativo» al salir del mantenimiento:** ver arriba.
- **Guardar solo el porcentaje de cada hora:** no se puede agregar bien (un porcentaje de porcentajes no es
  el porcentaje total); se guarda el tiempo en cada estado y se calcula al final.

## Consecuencias

- **Detectar una caída tarda hasta N × intervalo** (con 3 fallos y 60 s, unos 3 minutos). Es el precio de
  no avisar por ruido, y es configurable por monitor.
- Los eventos que devuelve la máquina de estados hay que **guardarlos todos** (fase 3): el registro de
  cambios de estado es lo que permite reconstruir los tramos y calcular la disponibilidad.
- El worker debe pasar la hora de cada observación y aplicar el mantenimiento en cada pasada
  (`ActualizarMantenimiento` es idempotente), porque el dominio no tiene reloj propio.
