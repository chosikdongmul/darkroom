// Darkroom Manager — 호스트가 보내는 state를 그리고, 바꾼 값을 바로 호스트로 보낸다(저장 버튼 없음).
"use strict";

const wv = window.chrome && window.chrome.webview;
const send = (msg) => wv && wv.postMessage(msg);
const $ = (sel, root = document) => root.querySelector(sel);
const el = (tag, props = {}, ...children) => {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === "class") e.className = v;
    else if (k === "dataset") Object.assign(e.dataset, v);
    else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
    else if (v === true) e.setAttribute(k, "");
    else if (v !== false && v != null) e.setAttribute(k, v);
  }
  for (const c of children.flat()) if (c != null) e.append(c);
  return e;
};

const MODES = [
  ["desktop", "바탕화면 고정"],
  ["bottom", "맨 아래"],
  ["normal", "일반 창"],
  ["topmost", "항상 위"],
];

let state = null;
let selected = null;

function toast(text, isError = false) {
  const t = $("#toast");
  t.textContent = text;
  t.className = "show" + (isError ? " err" : "");
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.className = ""), isError ? 4000 : 1800);
}

function switchInput(checked, onchange, text) {
  return el("label", { class: "switch" },
    el("input", { type: "checkbox", checked, onchange: (e) => onchange(e.target.checked) }),
    el("span"), text || "");
}

// ---------- 왼쪽: 위젯 목록 ----------
function renderWidgets() {
  const ul = $("#widgets");
  ul.replaceChildren(...state.widgets.map((w) => el("li",
    { class: w.id === selected ? "selected" : "", onclick: () => { selected = w.id; render(); } },
    el("span", { class: "dot " + (w.error ? "err" : w.running ? "on" : "") }),
    el("div", { class: "grow" }, el("div", { class: "name" }, w.name)),
    el("span", { onclick: (e) => e.stopPropagation() },
      switchInput(w.enabled, (on) => send({ type: "setEnabled", id: w.id, enabled: on }))),
  )));
  if (!state.widgets.length) ul.append(el("li", { class: "sub" }, "설치된 위젯이 없습니다."));

  $("#invalid").replaceChildren(...state.invalid.map((i) =>
    el("div", { class: "invalid" }, el("b", {}, `widgets/${i.folder}`), i.error)));
}

// ---------- 가운데: 선택한 위젯 ----------
function settingControl(w, key, spec) {
  const value = w.settings[key];
  const set = (v) => send({ type: "setSetting", id: w.id, key, value: v });
  switch (spec.type) {
    case "bool":
      return switchInput(!!value, set);
    case "number": {
      if (spec.min != null && spec.max != null) {
        const out = el("span", { class: "sub" }, String(value));
        return el("div", { class: "inline" },
          el("input", { type: "range", min: spec.min, max: spec.max, step: spec.step ?? 1, value,
            oninput: (e) => (out.textContent = e.target.value), onchange: (e) => set(Number(e.target.value)) }), out);
      }
      return el("input", { type: "number", value, step: spec.step ?? "any", onchange: (e) => set(Number(e.target.value)) });
    }
    case "choice":
      return el("select", { onchange: (e) => set(e.target.value) },
        ...spec.options.map((o) => el("option", { value: o, selected: o === value }, o)));
    default:
      return el("input", { type: "text", value: value ?? "", onchange: (e) => set(e.target.value) });
  }
}

