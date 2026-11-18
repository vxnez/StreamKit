# Spec: StreamKit — control local de TikTok LIVE + OBS

## Objetivo

Aplicación de escritorio **local, monousuario y sin telemetría** que conecta un LIVE de TikTok con
un sistema propio de puntos/comandos y con OBS Studio, para eliminar la dependencia de herramientas
freemium o de pago.

El usuario transmite (p. ej. Minecraft) y necesita:

1. Leer chat, regalos, likes, follows, shares, joins y suscripciones en tiempo real.
2. Acumular puntos por espectador en SQLite y responder comandos de chat con cooldowns.
3. Mostrar alertas/chat/metas en OBS mediante Browser Sources servidos por un HTTP/WebSocket local.
4. Reaccionar en OBS (escena, visibilidad, filtro, hotkey) a eventos del directo.
5. Que todo corra en segundo plano sin robar CPU/RAM al juego.

Éxito = el streamer abre la app, pulsa "Conectar", y todo lo anterior funciona sin editar archivos a mano.

## No objetivos

- Multiusuario, cuentas, nube, sincronización entre PCs, telemetría.
- **Escribir mensajes en el chat de TikTok**: no existe API pública para ello; las respuestas de
  comandos se renderizan en el overlay de chat.
- Grabación/edición de video, transcoding, o reemplazo de OBS.
- Soporte de varias cuentas de TikTok simultáneas (v1: un host por sesión).
- Móvil, macOS, Linux.

## Supuestos (corregir antes de implementar)

1. **Windows 10/11 x64.** Runtime .NET 8 presente (8.0.33). El SDK instalado es 10.0.403; compila
   `net8.0` sin problema (verificado).
2. **TikTok LIVE exige firmar el WebSocket** con un servicio externo. Se usa `TikTokLive_Sharp`
   1.2.3 (open source, MIT) con el servicio de firma Euler Stream (plan gratuito con rate limit).
   La clave la aporta el usuario y se guarda cifrada con DPAPI. Sin clave, la app funciona en
   **modo simulador** (eventos sintéticos) para desarrollo y para la auditoría de rendimiento.
   Esto es una restricción del protocolo de TikTok, no del diseño: ninguna librería open source
   firma localmente.
3. Las respuestas de comandos **no** se envían al chat de TikTok; se muestran en overlay y en el log.
4. OBS Studio 28+ con obs-websocket 5.x habilitado (por defecto `ws://127.0.0.1:4455`).
5. Un solo operador: no hay login. La superficie HTTP se protege con token + allowlist de Origin
   porque "local" no significa "confiable" (cualquier web abierta puede intentar hablar con
   `127.0.0.1`).
6. UI en español; identificadores de código en inglés.

## Mapa de capacidades

| Módulo | Responsabilidad | Depende de |
|---|---|---|
| `core` | Modelo de eventos, motor de puntos, comandos, cooldowns, cola de alertas, reglas, opciones, logging | — |
| `data` | SQLite: esquema, migraciones, repositorios, write-behind de puntos | `core` |
| `web` | Kestrel: estáticos, WebSocket de overlays, API local, guardas de token/Origin | `core` |
| `tiktok` | Adaptador `TikTokLive_Sharp` → `StreamEvent`, reconexión, modo simulador | `core` |
| `obs` | Cliente obs-websocket v5 propio (WS + JSON), automatización por reglas | `core` |
| `app` | Host WinForms, bandeja, config cifrada, composición, diagnóstico | todos |
| `bench` | Auditoría de CPU/RAM/latencia bajo carga | `core`, `data`, `web` |

Orden de construcción: `core` → `data` → `web` → `tiktok` → `obs` → `app` → `bench`.
Dirección de dependencias sin ciclos: los módulos de infraestructura dependen de `core`, nunca al revés.
`core` no referencia ningún paquete externo.

## Comandos

```powershell
# Build
dotnet build StreamKit.sln -c Release

# Tests
dotnet test tests/StreamKit.Tests/StreamKit.Tests.csproj -c Release

# Ejecutar
dotnet run --project src/StreamKit.App -c Release

# Publicar (framework-dependent, ~15 MB)
dotnet publish src/StreamKit.App -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o dist

# Auditoría de recursos (60 s, 500 eventos/min, 5 overlays)
dotnet run --project tools/StreamKit.Bench -c Release -- --seconds 60 --events-per-minute 500 --clients 5
```

## Estructura

```
StreamKit/
  StreamKit.sln
  Directory.Build.props        # nullable, warnings, analyzers, versiones
  docs/SPEC.md                 # este documento
  docs/ARCHITECTURE.md         # amenazas, protocolos, esquema, presupuesto de rendimiento
  docs/PERF.md                 # resultados medidos (bench)
  tasks/plan.md                # plan técnico por slices
  tasks/todo.md                # checklist ejecutable
  src/StreamKit.Core/          # dominio puro, sin IO ni paquetes
  src/StreamKit.Data/          # Microsoft.Data.Sqlite
  src/StreamKit.Web/           # Kestrel + wwwroot/overlay
  src/StreamKit.TikTok/        # TikTokLive_Sharp
  src/StreamKit.Obs/           # obs-websocket v5
  src/StreamKit.App/           # WinForms + bandeja (net8.0-windows)
  tools/StreamKit.Bench/       # auditoría de rendimiento
  tests/StreamKit.Tests/       # xUnit
```

