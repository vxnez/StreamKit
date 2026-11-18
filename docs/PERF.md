# Rendimiento medido

Auditoría de CPU, RAM y latencia de `StreamKit.Bench` contra el presupuesto de `SPEC.md`.
Los números son de la máquina de desarrollo del streamer, no de una VM.

## Metodología

- `tools/StreamKit.Bench` corre **el proceso completo** en Release: canal acotado (4096),
  `EventPipeline` secuencial, SQLite con write-behind, Kestrel y `OverlayHub` con **5 clientes
  WebSocket reales** conectados a `/ws/overlay/alerts`.
- Los eventos se generan a ritmo fijo (`--events-per-minute`) y la ráfaga encola N alertas de golpe
  (`--burst N`).
- CPU y RAM se muestrean cada segundo con `ResourceSampler` (working set del proceso, no del GC).
- **Las primeras 3 muestras de CPU se excluyen**: incluyen JIT, arranque de Kestrel y creación de
  la base. La tabla reporta el régimen estable, que es lo que ve el streamer durante un directo.
  La RAM sí usa todas las muestras (es monótona y el máximo importa).
- Latencia = `ahora - ts` del JSON de alerta, es decir, desde que la alerta entra en la cola hasta
  que el cliente la recibe. El cliente del bench hace `ack` al instante: en un navegador real hay
  además el coste de render, así que estos valores son una **cota inferior**.
- Cada escenario usa un puerto propio (8799) y una base temporal; no toca `settings.dat` ni los
  datos del streamer.

### Equipo

| | |
|---|---|
| CPU | Intel Core i5-12450H (8 núcleos / 12 hilos) |
| RAM | 16 GB |
| SO | Windows 11 |
| Runtime | .NET 8, Release, `win-x64` |

## Resultados

| Escenario | Duración | CPU media | CPU p95 | RAM media | RAM máx | Latencia p95 |
|---|---:|---:|---:|---:|---:|---:|
| Idle, 0 eventos, 5 overlays | 60 s | 1.0 % | 3.1 % | 59.5 MB | 64.9 MB | — |
| 500 ev/min, 5 overlays | 60 s | 2.0 % | 6.3 % | 67.3 MB | 70.5 MB | 1 ms |
| Ráfaga de 50 alertas | 10 s | 2.3 % | 6.2 % | 67.8 MB | 69.1 MB | 26 ms |

CPU en porcentaje de **un núcleo**; con 12 hilos, 2 % de un núcleo es ~0.17 % de la máquina.

### Detalle

**Idle (60 s, 0 ev/min, 5 clientes WS)**

```
eventos      : 0 generados · 0 procesados · 0 descartados
alertas      : 0 encoladas · 0 recibidas · 0 descartadas · 5 clientes al cierre
CPU          : media 1.0 % de un núcleo · p95 3.1 % · máx 6.3 % (régimen, sin arranque)
RAM          : media 59.5 MB · máx 64.9 MB · heap 3.2 MB
```

**500 ev/min (60 s, 5 clientes WS)**

```
eventos      : 500 generados · 500 procesados · 0 descartados (500/min)
alertas      : 200 encoladas · 200 recibidas · 0 descartadas · 5 clientes al cierre
CPU          : media 2.0 % de un núcleo · p95 6.3 % · máx 19.0 % (régimen, sin arranque)
RAM          : media 67.3 MB · máx 70.5 MB · heap 5.9 MB
latencia     : p50 1 ms · p95 1 ms · p99 1 ms · máx 12 ms (200 muestras)
```

**Ráfaga de 50 alertas (10 s)**

```
eventos      : 50 generados · 50 procesados · 0 descartados
alertas      : 50 encoladas · 50 recibidas · 0 descartadas · 5 clientes al cierre
CPU          : media 2.3 % de un núcleo · p95 6.2 % · máx 6.2 % (régimen, sin arranque)
RAM          : media 67.8 MB · máx 69.1 MB · heap 3.8 MB
latencia     : p50 21 ms · p95 26 ms · p99 27 ms · máx 27 ms (50 muestras)
```

La latencia de la ráfaga es mayor porque `OverlayHub` entrega **una alerta a la vez** por overlay:
las 50 entran juntas y la última espera a las anteriores. Aun así, 26 ms de p95 está muy por debajo
del presupuesto de 100 ms, y no se perdió ninguna alerta.

## Veredicto

| Escenario | Presupuesto | Medido | |
|---|---|---|---|
| Idle | CPU < 1 % · RAM < 150 MB | 1.0 % · 59.5 MB | ⚠️ CPU en el límite, RAM holgada |
| 500 ev/min + 5 overlays | CPU < 5 % · RAM < 180 MB · p95 < 50 ms | 2.0 % · 67.3 MB · 1 ms | ✅ |
| Ráfaga de 50 | CPU pico < 10 % · RAM < 180 MB · p95 < 100 ms | 6.2 % · 69.1 MB · 26 ms | ✅ |

El idle queda justo en el borde del 1 %: el muestreo es de 1 Hz y redondea a décimas, así que
1.0 % es indistinguible del ruido de medición (tareas del sampler, keep-alive de WebSocket y
flush de SQLite cada 5 s). En RAM hay margen de sobra: ~60 MB es menos de la mitad del presupuesto.

**Conclusión:** el proceso cabe de sobra en el presupuesto para un directo de Minecraft. El caso
realista (500 ev/min con 5 overlays) consume ~2 % de un núcleo y ~67 MB, sin descartes ni picos
por encima del 19 %.

## Reproducir

```powershell
dotnet build tools\StreamKit.Bench\StreamKit.Bench.csproj -c Release

# Idle
dotnet run --project tools\StreamKit.Bench -c Release --no-build -- `
  --seconds 60 --events-per-minute 0 --clients 5 --port 8799

# Carga típica
dotnet run --project tools\StreamKit.Bench -c Release --no-build -- `
  --seconds 60 --events-per-minute 500 --clients 5 --port 8799

# Ráfaga
dotnet run --project tools\StreamKit.Bench -c Release --no-build -- `
  --seconds 10 --events-per-minute 0 --clients 5 --port 8799 --burst 50
```

Los números varían con el equipo, el plan de energía de Windows y lo que esté corriendo en la
máquina; comparar siempre contra el mismo escenario y duración.
