# Arquitectura, amenazas y protocolos

## Vista de procesos

Un único proceso `StreamKit.App` (WinForms + Kestrel + SQLite + cliente OBS). Sin servicios auxiliares.

```
TikTok ──TLS──▶ TikTokLive_Sharp ──▶ TikTokEventSource ──┐
                                                          │ Channel<StreamEvent> (bounded 4096, DropOldest)
Simulador ────────────────────────────────────────────────┤
                                                          ▼
                                                   EventPipeline
                                     ┌────────────┬──────────────┬─────────────┐
                                     ▼            ▼              ▼             ▼
                              PointsEngine   CommandRouter   AlertQueue    ObsAutomation
                                     │            │              │             │
                                     ▼            ▼              ▼             ▼
                              ViewerStore    OverlayHub      OverlayHub   ObsWebSocketClient
                                  (SQLite)   (WS broadcast)  (WS alerts)   (WS JSON)
```

Hilos: el hilo de UI solo pinta; TikTok, OBS y Kestrel corren en `Task`s con `async/await`.
La base se escribe desde un único escritor (canal serializado) para evitar contención de SQLite.

## Límites de confianza

| Origen | Confianza | Qué se hace |
|---|---|---|
| Mensajes de TikTok (chat, nicknames, nombres de regalo) | **No confiable** | Longitud acotada, sin HTML, `textContent` en overlay, nunca SQL concatenado, nunca shell |
| HTTP/WS desde el navegador de OBS | Semi-confiable | Token obligatorio en `/api/*` y en `/ws/*`; allowlist de `Origin`/`Host`; solo loopback |
| Página web arbitraria del usuario | **No confiable** | Bloqueada por Origin + token (CSRF/DNS rebinding) |
| Archivo `config.json` local | Confiable pero sensible | Clave Euler y password OBS cifrados con DPAPI (`CurrentUser`), nunca en logs |
| Respuestas de OBS | Confiable | Se validan tipos y se acotan longitudes antes de actuar |

### STRIDE aplicado

| Amenaza | Vector concreto | Mitigación |
|---|---|---|
| Spoofing | Web cualquiera abre `ws://127.0.0.1:PORT/ws/overlay` y escucha el chat | Token en query + `Origin` allowlist |
| Tampering | POST a `/api/obs/scene` desde otra pestaña | Token en header `X-StreamKit-Token` (no cookie ⇒ no CSRF silencioso) + Origin |
| Repudiation | "no fui yo quien reinició los puntos" | Log de auditoría append-only con hora, acción y origen |
| Information disclosure | Log con la signing key / password OBS | Redacción explícita en el logger + DPAPI en reposo |
| DoS | Ráfaga de 10k mensajes o de conexiones WS | `Channel` acotado con descarte, límite de clientes WS, rate limit por IP, tamaño máx. de body 256 KB |
| Elevation | Comando de chat que dispare acciones de OBS | Los comandos solo actúan sobre puntos; las reglas OBS son configuración local, no texto de chat |

## Superficie HTTP

| Ruta | Método | Auth | Uso |
|---|---|---|---|
| `/health` | GET | — | liveness, sin datos sensibles |
| `/overlay/{alerts,chat,stats}.html` | GET | — (estático) | Browser Source |
| `/assets/*` | GET | — | CSS/JS |
| `/ws/overlay?channel=alerts&token=…` | WS | token | alertas/chat/metas |
| `/api/state` | GET | token | estado para panel |
| `/api/viewers/top?n=10` | GET | token | ranking |
| `/api/diagnostics` | GET | token | CPU, RAM, colas, contadores |
| `/api/test/event` | POST | token | inyectar evento sintético |
| `/api/obs/scene` | POST | token | cambiar escena |
| `/api/obs/filter` | POST | token | habilitar/deshabilitar filtro |

Guardas: `Host` debe ser `127.0.0.1[:port]` o `localhost[:port]`; si hay `Origin`, debe coincidir con
el propio origen. Cualquier otra cosa → `403`. Token ausente/incorrecto → `401`. Comparación en tiempo
constante (`CryptographicOperations.FixedTimeEquals`).

## Protocolo overlay (WS, JSON, `v:1`)

Servidor → cliente:

```json
{ "v": 1, "type": "hello", "channel": "alerts", "queueDepth": 0 }
{ "v": 1, "type": "alert", "id": "9f2c", "kind": "gift", "durationMs": 6000,
  "payload": { "user": "ana", "nickname": "Ana", "gift": "Rosa", "count": 3, "diamonds": 3, "text": "Ana envió 3 × Rosa" } }
{ "v": 1, "type": "chat", "payload": { "user": "ana", "nickname": "Ana", "text": "hola", "isModerator": false } }
{ "v": 1, "type": "goal", "payload": { "current": 1200, "target": 5000 } }
{ "v": 1, "type": "ping", "t": 1700000000 }
```

Cliente → servidor:

```json
{ "v": 1, "type": "ready" }
{ "v": 1, "type": "ack", "id": "9f2c" }
```