function renderDetail() {
  const box = $("#detail");
  // 슬라이더·입력칸을 조작하는 중이면 다시 그리지 않는다(포커스·드래그 유지).
  const active = document.activeElement;
  if (active && box.contains(active) && active.matches("input[type=range], input[type=text], input[type=number], input:not([type])")) return;

  const w = state.widgets.find((x) => x.id === selected);
  if (!w) { box.replaceChildren(el("p", { class: "empty" }, "왼쪽에서 위젯을 고르세요.")); return; }

  const prop = (name, value) => send({ type: "setProp", id: w.id, prop: name, value });
  const opacityOut = el("span", { class: "sub" }, `${Math.round(w.opacity * 100)}%`);
  const modeNote = w.running && w.effectiveMode && w.effectiveMode !== w.windowMode
    ? el("div", { class: "sub" }, `이 PC에서는 '${w.effectiveMode}' 방식으로 대체 중`) : null;

  const settingKeys = Object.keys(w.schema || {});
  box.replaceChildren(...[
    el("div", { class: "title" }, el("h1", {}, w.name), el("span", { class: "ver" }, "v" + w.version)),
    el("div", { class: "meta" }, w.id + " · " + w.folder),
    w.error ? el("div", { class: "error" }, w.error) : null,

    el("h3", {}, "창"),
    el("div", { class: "grid" },
      el("label", {}, "켜기"), switchInput(w.enabled, (on) => send({ type: "setEnabled", id: w.id, enabled: on })),
      el("label", { class: "lbl" }, "창 모드"),
      el("div", {},
        el("select", { onchange: (e) => prop("windowMode", e.target.value) },
          ...MODES.map(([v, t]) => el("option", { value: v, selected: v === w.windowMode }, t))),
        modeNote),
      el("label", { class: "lbl" }, "불투명도"),
      el("div", { class: "inline" },
        el("input", { type: "range", min: 20, max: 100, value: Math.round(w.opacity * 100),
          oninput: (e) => { opacityOut.textContent = e.target.value + "%"; prop("opacity", Number(e.target.value) / 100); } }),
        opacityOut),
      el("label", { class: "lbl" }, "위치 잠금"), switchInput(w.locked, (v) => prop("locked", v)),
      el("label", { class: "lbl" }, "마우스 올리면 흐리게"), switchInput(w.hoverFade, (v) => prop("hoverFade", v)),
    ),

    el("h3", {}, "설정"),
    settingKeys.length
      ? el("div", { class: "grid" }, ...settingKeys.flatMap((k) => [
          el("label", { class: "lbl" }, w.schema[k].label || k), settingControl(w, k, w.schema[k])]))
      : el("p", { class: "sub" }, "이 위젯은 설정이 없습니다."),

    el("h3", {}, "정보"),
    el("div", { class: "grid" },
      el("span", { class: "lbl" }, "구독 데이터"), el("span", {}, w.subscribe.join(", ") || "없음"),
      el("span", { class: "lbl" }, "권한"), el("span", {}, w.permissions.join(", ") || "없음 (외부 네트워크 차단)"),
    ),
    el("div", { class: "actions" },
      el("button", { class: "ghost", onclick: () => send({ type: "openFolder", id: w.id }) }, "위젯 폴더 열기")),
  ].filter((c) => c != null));
}

// ---------- 오른쪽: 레이아웃 ----------
function renderLayouts() {
  $("#layouts").replaceChildren(...state.layouts.map((l) => el("li", {},
    el("div", { class: "grow" }, el("div", { class: "name" }, l.name), el("div", { class: "sub" }, `${l.widgets.length}개 위젯 · ${l.theme || "테마 유지"}`)),
    el("div", { class: "btns" },
      el("button", { class: "small", onclick: () => { send({ type: "loadLayout", key: l.key }); toast(`'${l.name}' 불러옴`); } }, "불러오기"),
      el("button", { class: "small danger", title: "삭제", onclick: () => { if (confirm(`'${l.name}' 레이아웃을 지울까요?`)) send({ type: "deleteLayout", key: l.key }); } }, "✕")),
  )));
  if (!state.layouts.length) $("#layouts").append(el("li", { class: "sub" }, "저장된 레이아웃이 없습니다."));
}

