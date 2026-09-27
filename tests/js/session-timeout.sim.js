// Prueba de la lógica de cierre por inactividad (reloj simulado). Uso: node tests/js/session-timeout.sim.js
let clock = 0; const timers = [];
global.Date = { now: () => clock };
const store = {}; global.localStorage = { getItem: k => store[k] ?? null, setItem: (k, v) => store[k] = v };
const listeners = {};
global.window = { addEventListener: (e, f) => (listeners[e] = f), removeEventListener: e => delete listeners[e],
  setInterval: (f) => { timers.push(f); return timers.length; }, clearInterval: () => timers.length = 0, location: { href: "" } };
let pings = 0; global.fetch = async () => { pings++; return { status: 204, type: "basic" }; };
require(require("path").join(__dirname, "../../src/CgShop.Web/wwwroot/js/session-timeout.js"));
let failures = 0;
const calls = []; const dotnet = { invokeMethodAsync: (m, a) => { calls.push([m, a]); return Promise.resolve(); } };
const advance = (sec) => { for (let i = 0; i < sec; i++) { clock += 1000; timers.forEach(t => t()); } };
const ok = (name, cond) => { if (!cond) failures++; console.log((cond ? "OK   " : "FALLA") + " " + name); };

window.cgSession.start(dotnet, 900, 60, 60, "/ping", "/expired");
advance(600); listeners.mousemove();                       // actividad a los 10:00
ok("renueva la cookie (ping) con actividad", pings >= 1);
advance(839); ok("sin aviso a los 13:59 de inactividad", !calls.some(c => c[0] === "ShowWarning"));
advance(1);   ok("aviso a los 14:00 de inactividad", calls.some(c => c[0] === "ShowWarning"));
listeners.mousemove(); advance(1); ok("un mouse accidental no cancela el aviso", window.location.href === "");
window.cgSession.stayConnected(); advance(840);
ok("'Seguir conectado' reinicia el plazo completo", window.location.href === "");
store["cg-last-activity"] = String(clock);                 // actividad en OTRA pestaña
advance(30); ok("actividad en otra pestaña oculta el aviso", calls.some(c => c[0] === "HideWarning"));
advance(869); ok("no expira antes de 15 min desde la última actividad", window.location.href === "");
advance(2); ok("expira a los 15 min sin actividad", window.location.href === "/expired");
process.exitCode = failures ? 1 : 0;