## Estilo de código

```csharp
// Eventos como records inmutables; el dominio no conoce TikTok ni OBS.
public sealed record GiftEvent(
    DateTimeOffset At,
    ViewerRef Viewer,
    string GiftId,
    string GiftName,
    int Count,
    int Diamonds,
    bool StreakFinished) : StreamEvent(At, Viewer);

// Servicios con dependencias explícitas, reloj inyectable, sin estado global.
public sealed class PointsEngine(IViewerStore store, IClock clock, StreamKitOptions options)
{
    public async Task<long> AwardAsync(ViewerRef viewer, long delta, string reason, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(delta);
        var balance = await store.AddPointsAsync(viewer, delta, reason, clock.UtcNow, ct);
        return balance;
    }
}
```

Convenciones: `PascalCase` público, `_camelCase` privado, `Async` en métodos asíncronos, `CancellationToken`
siempre último, `sealed` por defecto, `record` para datos, `ILogger`-style propio (`IAppLog`) para no
arrastrar `Microsoft.Extensions.Logging` a `core`.

## Estrategia de testing

- **xUnit** en `tests/StreamKit.Tests` (`net8.0`).
- Unitarios: puntos, cooldowns, parser de comandos, cola de alertas, reglas de OBS, política de
  token/Origin, autenticación obs-websocket (contra vector oficial del protocolo).
- Integración: SQLite sobre archivo temporal (`Migrate` → repos → reinicio → datos intactos).
- Web: `OverlayHub` con sockets falsos y `WebApplication` en puerto efímero para probar guardas.
- TikTok: el mapeo se hace desde primitivas (`TikTokEventMapper`), así que se testea sin red.
- Sin tests de UI. Cobertura objetivo de `core` ≥ 85 %.
- Nada de tests que dependan de internet.

## Límites

**Siempre**
- `dotnet build` + `dotnet test` verdes antes de cerrar cada slice.
- SQL parametrizado; nunca concatenar texto de chat en SQL.
- Texto de chat renderizado con `textContent`, jamás `innerHTML`.
- HTTP/WS escuchando solo en `127.0.0.1`; `/api/*` exige token; Origin externo → 403.
- Cifrar con DPAPI la clave de firma y la contraseña de OBS.

**Consultar antes**
- Agregar un paquete NuGet nuevo.
- Cambiar el esquema de la base (migración nueva).
- Escuchar en una interfaz que no sea loopback.
- Agregar cualquier salida de red distinta de TikTok/Euler y OBS local.

**Nunca**
- Commitear claves, tokens o contraseñas.
- Loguear la signing key, la contraseña de OBS o tokens de overlay.
- Desactivar la verificación de Origin "para que funcione".
- Ejecutar comandos de shell construidos con texto de chat.

## Presupuesto de rendimiento

Medido con `tools/StreamKit.Bench` (60 s, Release, `win-x64`), sobre la PC del streamer:

| Escenario | CPU (1 núcleo) | RAM (working set) | Latencia p95 evento→overlay |
|---|---|---|---|
| Idle conectado, 0 eventos | < 1 % | < 150 MB | — |
| 500 ev/min + 5 overlays WS | < 5 % | < 180 MB | < 50 ms |
| Ráfaga de 50 alertas | < 10 % pico | < 180 MB | < 100 ms |

Configuración de runtime: workstation GC concurrente, `TieredPGO`, `PublishReadyToRun`,
`ServerGarbageCollection=false`, sin `InvariantGlobalization` (se necesita `es`).

## Criterios de éxito

- [ ] `dotnet build` y `dotnet test` verdes; app arranca sin excepciones.
- [ ] Conectar/desconectar TikTok (real o simulador) con estado visible y reconexión con backoff.
- [ ] Puntos: otorgar por regalo/chat/tiempo, `!puntos` responde en < 10 ms, sobreviven reinicio.
- [ ] Cooldowns en RAM: 20 mensajes/segundo del mismo usuario no producen más de 1 respuesta por ventana.
- [ ] Overlays: 3 Browser Sources sirven y reciben; 50 alertas simultáneas se muestran **en serie**,
      sin superposición, sin pérdida (salvo descarte explícito por límite de cola).
- [ ] OBS: cambio de escena y filtro disparados por regla de regalo verificados contra OBS real o
      servidor falso.
- [ ] Seguridad: sin token → 401; Origin `https://evil.example` → 403; escucha solo en 127.0.0.1.
- [ ] Rendimiento dentro del presupuesto, con números en `docs/PERF.md`.

## Preguntas abiertas

1. Nombre final y carpeta del proyecto.
2. Forma de la UI (ventana WinForms + bandeja vs. bandeja + panel web).
3. ¿Ya hay API key de Euler Stream para el modo real?