// ---------- 머리글 ----------
function renderHeader() {
  const sel = $("#theme");
  sel.replaceChildren(...state.themes.map((t) => el("option", { value: t.key, selected: t.key === state.theme }, t.name)));
  $("#font").replaceChildren(...state.fonts.map((f) =>
    el("option", { value: f.key, selected: f.key === state.font, title: f.description || "" }, f.description ? `${f.name} — ${f.description}` : f.name)));
  $("#allLocked").checked = state.allLocked;
  $("#allHidden").checked = state.allHidden;
}

// ---------- 오른쪽 아래: 환경 ----------
function renderEnv() {
  // 사진 위젯 넘김 간격은 사진 위젯이 켜져 있을 때만
  $("#photosIntervalRow").hidden = !state.widgets.some((w) => w.enabled && w.subscribe.some((k) => k.startsWith("photos.")));
  const p = state.providers;
  if (document.activeElement !== $("#photosFolder")) {
    $("#photosFolder").value = p.photosFolder || "";
    $("#photosFolder").placeholder = "기본: " + state.photosFolder;
  }
  if ($("#env").contains(document.activeElement)) return; // 입력 중이면 덮어쓰지 않음
  $("#locName").value = p.locationName;
  $("#lat").value = p.latitude;
  $("#lon").value = p.longitude;
  $("#photosInterval").value = p.photosIntervalSeconds;
}

function sendProviders() {
  send({ type: "setProviders", value: {
    locationName: $("#locName").value.trim(),
    latitude: Number($("#lat").value),
    longitude: Number($("#lon").value),
    photosFolder: $("#photosFolder").value.trim() || null,
    photosIntervalSeconds: Math.round(Number($("#photosInterval").value)),
  } });
}
$("#env").addEventListener("submit", (e) => {
  e.preventDefault();
  sendProviders();
  document.activeElement.blur();
  toast("환경 설정 적용");
});
$("#photosFolder").addEventListener("change", () => { sendProviders(); toast("사진 폴더를 바꿨습니다"); });
$("#pickFolder").addEventListener("click", () => send({ type: "pickPhotosFolder" }));

// ---------- 오른쪽: 배경화면 바꾸기 ----------
function renderWallpaper() {
  const w = state.wallpaper;
  $("#wallOn").checked = w.enabled;
  if (document.activeElement !== $("#wallInterval")) $("#wallInterval").value = w.intervalMinutes;
  $("#wallFit").value = w.fit;
  $("#wallNext").disabled = !w.enabled;
  $("#wallStatus").textContent = w.error ? w.error
    : w.enabled ? `${w.current ?? "준비 중"} · 후보 ${w.count}장` : "꺼짐";
}
function sendWallpaper() {
  send({ type: "setWallpaper", value: {
    enabled: $("#wallOn").checked,
    intervalMinutes: Math.round(Number($("#wallInterval").value) || 30),
    fit: $("#wallFit").value,
  } });
}
$("#wallOn").addEventListener("change", sendWallpaper);
$("#wallFit").addEventListener("change", sendWallpaper);
$("#wallInterval").addEventListener("change", sendWallpaper);
$("#wallNext").addEventListener("click", () => send({ type: "nextWallpaper" }));

