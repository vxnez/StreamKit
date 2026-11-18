# Todo — StreamKit

## S0 Esqueleto
- [x] Solución + proyectos + referencias
- [x] `Directory.Build.props` (nullable, analyzers, R2R, GC)
- [x] Contratos base (`IClock`, `IAppLog`, `StreamEvent`, `StreamKitOptions`)

## S1 Core
- [x] `PointsEngine` (diamantes reportados por la plataforma, sin tabla de valores)
- [x] `CooldownTracker`
- [x] `CommandParser` / `CommandRouter`
- [x] `AlertQueue` + `AlertCoalescer`
- [x] Tests

## S2 Data
- [x] Migración v1 + pragmas
- [x] `SqliteViewerStore` (caché, write-behind, ledger, transferencias), `KvStore`
- [x] Write-behind + flush
- [x] Tests de integración

## S3 Web
- [x] Kestrel + guardas (Host, Origin, token, loopback)
- [x] `OverlayHub` (canales, ack, timeout, requeue, límite)
- [x] API REST
- [x] Overlays HTML/CSS/JS (`alerts`, `chat`, `stats`)
- [x] Tests (guardas, hub, servidor real con HTTP y WebSocket)

## S4 TikTok
- [x] `TikTokEventMapper`
- [x] `TikTokEventSource` real
- [x] `SimulatedEventSource`
- [x] Tests

## S5 OBS
- [x] `ObsWebSocketClient`
- [x] `ObsAutomation` + reglas
- [x] Tests

## S6 App
- [x] Config DPAPI
- [x] Logger con rotación
- [x] `MainForm` + bandeja (Conexión, Puntos, Overlays, OBS, Diagnóstico)
- [x] Composición y apagado ordenado
- [x] Reglas OBS editables en la UI (JSON validado, aplicación en caliente)
- [x] Tests de `LiveAlerts` / `LiveSessionHandler`

## S7 Rendimiento
- [x] `StreamKit.Bench`
- [x] `docs/PERF.md` con números (idle, 500 ev/min, ráfaga de 50)
