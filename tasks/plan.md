# Plan técnico — StreamKit

Cada slice deja el repositorio compilando, con tests verdes y un commit lógico.

## S0 — Esqueleto y contrato
- Solución, `Directory.Build.props`, 7 proyectos, referencias en una sola dirección.
- `IClock`, `IAppLog`, `StreamKitOptions`, `StreamEvent` + records.
- **Verificar:** `dotnet build` verde; `dotnet test` corre (0 tests).

## S1 — Dominio (core)
- `PointsEngine` (regalo, chat, tiempo, transferencia), caché de saldos.
- `CooldownTracker` (global + por usuario, poda acotada).
- `CommandParser` + `CommandRouter` (`!puntos`, `!top`, `!dados`, `!comandos`, `!regalar`, `!sumar`).
- `AlertQueue` (prioridad, coalescencia, ack, timeout, requeue).
- **Verificar:** tests unitarios; cobertura core ≥ 85 %.

## S2 — Persistencia
- `SqliteDatabase` (WAL, pragmas), migración v1, `ViewerRepository`, `LedgerRepository`, `KvStore`.
- `WriteBehindPointStore`: flush cada 5 s / 200 cambios, flush en shutdown.
- **Verificar:** test de integración con DB temporal: migrar, escribir, reabrir, leer.

## S3 — Web y overlays
- `WebHost` con Kestrel en `127.0.0.1`, estáticos, `/health`, guardas de token/Origin.
- `OverlayHub`: canales, broadcast, ack/timeout, límite de clientes.
- API: state, top, diagnostics, test/event, obs/scene, obs/filter.
- Overlays: `alerts.html`, `chat.html`, `stats.html` (vanilla JS, sin build, sin CDN).
- **Verificar:** tests de guardas y de hub; smoke test con `curl` y navegador.

## S4 — TikTok
- `TikTokEventSource` (real) con `TikTokLive_Sharp`, reconexión con backoff, estado.
- `TikTokEventMapper` puro (primitivas → `StreamEvent`).
- `SimulatedEventSource` (chat/gift/follow/like a tasa configurable).
- **Verificar:** tests del mapper; prueba manual en simulador; prueba real si hay key.

## S5 — OBS
- `ObsWebSocketClient`: Hello/Identify, peticiones con `requestId`, eventos, reconexión.
- `ObsAutomation`: evaluación de reglas, cooldown por regla, ejecución de acciones.
- **Verificar:** test de autenticación contra vector del protocolo; servidor WS falso para request/response.

## S6 — App
- `Program`, `ConfigStore` (DPAPI), `RollingFileLogger`.
- `MainForm` (Conexión, Puntos, Overlays, OBS, Diagnóstico), `TrayIcon`.
- Editor de reglas OBS en JSON con validación y aplicación en caliente.
- Composición de todo; arranque/parada ordenada.
- **Verificar:** arranque real, conectar simulador, overlays en OBS, OBS real.

## S7 — Auditoría de recursos
- `tools/StreamKit.Bench`: 60 s, N eventos/min, M clientes WS, muestreo de CPU/RSS, p95.
- `docs/PERF.md` con números y veredicto; ajustes solo si se sale del presupuesto.
- **Verificar:** tabla del spec cumplida.

## Riesgos

| Riesgo | Impacto | Mitigación |
|---|---|---|
| TikTok cambia el esquema protobuf | Se rompe la lectura | Aislado en `tiktok`; actualizar paquete; simulador permite seguir trabajando |
| Euler Stream cambia precio/límites | Sin conexión real | Interfaz `IStreamEventSource` + simulador; documentar |
| obs-websocket sin password en la PC del usuario | Conexión rechazada | UI muestra el error exacto y el puerto detectado |
| SQLite bloqueado por antivirus/OneDrive | Escrituras lentas | DB en `%LOCALAPPDATA%`, WAL, `busy_timeout`, flush acotado |
| WinForms + Kestrel en el mismo proceso | Cierre colgado | `ApplicationExit` → cancelación cooperativa con timeout de 5 s |
