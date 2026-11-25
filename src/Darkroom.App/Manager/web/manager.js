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
  const sorted = [...state.widgets].sort((a, b) => a.name.localeCompare(b.name, "ko"));
  ul.replaceChildren(...sorted.map((w) => el("li",
    { class: w.id === selected ? "selected" : "", onclick: () => { selected = w.id; render(); } },
    el("span", { class: "dot " + (w.error ? "err" : w.running ? "on" : "") }),
    el("div", { class: "grow" }, el("div", { class: "name" }, w.name), w.description ? el("div", { class: "sub" }, w.description) : null),
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
        ...spec.options.map((o, i) => el("option", { value: o, selected: o === value }, (spec.optionLabels && spec.optionLabels[i]) || o)));
    default:
      return el("input", { type: "text", value: value ?? "", onchange: (e) => set(e.target.value) });
  }
}

// ---------- 목록 편집기(날짜·바로가기·숫자) ----------
const LIST_TYPES = ["dates", "links", "numbers"];
let pendingFocus = null; // 방금 추가한 줄의 입력칸에 커서를 둔다 { id, key, index, field }

const todayPlus = (days) => { const d = new Date(Date.now() + days * 864e5); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`; };
const iconUrl = (target) => "https://assets.darkroom/icon?t=" + encodeURIComponent(target);
const isWeb = (t) => /^https?:/i.test(t || "");

function listEditor(w, key, spec) {
  const items = (Array.isArray(w.settings[key]) ? w.settings[key] : []).map((x) => (typeof x === "object" ? { ...x } : x));
  const max = spec.maxItems || 8;
  const save = (next) => send({ type: "setSetting", id: w.id, key, value: next });
  const full = items.length >= max;
  const focusAttr = (index, field) => pendingFocus && pendingFocus.id === w.id && pendingFocus.key === key && pendingFocus.index === index && pendingFocus.field === field;
  const remove = (i) => el("button", { type: "button", class: "icon-btn", title: "지우기", onclick: () => save(items.filter((_, j) => j !== i)) }, "✕");

  if (spec.type === "dates") {
    const rows = items.map((it, i) => el("div", { class: "list-row" + (it.pinned ? " pinned" : "") },
      el("button", { type: "button", class: "star" + (it.pinned ? " on" : ""), title: it.pinned ? "크게 보여 주는 날(다시 누르면 해제)" : "이 날을 크게 보여 주기",
        onclick: () => save(items.map((x, j) => ({ ...x, pinned: j === i ? !x.pinned : false }))) }, it.pinned ? "★" : "☆"),
      el("input", { type: "date", value: it.date, onchange: (e) => { if (e.target.value) save(items.map((x, j) => (j === i ? { ...x, date: e.target.value } : x))); } }),
      el("input", { type: "text", value: it.name || "", placeholder: "무슨 날인가요?", maxlength: 60, autofocus: focusAttr(i, "name"),
        onchange: (e) => save(items.map((x, j) => (j === i ? { ...x, name: e.target.value.trim() } : x))) }),
      el("label", { class: "check", title: "매년 같은 날 반복(생일·기념일)" },
        el("input", { type: "checkbox", checked: !!it.yearly, onchange: (e) => save(items.map((x, j) => (j === i ? { ...x, yearly: e.target.checked } : x))) }), "매년"),
      remove(i)));
    return el("div", { class: "list-editor" }, ...rows,
      items.length ? null : el("p", { class: "sub" }, "아직 날짜가 없습니다."),
      el("div", { class: "list-actions" },
        el("button", { type: "button", class: "small", disabled: full, onclick: () => {
          pendingFocus = { id: w.id, key, index: items.length, field: "name" };
          save([...items, { date: todayPlus(7), name: "", yearly: false, pinned: false }]);
        } }, "+ 날짜 추가")));
  }

  if (spec.type === "links") {
    const rows = items.map((it, i) => el("div", { class: "list-row" },
      el("span", { class: "link-icon" }, isWeb(it.target)
        ? "🌐"
        : el("img", { src: iconUrl(it.target), alt: "", onerror: (e) => e.target.replaceWith(document.createTextNode("📄")) })),
      el("input", { type: "text", value: it.name || "", placeholder: "이름", maxlength: 40, autofocus: focusAttr(i, "name"),
        onchange: (e) => save(items.map((x, j) => (j === i ? { ...x, name: e.target.value.trim() } : x))) }),
      isWeb(it.target)
        ? el("input", { type: "text", class: "url", value: it.target, placeholder: "https://", autofocus: focusAttr(i, "target"),
            onchange: (e) => { const v = e.target.value.trim(); if (v) save(items.map((x, j) => (j === i ? { ...x, target: /^[a-z]+:/i.test(v) ? v : "https://" + v } : x))); } })
        : el("span", { class: "path", title: it.target }, it.target.replace(/^.*[\\/]/, "") || it.target),
      remove(i)));
    return el("div", { class: "list-editor" }, ...rows,
      items.length ? null : el("p", { class: "sub" }, "아직 바로가기가 없습니다."),
      el("div", { class: "list-actions" },
        el("button", { type: "button", class: "small", disabled: full, onclick: () => send({ type: "pickApp", id: w.id, key }) }, "+ 앱"),
        el("button", { type: "button", class: "small ghost", disabled: full, onclick: () => send({ type: "pickFolder", id: w.id, key }) }, "+ 폴더"),
        el("button", { type: "button", class: "small ghost", disabled: full, onclick: () => {
          pendingFocus = { id: w.id, key, index: items.length, field: "target" };
          save([...items, { name: "웹사이트", target: "https://" }]);
        } }, "+ 웹 주소")));
  }

  // numbers: 칩 + 추가
  const unit = spec.unit || "분";
  const input = el("input", { type: "number", min: spec.min ?? "", max: spec.max ?? "", step: spec.step ?? 1, placeholder: unit, class: "num-input" });
  const add = () => {
    const v = Number(input.value);
    if (!(v > 0) || (spec.min != null && v < spec.min) || (spec.max != null && v > spec.max)) { toast(`${spec.min ?? 1}~${spec.max ?? 600} 사이로 넣어 주세요`, true); return; }
    if (items.includes(v)) return;
    save([...items, v].sort((a, b) => a - b));
  };
  input.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); add(); } });
  return el("div", { class: "list-editor" },
    el("div", { class: "chips" }, ...items.map((v, i) => el("span", { class: "chip" }, `${v}${unit}`,
      el("button", { type: "button", title: "지우기", onclick: () => save(items.filter((_, j) => j !== i)) }, "✕")))),
    full ? null : el("div", { class: "inline" }, input, el("button", { type: "button", class: "small", onclick: add }, "추가")));
}

function renderDetail() {
  const box = $("#detail");
  // 슬라이더·입력칸을 조작하는 중이면 다시 그리지 않는다(포커스·드래그 유지).
  const active = document.activeElement;
  if (active && box.contains(active) && active.matches("input[type=range], input[type=text], input[type=number], input[type=date], input:not([type])")) return;

  const w = state.widgets.find((x) => x.id === selected);
  if (!w) { box.replaceChildren(el("p", { class: "empty" }, "왼쪽에서 위젯을 고르세요.")); return; }

  const prop = (name, value) => send({ type: "setProp", id: w.id, prop: name, value });
  const opacityOut = el("span", { class: "sub" }, `${Math.round(w.opacity * 100)}%`);
  const modeNote = w.running && w.effectiveMode && w.effectiveMode !== w.windowMode
    ? el("div", { class: "sub" }, `이 PC에서는 '${w.effectiveMode}' 방식으로 대체 중`) : null;

  // widget.json에 적은 순서대로(객체 키 순서는 전달 중에 바뀔 수 있어 순서 목록을 따로 받는다)
  const settingKeys = (w.settingOrder || Object.keys(w.schema || {})).filter((k) => w.schema && w.schema[k]);
  const simpleKeys = settingKeys.filter((k) => !LIST_TYPES.includes(w.schema[k].type));
  const listKeys = settingKeys.filter((k) => LIST_TYPES.includes(w.schema[k].type));
  box.replaceChildren(...[
    el("div", { class: "title" }, el("h1", {}, w.name), switchInput(w.enabled, (on) => send({ type: "setEnabled", id: w.id, enabled: on }), w.enabled ? "켜짐" : "꺼짐")),
    w.description ? el("p", { class: "desc" }, w.description) : null,
    w.error ? el("div", { class: "error" }, w.error) : null,

    // 목록 설정(날짜·바로가기 등)은 이 위젯의 핵심이라 맨 위에
    ...listKeys.flatMap((k) => [
      el("h3", {}, w.schema[k].label || k),
      listEditor(w, k, w.schema[k]),
      w.schema[k].hint ? el("p", { class: "hint" }, w.schema[k].hint) : null,
    ]),

    simpleKeys.length ? el("h3", {}, "설정") : null,
    simpleKeys.length
      ? el("div", { class: "grid" }, ...simpleKeys.flatMap((k) => [
          el("label", { class: "lbl" }, w.schema[k].label || k), settingControl(w, k, w.schema[k])]))
      : null,

    el("h3", {}, "보기"),
    el("div", { class: "grid" },
      el("label", { class: "lbl" }, "놓는 곳"),
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

    // 만드는 사람용 정보는 접어 둔다
    el("details", { class: "advanced" },
      el("summary", {}, "자세히"),
      el("div", { class: "grid" },
        el("span", { class: "lbl" }, "버전"), el("span", {}, "v" + w.version),
        el("span", { class: "lbl" }, "ID"), el("span", {}, w.id),
        el("span", { class: "lbl" }, "받는 데이터"), el("span", {}, w.subscribe.join(", ") || "없음"),
        el("span", { class: "lbl" }, "권한"), el("span", {}, PERMS(w.permissions)),
      ),
      el("div", { class: "actions" },
        el("button", { class: "ghost small", onclick: () => send({ type: "openFolder", id: w.id }) }, "위젯 폴더 열기"))),
  ].filter((c) => c != null));

  // 방금 추가한 줄에 커서
  if (pendingFocus && pendingFocus.id === w.id) {
    const target = box.querySelector("input[autofocus]");
    if (target) { target.focus(); target.select?.(); }
    pendingFocus = null;
  }
}

const PERMS = (list) => list.length
  ? list.map((p) => ({ network: "인터넷 사용", launch: "앱·파일 열기", notify: "Windows 알림", calendar: "캘린더 일정 읽기" }[p] || p)).join(", ")
  : "없음 (인터넷 차단)";

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
  renderThemePicker();
  renderFontPicker();
  $("#allLocked").checked = state.allLocked;
  $("#allHidden").checked = state.allHidden;
}

// ---------- 오른쪽 아래: 환경 ----------
function renderEnv() {
  // 사진 위젯 넘김 간격은 사진 위젯이 켜져 있을 때만
  $("#photosIntervalRow").hidden = !state.widgets.some((w) => w.enabled && w.subscribe.some((k) => k.startsWith("photos.")));
  const p = state.providers;
  $("#locNow").textContent = `${p.locationName || "이름 없는 위치"} · ${Number(p.latitude).toFixed(2)}, ${Number(p.longitude).toFixed(2)}`;
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

// 도시 이름으로 위치 찾기 → 고르면 바로 적용
$("#citySearch").addEventListener("submit", (e) => {
  e.preventDefault();
  const q = $("#cityQuery").value.trim();
  if (!q) return;
  $("#cityResults").hidden = false;
  $("#cityResults").replaceChildren(el("li", { class: "sub" }, "찾는 중…"));
  send({ type: "searchCity", query: q });
});
function showCities(results) {
  const ul = $("#cityResults");
  ul.hidden = false;
  if (!results || !results.length) { ul.replaceChildren(el("li", { class: "sub" }, "찾지 못했습니다. 다른 이름으로 해 보세요(인터넷 필요).")); return; }
  ul.replaceChildren(...results.map((c) => el("li", { onclick: () => {
    $("#locName").value = c.name; $("#lat").value = c.latitude; $("#lon").value = c.longitude;
    sendProviders();
    ul.hidden = true; $("#cityQuery").value = "";
    toast(`위치를 '${c.name}'(으)로 바꿨습니다`);
  } }, el("b", {}, c.name), el("span", { class: "sub" }, c.region))));
}
$("#pickFolder").addEventListener("click", () => send({ type: "pickPhotosFolder" }));

// ---------- 오른쪽: 캘린더 ----------
function renderCalendar() {
  const c = state.calendar;
  $("#calOff").hidden = c.connected;
  $("#calOn").hidden = !c.connected;
  $("#calConnect").disabled = c.busy;
  $("#calConnect").textContent = c.busy ? "연결하는 중…" : "연결";
  if (!c.connected) {
    if (!c.isNaver && document.activeElement !== $("#calServer") && !$("#calServer").value) $("#calServer").value = c.server || "";
    return;
  }
  $("#calWho").textContent = `${c.isNaver ? "네이버 캘린더" : "캘린더"} · ${c.username}`;
  const when = c.updatedAt ? new Date(c.updatedAt).toLocaleTimeString("ko-KR", { hour: "numeric", minute: "2-digit" }) : null;
  $("#calStatus").className = "hint top" + (c.error ? " err" : "");
  $("#calStatus").textContent = c.error ? c.error
    : c.count != null ? `일정 ${c.count}개 · ${when} 받음`
    : c.used ? "일정을 받는 중…" : "연결됨. 디데이나 일정 위젯을 켜면 일정을 받아 옵니다.";
  $("#calList").replaceChildren(...c.calendars.map((x) => el("li", {},
    el("span", { class: "swatch", style: x.color ? `background:${x.color}` : "" }),
    el("span", { class: "grow", title: x.name }, x.name),
    switchInput(x.shown, (on) => send({ type: "calendarShow", id: x.id, shown: on })))));
}
$("#calForm").addEventListener("submit", (e) => {
  e.preventDefault();
  const username = $("#calUser").value.trim().replace(/@naver\.com$/i, "");
  const password = $("#calPass").value.replace(/\s+/g, "");
  if (!username || !password) { calMsg("아이디와 애플리케이션 비밀번호를 모두 넣어 주세요.", true); return; }
  calMsg("");
  send({ type: "calendarConnect", username, password, server: $("#calServer").value.trim() });
});
function calMsg(text, err = false) { $("#calMsg").textContent = text; $("#calMsg").className = "hint" + (err ? " err" : ""); }
$("#calHelp").addEventListener("click", () => send({ type: "openNaverSecurity" }));
$("#calRefresh").addEventListener("click", () => { send({ type: "calendarRefresh" }); toast("일정을 새로 받습니다"); });
$("#calDisconnect").addEventListener("click", () => {
  if (confirm("캘린더 연결을 해제할까요? 저장한 비밀번호와 받아 둔 일정을 지웁니다.")) send({ type: "calendarDisconnect" });
});

// ---------- 오른쪽: 해 시간(골든아워 알림·색 자동 전환) ----------
function renderSunTime() {
  const s = state.sunTime;
  $("#alertOn").checked = s.alerts.enabled;
  const lead = String(s.alerts.leadMinutes);
  if (![...$("#alertLead").options].some((o) => o.value === lead))
    $("#alertLead").append(el("option", { value: lead }, `${lead}분 전`)); // 목록에 없는 값(직접 고친 설정)도 보이게
  $("#alertLead").value = lead;
  $("#alertMorning").checked = s.alerts.morning;
  $("#alertRain").checked = s.alerts.rain;
  $("#alertLead").disabled = $("#alertMorning").disabled = !s.alerts.enabled;
  $("#themeAuto").checked = s.themeAuto;
  $("#themeAutoHint").textContent = "골든아워 Warm Negative · 블루아워 Blue Hour · 밤 Safelight Red, 낮에는 위에서 고른 색."
    + (s.themeAuto && s.effectiveTheme ? ` 지금: ${s.effectiveTheme}` : "");
}
function sendAlerts() {
  send({ type: "setAlerts", value: {
    enabled: $("#alertOn").checked, leadMinutes: Number($("#alertLead").value), morning: $("#alertMorning").checked, rain: $("#alertRain").checked,
  } });
}
$("#alertOn").addEventListener("change", sendAlerts);
$("#alertLead").addEventListener("change", sendAlerts);
$("#alertMorning").addEventListener("change", sendAlerts);
$("#alertRain").addEventListener("change", sendAlerts);
$("#themeAuto").addEventListener("change", (e) => send({ type: "setThemeAuto", value: e.target.checked }));

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
  $("#pauseFull").checked = state.app.pauseOnFullscreen;
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
$("#pauseFull").addEventListener("change", (e) => send({ type: "setPauseOnFullscreen", value: e.target.checked }));
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
  renderSunTime();
  renderCalendar();
  renderEnv();
  renderApp();
}

// ---------- 색 테마 고르기: 테마마다 카드 색·글자색·강조색을 작은 카드로 보여 준다 ----------
const ACCENTS = ["#ff9f45", "#ff5a4a", "#ff6ec7", "#b388ff", "#6e9bff", "#4fc3f7", "#4dd0b5", "#7ed957", "#ffd54a", "#ffffff"];
const alphaOf = (rgba) => { const m = /rgba?\(([^)]*)\)/.exec(rgba || ""); const p = m ? m[1].split(",") : []; return p.length > 3 ? Number(p[3]) : 1; };
const withAlpha = (rgba, a) => { const m = /rgba?\(([^)]*)\)/.exec(rgba || ""); if (!m) return rgba; return `rgba(${m[1].split(",").slice(0, 3).join(",")},${a})`; };

// 테마 카드는 지금 고른 글꼴로 그린다(글꼴 카드는 지금 테마 색으로) → 서로 맞춰 본 모습을 바로 볼 수 있다
function themeSwatch(t, custom, big) {
  const tok = t.tokens;
  const accent = (custom && custom.accent) || tok.accent;
  const bg = custom && custom.cardAlpha != null ? withAlpha(tok.bg, custom.cardAlpha) : tok.bg;
  const f = state.fonts.find((x) => x.key === state.font);
  if (f) loadFontCss(f.key);
  const num = f ? `font-family:${f.num};font-weight:${f.numWeight};letter-spacing:${f.numTracking};font-variation-settings:${f.numVariation || "normal"};`
    + `font-size:${Math.round((big ? 22 : 12) * (f.numScale || 1))}px` : "";
  const style = `--sw-bg:${bg};--sw-fg:${tok.fg};--sw-muted:${tok.muted};--sw-accent:${accent};--sw-radius:${Math.min(parseInt(tok.radius) || 14, 12)}px`;
  if (!big) return el("span", { class: "swatch-card", style }, el("span", { class: "sw-time", style: num }, "12:30"), el("span", { class: "sw-bar" }));
  return el("span", { class: "swatch-card big", style },
    el("span", { class: "sw-top" }, el("span", { class: "sw-time", style: num }, "12:30"), el("span", { class: "sw-bar" })),
    el("span", { class: "sw-ui", style: f ? `font-family:${f.ui}` : "" }, "다가오는 날"));
}

function renderThemePicker() {
  const cur = state.themes.find((t) => t.key === state.theme) || state.themes[0];
  const custom = state.themeCustom || {};
  $("#themeBtn").replaceChildren(themeSwatch(cur, custom), el("span", {}, cur ? cur.name : ""), el("span", { class: "caret" }, "▾"));
  $("#themeGrid").replaceChildren(...state.themes.map((t) => el("button", {
    type: "button", class: "theme-tile" + (t.key === state.theme ? " on" : ""), title: t.name,
    onclick: () => send({ type: "setTheme", key: t.key }),
  }, themeSwatch(t, null, true), el("span", { class: "tile-name" }, t.name))));
  // 강조색: 테마 기본 + 고른 색들 + 직접 고르기
  const pick = (accent) => send({ type: "setThemeCustom", value: { accent, cardAlpha: custom.cardAlpha ?? null } });
  const own = custom.accent && !ACCENTS.includes(custom.accent);
  const colorInput = el("input", { type: "color", value: custom.accent || (cur && cur.tokens.accent) || "#ff9f45", onchange: (e) => pick(e.target.value) });
  $("#accentChips").replaceChildren(
    el("button", { type: "button", class: "accent-chip default" + (!custom.accent ? " on" : ""), title: "테마 기본 강조색", onclick: () => pick(null),
      style: `--c:${cur ? cur.tokens.accent : "#888"}` }, "기본"),
    ...ACCENTS.map((c) => el("button", { type: "button", class: "accent-chip" + (custom.accent === c ? " on" : ""), title: c, style: `--c:${c}`, onclick: () => pick(c) })),
    el("label", { class: "accent-chip custom" + (own ? " on" : ""), title: "직접 고르기", style: own ? `--c:${custom.accent}` : "" }, "+", colorInput));
  // 카드 진하기
  const a = custom.cardAlpha ?? alphaOf(cur && cur.tokens.bg);
  if (document.activeElement !== $("#cardAlpha")) $("#cardAlpha").value = Math.round(a * 100);
  $("#cardAlphaOut").textContent = `${Math.round(a * 100)}%` + (custom.cardAlpha == null ? " (테마 기본)" : "");
  $("#cardAlphaReset").disabled = custom.cardAlpha == null;
}
$("#themeBtn").addEventListener("click", (e) => { e.stopPropagation(); $("#fontPop").hidden = true; $("#themePop").hidden = !$("#themePop").hidden; });
$("#themePop").addEventListener("click", (e) => e.stopPropagation());
document.addEventListener("click", () => { $("#themePop").hidden = true; });
document.addEventListener("keydown", (e) => { if (e.key === "Escape") $("#themePop").hidden = true; });
$("#cardAlpha").addEventListener("input", (e) => { $("#cardAlphaOut").textContent = `${e.target.value}%`; });
$("#cardAlpha").addEventListener("change", (e) => {
  const custom = (state && state.themeCustom) || {};
  send({ type: "setThemeCustom", value: { accent: custom.accent ?? null, cardAlpha: Number(e.target.value) / 100 } });
});
$("#cardAlphaReset").addEventListener("click", () => {
  const custom = (state && state.themeCustom) || {};
  send({ type: "setThemeCustom", value: { accent: custom.accent ?? null, cardAlpha: null } });
});
// ---------- 글꼴 조합 고르기: 조합마다 큰 숫자(숫자 글꼴)와 한글 문장(본문 글꼴)을 그 글꼴로 보여 준다 ----------
const fontCssLoaded = new Set(["pretendard"]);
function loadFontCss(key) {
  if (fontCssLoaded.has(key)) return;
  fontCssLoaded.add(key);
  document.head.append(el("link", { rel: "stylesheet", href: `https://assets.darkroom/fonts/packs/${encodeURIComponent(key)}/font.css` }));
}
// 위젯과 같은 규칙으로 그린다: 큰 숫자는 똑바로(시계·디데이·남은 시간), 초·노출값 줄은 조합의 기울기(numStyle),
// 본문은 본문 글꼴, 배경·글자색은 지금 테마의 카드 색.
function fontSample(f, big) {
  const s = f.numScale || 1;
  const base = `font-family:${f.num};font-weight:${f.numWeight};font-variation-settings:${f.numVariation || "normal"};`;
  const time = el("span", { class: "fs-num", style: `${base}letter-spacing:${f.numTracking};font-size:${Math.round((big ? 40 : 15) * s)}px` }, "12:30");
  if (!big) return el("span", { class: "font-sample" }, time);
  const theme = state.themes.find((t) => t.key === state.theme) || state.themes[0];
  const tok = theme ? theme.tokens : {};
  const custom = state.themeCustom || {};
  const bg = custom.cardAlpha != null ? withAlpha(tok.bg, custom.cardAlpha) : tok.bg;
  return el("span", { class: "font-sample big", style: `--fs-bg:${bg};--fs-fg:${tok.fg};--fs-muted:${tok.muted}` },
    el("span", { class: "fs-row" }, time,
      el("span", { class: "fs-sec", style: `${base}font-style:${f.numStyle};font-size:${Math.round(16 * s)}px` }, "45")),
    el("span", { class: "fs-exp", style: `${base}font-style:${f.numStyle};font-size:${Math.round(13 * s)}px` }, "f/8 · 1/500 · ISO 100"),
    el("span", { class: "fs-ui", style: `font-family:${f.ui}` }, "다가오는 날 · 11월 25일 수요일"));
}
function renderFontPicker() {
  const cur = state.fonts.find((f) => f.key === state.font) || state.fonts[0];
  if (!cur) return;
  loadFontCss(cur.key);
  $("#fontBtn").replaceChildren(fontSample(cur, false), el("span", { style: `font-family:${cur.ui}` }, cur.name), el("span", { class: "caret" }, "▾"));
  $("#fontGrid").replaceChildren(...state.fonts.map((f) => el("button", {
    type: "button", class: "font-tile" + (f.key === state.font ? " on" : ""), title: f.description || f.name,
    onclick: () => { send({ type: "setFont", key: f.key }); toast("글꼴을 바꿨습니다"); },
  }, fontSample(f, true), el("span", { class: "tile-name" }, f.description ? `${f.name} · ${f.description}` : f.name))));
}
$("#fontBtn").addEventListener("click", (e) => {
  e.stopPropagation();
  state.fonts.forEach((f) => loadFontCss(f.key)); // 처음 열 때 모든 조합의 글꼴을 불러온다(보이는 글자만 받는다)
  $("#themePop").hidden = true;
  $("#fontPop").hidden = !$("#fontPop").hidden;
});
$("#fontPop").addEventListener("click", (e) => e.stopPropagation());
document.addEventListener("click", () => { $("#fontPop").hidden = true; });
document.addEventListener("keydown", (e) => { if (e.key === "Escape") $("#fontPop").hidden = true; });
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
    else if (m.type === "picked") {
      // 고른 앱·폴더를 바로가기 목록 끝에 붙인다
      const w = state && state.widgets.find((x) => x.id === m.id);
      if (!w) return;
      const list = Array.isArray(w.settings[m.key]) ? w.settings[m.key] : [];
      if (list.some((x) => x.target === m.item.target)) { toast("이미 있는 항목입니다"); return; }
      send({ type: "setSetting", id: m.id, key: m.key, value: [...list, m.item] });
      toast(`'${m.item.name}' 추가함`);
    }
    else if (m.type === "cities") showCities(m.results);
    else if (m.type === "section" && m.section === "calendar") {
      const box = $("#calSection");
      box.scrollIntoView({ behavior: "smooth", block: "start" });
      box.classList.remove("flash"); void box.offsetWidth; box.classList.add("flash");
      if (!state || !state.calendar.connected) setTimeout(() => $("#calUser").focus(), 400);
    }
    else if (m.type === "calendarResult") {
      if (m.ok) { $("#calPass").value = ""; calMsg(""); toast("캘린더를 연결했습니다"); }
      else calMsg(m.message || "연결하지 못했습니다.", true);
    }
  });
  send({ type: "ready" });
}
