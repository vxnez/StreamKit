/* StreamKit overlay client. Sin dependencias, sin innerHTML con datos del chat. */
(() => {
  "use strict";

  const params = new URLSearchParams(location.search);
  const token = params.get("token") || "";
  const channel = document.body.dataset.channel || "alerts";

  const state = {
    socket: null,
    retries: 0,
    stopped: false,
    alertQueue: [],
    playing: false,
  };

  const MAX_CHAT_ITEMS = 12;
  const CHAT_TTL_MS = 45000;

  function socketUrl() {
    const scheme = location.protocol === "https:" ? "wss:" : "ws:";
    return `${scheme}//${location.host}/ws/overlay?channel=${encodeURIComponent(channel)}&token=${encodeURIComponent(token)}`;
  }

  function connect() {
    if (state.stopped) return;

    let socket;
    try {
      socket = new WebSocket(socketUrl());
    } catch {
      scheduleReconnect();
      return;
    }

    state.socket = socket;

    socket.addEventListener("open", () => {
      state.retries = 0;
    });

    socket.addEventListener("message", (event) => {
      if (typeof event.data !== "string") return;
      let message;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }
      handle(message);
    });

    socket.addEventListener("close", () => {
      state.socket = null;
      scheduleReconnect();
    });

    socket.addEventListener("error", () => {
      try {
        socket.close();
      } catch {
        /* noop */
      }
    });
  }

  function scheduleReconnect() {
    if (state.stopped) return;
    const delay = Math.min(10000, 500 * Math.pow(2, state.retries));
    state.retries += 1;
    setTimeout(connect, delay);
  }

  function handle(message) {
    if (!message || typeof message.type !== "string") return;

    switch (message.type) {
      case "alert":
        state.alertQueue.push(message);
        playNextAlert();
        break;
      case "chat":
        renderChat(message.payload);
        break;
      case "stats":
        renderStats(message.payload);
        break;
      case "hello":
      case "ping":
      default:
        break;
    }
  }

  function sendAck(id) {
    const socket = state.socket;
    if (!socket || socket.readyState !== WebSocket.OPEN) return;
    socket.send(JSON.stringify({ v: 1, type: "ack", id }));
  }

  /* ---------------- Alertas ---------------- */

  const KIND_ICON = {
    gift: "🎁",
    follow: "⭐",
    subscribe: "💜",
    like: "❤️",
    share: "🔁",
    custom: "✨",
  };

  function playNextAlert() {
    if (state.playing) return;

    const root = document.getElementById("alert-root");
    if (!root) return;

    const alert = state.alertQueue.shift();
    if (!alert) return;

    state.playing = true;

    const card = document.createElement("div");
    card.className = "alert-card";
    card.dataset.kind = alert.kind || "custom";
    card.style.setProperty("--alert-duration", `${Math.max(800, alert.durationMs || 4000)}ms`);

    const icon = document.createElement("div");
    icon.className = "alert-icon";
    icon.textContent = KIND_ICON[alert.kind] || KIND_ICON.custom;

    const body = document.createElement("div");
    body.className = "alert-body";

    const title = document.createElement("div");
    title.className = "alert-title";
    title.textContent = alertTitle(alert);

    const detail = document.createElement("div");
    detail.className = "alert-detail";
    detail.textContent = alertDetail(alert);

    body.append(title, detail);
    card.append(icon, body);

    if (typeof alert.count === "number" && alert.count > 1 && alert.kind !== "like") {
      const badge = document.createElement("div");
      badge.className = "alert-count";
      badge.textContent = `×${alert.count}`;
      card.append(badge);
    }

    root.replaceChildren(card);

    const hold = Math.max(800, alert.durationMs || 4000);

    setTimeout(() => {
      card.classList.add("is-leaving");
      setTimeout(() => {
        root.replaceChildren();
        state.playing = false;
        sendAck(alert.id);
        playNextAlert();
      }, 320);
    }, hold);
  }

  function alertTitle(alert) {
    const payload = alert.payload || {};
    if (payload.nickname) return payload.nickname;
    if (payload.user) return payload.user;
    return alert.text || "Alerta";
  }

  function alertDetail(alert) {
    const payload = alert.payload || {};

    switch (alert.kind) {
      case "gift": {
        const gift = payload.gift || "un regalo";
        const count = Number(payload.count || alert.count || 1);
        return count > 1 ? `envió ${count} × ${gift}` : `envió ${gift}`;
      }
      case "follow":
        return "nuevo seguidor";
      case "subscribe": {
        const months = Number(payload.months || 1);
        return months > 1 ? `se suscribió por ${months} meses` : "se suscribió";
      }
      case "like": {
        const count = Number(payload.count || alert.count || 1);
        return `${count} ${count === 1 ? "like" : "likes"}`;
      }
      case "share":
        return "compartió el directo";
      default:
        return alert.text || "";
    }
  }

  /* ---------------- Chat ---------------- */

  function renderChat(payload) {
    const root = document.getElementById("chat-root");
    if (!root || !payload) return;

    const item = document.createElement("div");
    item.className = "chat-item";
    item.dataset.bot = payload.isBot ? "true" : "false";
    item.dataset.error = payload.isError ? "true" : "false";

    const avatar = document.createElement("div");
    avatar.className = "chat-avatar";
    avatar.textContent = initials(payload.nickname || payload.user || "?");

    const body = document.createElement("div");
    body.className = "chat-body";

    const name = document.createElement("div");
    name.className = "chat-name";
    name.textContent = payload.nickname || payload.user || "anónimo";

    const text = document.createElement("div");
    text.className = "chat-text";
    text.textContent = payload.text || "";

    body.append(name, text);
    item.append(avatar, body);
    root.append(item);

    while (root.childElementCount > MAX_CHAT_ITEMS) {
      root.firstElementChild?.remove();
    }

    setTimeout(() => {
      item.classList.add("is-leaving");
      setTimeout(() => item.remove(), 320);
    }, CHAT_TTL_MS);
  }

  function initials(value) {
    const clean = String(value).replace(/^@/, "").trim();
    if (!clean) return "?";
    const parts = clean.split(/[\s._-]+/).filter(Boolean);
    if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
    return clean.slice(0, 2).toUpperCase();
  }

  /* ---------------- Estadísticas ---------------- */

  function renderStats(payload) {
    if (!payload) return;

    setText("stat-viewers", formatNumber(payload.viewers));
    setText("stat-points", formatNumber(payload.tracked));
    setText("stat-events", formatNumber(payload.events));

    const top = Array.isArray(payload.top) ? payload.top : [];
    const summary = top
      .slice(0, 3)
      .map((viewer, index) => `${index + 1}. ${viewer.nickname || viewer.user} ${formatNumber(viewer.points)}`)
      .join("  ·  ");

    setText("stat-top", summary || "sin datos todavía");
  }

  function setText(id, value) {
    const element = document.getElementById(id);
    if (element) element.textContent = value;
  }

  function formatNumber(value) {
    const number = Number(value || 0);
    return new Intl.NumberFormat("es-AR").format(number);
  }

  connect();
})();
