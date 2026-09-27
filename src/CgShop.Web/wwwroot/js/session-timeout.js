// Cierre de sesión por inactividad.
// - Registra actividad real (mouse, teclado, toque, scroll) y la comparte entre pestañas (localStorage).
// - Mientras hay actividad renueva la cookie con un ping periódico (sliding expiration en el servidor).
// - Avisa antes del cierre y, al vencer, redirige al endpoint que cierra la sesión.
window.cgSession = (() => {
    const storageKey = "cg-last-activity";
    const events = ["mousemove", "mousedown", "keydown", "touchstart", "scroll", "wheel"];
    let state = null;

    const now = () => Date.now();

    const readShared = () => {
        try { return parseInt(localStorage.getItem(storageKey) || "0", 10) || 0; } catch { return 0; }
    };

    const writeShared = (value) => {
        try { localStorage.setItem(storageKey, String(value)); } catch { /* modo privado */ }
    };

    const lastActivity = () => Math.max(state.lastActivity, readShared());

    const expire = () => {
        if (!state || state.expired) return;
        state.expired = true;
        stop();
        window.location.href = state.expireUrl;
    };

    const ping = async () => {
        state.lastPing = now();
        try {
            const response = await fetch(state.pingUrl, { credentials: "same-origin", cache: "no-store", redirect: "manual" });
            if (response.status === 401 || response.status === 403 || response.type === "opaqueredirect") expire();
        } catch { /* sin red: se reintenta con la próxima actividad */ }
    };

    const onActivity = () => {
        if (!state || state.expired) return;
        const t = now();
        // Con el aviso visible solo cuenta el botón "Seguir conectado" (evita que un mouse accidental lo cierre).
        if (state.warning) return;
        state.lastActivity = t;
        if (t - state.lastShared > 5000) { writeShared(t); state.lastShared = t; }
        if (t - state.lastPing > state.keepAliveMs) ping();
    };

    const tick = () => {
        if (!state || state.expired) return;
        const idleMs = now() - lastActivity();
        const remainingMs = state.timeoutMs - idleMs;

        if (remainingMs <= 0) { expire(); return; }

        if (remainingMs <= state.warningMs) {
            state.warning = true;
            state.dotnet.invokeMethodAsync("ShowWarning", Math.ceil(remainingMs / 1000)).catch(() => { });
        } else if (state.warning) {
            // Otra pestaña registró actividad: ocultar el aviso aquí también.
            state.warning = false;
            state.dotnet.invokeMethodAsync("HideWarning").catch(() => { });
        }
    };

    function start(dotnet, timeoutSeconds, warningSeconds, keepAliveSeconds, pingUrl, expireUrl) {
        stop();
        const t = now();
        state = {
            dotnet, pingUrl, expireUrl,
            timeoutMs: timeoutSeconds * 1000,
            warningMs: warningSeconds * 1000,
            keepAliveMs: keepAliveSeconds * 1000,
            lastActivity: t, lastShared: 0, lastPing: t,
            warning: false, expired: false, timer: 0
        };
        writeShared(t);
        events.forEach(e => window.addEventListener(e, onActivity, { passive: true }));
        state.timer = window.setInterval(tick, 1000);
    }

    /// Llamado por el botón "Seguir conectado".
    function stayConnected() {
        if (!state) return;
        state.warning = false;
        const t = now();
        state.lastActivity = t;
        state.lastShared = t;
        writeShared(t);
        ping();
    }

    function stop() {
        if (!state) return;
        window.clearInterval(state.timer);
        events.forEach(e => window.removeEventListener(e, onActivity));
    }

    return { start, stayConnected, stop };
})();
