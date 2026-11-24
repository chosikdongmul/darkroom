/*
 * Darkroom 위젯 SDK. 호스트가 모든 위젯 문서에 자동으로 넣는다(페이지 스크립트보다 먼저 실행).
 *
 *   darkroom.subscribe("time.now", (v) => { ... })   // Data Bus 구독, 해제 함수를 돌려줌
 *   darkroom.settings                                 // 이 위젯의 설정값
 *   darkroom.onSettingsChanged((s) => { ... })
 *   darkroom.theme                                    // 현재 테마 토큰 (CSS 변수 --dr-* 로도 들어감)
 *   darkroom.onThemeChanged((t) => { ... })
 *   darkroom.action({ type: "open", target: "C:\\Photos" })
 *   darkroom.setSetting("format24h", false)
 *   darkroom.web.show(element, url)                   // "web" 권한: element 자리에 웹 화면을 띄움(web.allow 사이트만)
 *
 * 드래그: data-drag 속성이 있는 요소를 끌거나, 아무 곳이나 Ctrl+드래그.
 */
(() => {
  "use strict";
  if (window.darkroom) return;

  const init = window.__darkroomInit || {};
  try { delete window.__darkroomInit; } catch { /* 무시 */ }
  const wv = window.chrome && window.chrome.webview;
  const post = (msg) => { if (wv) wv.postMessage(msg); };

  // network 권한이 없으면 WebRTC를 없앤다. CSP connect-src는 WebRTC(STUN/TURN)를 막지 못해서다.
  // fetch·WebSocket 등은 호스트가 응답 헤더 CSP로 막는다. (최선 노력: 같은 출처 iframe의 새 창 객체까지는 못 막을 수 있다)
  if (init.network === false) {
    for (const name of ["RTCPeerConnection", "webkitRTCPeerConnection", "RTCDataChannel", "RTCIceTransport"]) {
      try { Object.defineProperty(window, name, { value: undefined, configurable: false, writable: false }); } catch { /* 무시 */ }
    }
  }

  let theme = init.theme || { tokens: {}, vars: {} };
  let settings = init.settings || {};
  let win = init.window || { opacity: 1, hoverFade: false };

  // ---- 스타일: 테마 변수 + 내장 글꼴 + 투명 배경 + 위젯 불투명도/마우스 올리면 흐려지기 ----
  // 글꼴: 호스트가 고른 글꼴 조합의 @font-face(theme.fontCss)와 글꼴 변수(--dr-font-ui, --dr-font-num, --dr-num-*)를 넘긴다.
  // 글꼴 조각은 https://assets.darkroom/fonts/packs/<조합>/ 에서 오고, 화면에 나온 글자 범위의 조각만 읽는다.
  // 글꼴 CSS는 바뀔 때만 다시 넣는다(불투명도 변경 등에서 매번 큰 CSS를 다시 파싱하지 않게).
  const fontSheet = new CSSStyleSheet();
  const sheet = new CSSStyleSheet();
  document.adoptedStyleSheets = [...document.adoptedStyleSheets, fontSheet, sheet];
  let fontCssApplied = null;
  function render() {
    const fontCss = typeof theme.fontCss === "string" ? theme.fontCss : "";
    if (fontCss !== fontCssApplied) { fontSheet.replaceSync(fontCss); fontCssApplied = fontCss; }
    const vars = Object.entries(theme.vars || {}).map(([k, v]) => `${k}:${v};`).join("");
    const opacity = Math.min(1, Math.max(0.1, Number(win.opacity) || 1));
    const hover = win.hoverFade ? opacity * 0.25 : opacity;
    sheet.replaceSync(
      `:root{${vars}--dr-window-opacity:${opacity};--dr-window-hover-opacity:${hover};}` +
      `:where(html,body){background:transparent;}` +
      `:root{opacity:var(--dr-window-opacity);transition:opacity .25s ease;}` +
      `:root:hover{opacity:var(--dr-window-hover-opacity);}`);
  }

  // ---- 콜백 ----
  const subs = new Map();   // key → Set<callback>
  const last = new Map();   // key → 마지막 값
  const settingsCallbacks = new Set();
  const themeCallbacks = new Set();
  const safe = (fn, arg) => { try { fn(arg); } catch (e) { reportError(e); } };
  const reportError = (e) => post({ type: "log", level: "error", message: String(e && e.stack || e) });

  function subscribe(key, callback) {
    if (typeof key !== "string" || typeof callback !== "function") throw new TypeError("subscribe(key: string, callback: function)");
    let set = subs.get(key);
    if (!set) { set = new Set(); subs.set(key, set); post({ type: "subscribe", key }); }
    set.add(callback);
    if (last.has(key)) queueMicrotask(() => set.has(callback) && safe(callback, last.get(key)));
    return () => {
      set.delete(callback);
      if (set.size === 0 && subs.get(key) === set) { subs.delete(key); post({ type: "unsubscribe", key }); }
    };
  }

  const onChange = (set) => (callback) => { set.add(callback); return () => set.delete(callback); };

  if (wv) {
    wv.addEventListener("message", (e) => {
      const m = e.data;
      if (!m || typeof m !== "object") return;
      switch (m.type) {
        case "data":
          last.set(m.key, m.value);
          for (const cb of subs.get(m.key) || []) safe(cb, m.value);
          break;
        case "settings":
          settings = m.settings || {};
          for (const cb of settingsCallbacks) safe(cb, { ...settings });
          break;
        case "theme":
          theme = m.theme || theme;
          render();
          for (const cb of themeCallbacks) safe(cb, { ...theme.tokens });
          break;
        case "window":
          win = m.window || win;
          render();
          break;
        case "actionError":
          console.warn("darkroom action 거부:", m.reason);
          break;
      }
    });
  }

  // ---- 드래그 ----
  window.addEventListener("mousedown", (e) => {
    if (e.button !== 0) return;
    const t = e.target instanceof Element ? e.target : null;
    const interactive = t && t.closest("[data-no-drag], a, button, input, select, textarea, label");
    if (e.ctrlKey || (t && t.closest("[data-drag]") && !interactive)) {
      e.preventDefault();
      post({ type: "drag" });
    }
  }, true);

  window.addEventListener("error", (e) => post({ type: "log", level: "error", message: `${e.message} (${e.filename}:${e.lineno})` }));
  window.addEventListener("unhandledrejection", (e) => reportError(e.reason));

  // ---- 웹 화면("web" 권한): 요소의 자리에 호스트가 웹 화면을 겹쳐 띄운다 ----
  let webEl = null;
  const rectOf = (elem) => { const r = elem.getBoundingClientRect(); return { x: r.left, y: r.top, width: r.width, height: r.height }; };
  const webApi = init.web ? Object.freeze({
    show(elem, url) {
      if (!(elem instanceof Element)) throw new TypeError("web.show(element, url)");
      webEl = elem;
      post({ type: "web", op: "show", url: url == null ? null : String(url), rect: rectOf(elem) });
    },
    navigate(url) { post({ type: "web", op: "navigate", url: String(url), rect: webEl ? rectOf(webEl) : undefined }); },
    hide() { webEl = null; post({ type: "web", op: "hide" }); },
  }) : undefined;
  if (init.web) window.addEventListener("resize", () => { if (webEl) post({ type: "web", op: "show", url: null, rect: rectOf(webEl) }); });

  const api = Object.freeze({
    version: "0.1.0",
    widget: Object.freeze({ ...(init.widget || {}) }),
    subscribe,
    get settings() { return { ...settings }; },
    onSettingsChanged: onChange(settingsCallbacks),
    get theme() { return { ...(theme.tokens || {}) }; },
    get themeName() { return theme.name || ""; },
    onThemeChanged: onChange(themeCallbacks),
    action(action) { post({ type: "action", action }); },
    setSetting(key, value) { post({ type: "action", action: { type: "setSetting", key, value } }); },
    web: webApi,
    log(...args) { post({ type: "log", level: "info", message: args.map(String).join(" ") }); },
  });
  Object.defineProperty(window, "darkroom", { value: api, writable: false, configurable: false });
  render();
})();