Regla de no superposición: el servidor entrega **una** alerta por canal; avanza cuando llega `ack`
o cuando vence `durationMs + 2500 ms` (overlay colgado). Si el socket se cae con una alerta activa,
esa alerta vuelve al frente de la cola.

## Cola de alertas

- Un carril por canal (`alerts`), capacidad 64, política de descarte por prioridad
  (`gift > subscribe > follow > share > like`) y luego por antigüedad.
- Coalescencia: ráfagas de `like` del mismo usuario dentro de 3 s se agrupan en un solo item;
  regalos con el mismo `giftId` del mismo usuario dentro de 1.5 s se agrupan incrementando `count`.
- Nunca bloquea al productor: `TryEnqueue` devuelve `false` si está llena (se cuenta y se loguea).

## Esquema SQLite

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;
PRAGMA foreign_keys = ON;

CREATE TABLE schema_version (version INTEGER NOT NULL);

CREATE TABLE viewers (
  user_id         TEXT PRIMARY KEY,
  unique_id       TEXT NOT NULL,
  nickname        TEXT NOT NULL,
  points          INTEGER NOT NULL DEFAULT 0,
  lifetime_points INTEGER NOT NULL DEFAULT 0,
  gifts_count     INTEGER NOT NULL DEFAULT 0,
  diamonds        INTEGER NOT NULL DEFAULT 0,
  first_seen      TEXT NOT NULL,
  last_seen       TEXT NOT NULL
);
CREATE UNIQUE INDEX ux_viewers_unique_id ON viewers(unique_id);
CREATE INDEX ix_viewers_points ON viewers(points DESC);

-- Solo movimientos con valor de auditoría (regalos, canjes, ajustes manuales).
-- Los ticks de tiempo se agregan en memoria y se vuelcan a viewers.points.
CREATE TABLE points_ledger (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id    TEXT NOT NULL,
  delta      INTEGER NOT NULL,
  reason     TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE INDEX ix_ledger_user ON points_ledger(user_id, created_at DESC);

CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
```

Escritura: un solo `SqliteConnection` de larga vida, comandos preparados, un `BEGIN IMMEDIATE` por
flush (cada 5 s o 200 cambios). Lectura de `!puntos` desde caché en memoria; `!top` desde la vista
`ix_viewers_points` con `LIMIT`.

## Cliente obs-websocket v5

Handshake:

1. Servidor envía `Hello { obsWebSocketVersion, rpcVersion: 1, authentication?: { challenge, salt } }`.
2. Cliente calcula `secret = base64(sha256(password + salt))`, luego
   `auth = base64(sha256(secret + challenge))`.
3. Cliente envía `Identify { rpcVersion: 1, authentication, eventSubscriptions }`.
4. Servidor responde `Identified { negotiatedRpcVersion }`.

Peticiones: `{ op: 6, d: { requestType, requestId, requestData } }` → `RequestResponse { op: 7, d: { requestStatus: { result, code }, responseData } }`.
Timeout por petición 5 s; reconexión con backoff 1→2→4→8→15 s (tope) y resync de estado.
Suscripción de eventos: `Scenes`, `SceneItems`, `Filters`, `Outputs`, `InputVolumeMeters` desactivado
(alto volumen, no se necesita).

Acciones de regla soportadas: `scene`, `sourceVisibility`, `filter`, `hotkey`, `mute`, `raw`.

## Reglas de automatización (config)

La pestaña OBS de la app edita esta lista en JSON, la valida con `RuleValidator` y la aplica en
caliente (`ObsAutomation.ReplaceRules`), sin reiniciar. También queda en `settings.dat` cifrado.

```json
[
  {
    "id": "regalo-rosa-escena",
    "enabled": true,
    "on": "gift",
    "where": { "giftId": "5655", "minCount": 1 },
    "cooldownMs": 5000,
    "delayMs": 0,
    "actions": [
      { "type": "scene", "scene": "Alerta" },
      { "type": "filter", "source": "Camara", "filter": "Blur", "enabled": true }
    ]
  }
]
```

`where` admite: `giftId`, `giftName`, `minCount`, `minDiamonds`, `user`, `command`, `keyword`.
Las reglas se evalúan en el pipeline, después de puntos, y se ejecutan en serie por regla.

## Presupuesto de recursos y decisiones

| Decisión | Motivo |
|---|---|
| WinForms en vez de WPF/Electron | Arranque y RSS mucho menores; sin runtime extra |
| Kestrel en vez de `HttpListener` | WebSockets y estáticos maduros, mismo runtime compartido |
| `Microsoft.Data.Sqlite` en vez de EF Core | Sin cambio de modelo ni migraciones automáticas; menos RAM y arranque |
| Cliente OBS propio (~300 líneas) | Evita `Websocket.Client` + `Newtonsoft`; control total del ciclo de vida |
| Logger propio con `Channel` + rotación | Sin Serilog; escritura fuera del hot path |
| `Channel` acotado + write-behind | Picos de chat no bloquean ni saturan SQLite |
| Un solo proceso, sin plugins | Simplicidad y arranque < 1 s |

Números medidos contra el presupuesto del spec: [`docs/PERF.md`](PERF.md).