// ---------- 오른쪽 맨 아래: 앱 ----------
function renderApp() {
  $("#autoStart").checked = state.app.autoStart;
  $("#checkUpdates").checked = state.app.checkUpdates;
  $("#appVersion").textContent = `Darkroom ${state.app.version}`;
  const u = state.app.update || {};
  const btn = $("#updateBtn");
  btn.disabled = ["checking", "downloading", "installing"].includes(u.state);
  btn.textContent = u.state === "available" ? (u.installed ? "지금 업데이트" : "다운로드 페이지") : "업데이트 확인";
  btn.classList.toggle("ghost", u.state !== "available");
  $("#updateText").textContent = {
    checking: "확인 중…",
    uptodate: "최신 버전입니다",
    available: `새 버전 ${u.latest}` + (u.message ? ` · ${u.message}` : ""),
    downloading: `${u.latest} 받는 중…`,
    installing: "설치 중… 끝나면 다시 시작합니다",
    failed: u.message || "",
  }[u.state] || "";

  // 상단 알림 줄: 새 버전이 있을 때만("나중에"를 누르면 이 창에서는 다시 안 보임)
  const busy = u.state === "downloading" || u.state === "installing";
  $("#updateBar").hidden = updateBarDismissed || !(u.state === "available" || busy);
  $("#updateBarText").textContent = busy ? $("#updateText").textContent : `새 버전 ${u.latest}이 나왔습니다.` + (u.message ? ` ${u.message}` : "");
  $("#updateBarBtn").hidden = busy;
  $("#updateBarBtn").textContent = u.installed ? "지금 업데이트" : "다운로드 페이지";
  $("#updateBarLater").hidden = busy;
}
let updateBarDismissed = false;
$("#updateBarBtn").addEventListener("click", () => send({ type: "installUpdate" }));
$("#updateBarLater").addEventListener("click", () => { updateBarDismissed = true; $("#updateBar").hidden = true; });
$("#checkUpdates").addEventListener("change", (e) => send({ type: "setCheckUpdates", value: e.target.checked }));
$("#updateBtn").addEventListener("click", () => {
  const u = (state && state.app.update) || {};
  send({ type: u.state === "available" ? "installUpdate" : "checkUpdate" });
});
$("#autoStart").addEventListener("change", (e) => {
  send({ type: "setAutoStart", value: e.target.checked });
  toast(e.target.checked ? "Windows를 켜면 Darkroom이 함께 시작됩니다" : "자동 실행을 껐습니다");
});
$("#openLogs").addEventListener("click", () => send({ type: "openLogs" }));

function render() {
  if (!state) return;
  if (!selected || !state.widgets.some((w) => w.id === selected)) selected = state.widgets[0]?.id ?? null;
  renderHeader();
  renderWidgets();
  renderDetail();
  renderLayouts();
  renderWallpaper();
  renderEnv();
  renderApp();
}

$("#theme").addEventListener("change", (e) => send({ type: "setTheme", key: e.target.value }));
$("#font").addEventListener("change", (e) => { send({ type: "setFont", key: e.target.value }); toast("글꼴을 바꿨습니다"); });
$("#allLocked").addEventListener("change", (e) => send({ type: "setAllLocked", value: e.target.checked }));
$("#allHidden").addEventListener("change", (e) => send({ type: "setAllHidden", value: e.target.checked }));
$("[data-open-folder]").addEventListener("click", () => send({ type: "openFolder" }));
$("#saveLayout").addEventListener("submit", (e) => {
  e.preventDefault();
  const name = $("#layoutName").value.trim() || `레이아웃 ${new Date().toLocaleString("ko-KR")}`;
  send({ type: "saveLayout", name });
  $("#layoutName").value = "";
  toast(`'${name}' 저장함`);
});
// 마우스 버튼을 누른 채로 DOM을 다시 그리면 클릭이 사라진다 → 버튼을 뗀 뒤로 미룬다.
let pointerDown = false;
let renderPending = false;
function scheduleRender() {
  if (pointerDown) { renderPending = true; return; }
  render();
}
document.addEventListener("pointerdown", () => { pointerDown = true; }, true);
document.addEventListener("pointerup", () => {
  pointerDown = false;
  if (renderPending) { renderPending = false; setTimeout(render, 0); }
}, true);
document.addEventListener("focusout", () => setTimeout(scheduleRender, 0)); // 입력을 마치면 최신 상태로

if (wv) {
  wv.addEventListener("message", (e) => {
    const m = e.data;
    if (m.type === "state") { state = m.state; scheduleRender(); }
    else if (m.type === "error") toast(m.message, true);
  });
  send({ type: "ready" });
}
