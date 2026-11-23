// Exposure Clock — 시계 + 지금 배경화면 사진의 노출값 + 지금 빛.
// 노출값 줄(f값·셔터·ISO)은 지금 배경화면 사진의 EXIF(wallpaper.current). 없는 값은 "–", 하나도 없으면 흐리게.
// 아래 줄은 해의 고도로 추정한 맑은 날 기준 노출값(EV100), 노출계 바늘은 "30분 뒤 빛이 몇 스톱 바뀌는지"(-2 ~ +2).
const $ = (id) => document.getElementById(id);
const pad = (n) => String(n).padStart(2, "0");
const DAYS = ["일요일", "월요일", "화요일", "수요일", "목요일", "금요일", "토요일"];

// 해 고도(°) → 맑은 날 EV100. Sunny 16(EV15)에서 시작해 박명·밤으로 내려가는 근사 곡선.
const EV_CURVE = [[-18, 3], [-12, 4], [-8, 7], [-4, 10], [0, 11.5], [6, 13], [15, 14], [30, 15], [90, 15]];
function ev100(alt) {
  if (alt <= EV_CURVE[0][0]) return EV_CURVE[0][1];
  for (let i = 1; i < EV_CURVE.length; i++) {
    const [a1, e1] = EV_CURVE[i - 1], [a2, e2] = EV_CURVE[i];
    if (alt <= a2) return e1 + ((alt - a1) / (a2 - a1)) * (e2 - e1);
  }
  return 15;
}

let settings = window.darkroom ? darkroom.settings : { format24h: true, showSeconds: true, showDecor: true };
let lastMs = Date.now();
let sun = null;
let exif = null; // 지금 배경화면의 EXIF { aperture: "f/2.8", shutter: "1/250", iso: 400, ... } 또는 null

function renderTime() {
  const d = new Date(lastMs);
  let h = d.getHours();
  let ampm = "";
  if (!settings.format24h) { ampm = h < 12 ? "오전" : "오후"; h = h % 12 || 12; }
  $("hm").textContent = `${settings.format24h ? pad(h) : h}:${pad(d.getMinutes())}`;
  $("sec").textContent = pad(d.getSeconds());
  $("sec").hidden = !settings.showSeconds;
  $("ampm").textContent = ampm;
  $("ampm").hidden = !ampm;
  $("md").textContent = `${d.getMonth() + 1}월 ${d.getDate()}일`;
  $("day").textContent = DAYS[d.getDay()];
}

function renderExposure() {
  $("decor").hidden = !settings.showDecor;
  // 없는 값은 "–"로 자리만 표시하고, 셋 다 없으면 흐리게
  const e = exif || {};
  $("exp").textContent = [e.aperture || "f/–", e.shutter || "–", `ISO ${e.iso || "–"}`].join(" · ");
  $("exp").classList.toggle("none", !e.aperture && !e.shutter && !e.iso);
  if (!sun) return;
  const ev = ev100(sun.altitude);

  const delta = sun.altitudeIn30Min == null ? 0 : ev100(sun.altitudeIn30Min) - ev;
  const clamped = Math.max(-2, Math.min(2, delta));
  $("needle").style.transform = `translateX(${((clamped + 2) / 4) * 100}%)`;
  const sign = delta > 0.05 ? "+" : delta < -0.05 ? "−" : "±";
  $("ev").textContent = `EV ${ev.toFixed(1)} · 30분 뒤 ${sign}${Math.abs(delta).toFixed(1)}스톱 · 맑음 기준`;
}

if (window.darkroom) {
  darkroom.subscribe("time.now", (v) => { lastMs = v.epochMs; renderTime(); });
  darkroom.subscribe("sun.position", (p) => { sun = p; renderExposure(); });
  darkroom.subscribe("wallpaper.current", (w) => { exif = w && w.exif; renderExposure(); });
  darkroom.onSettingsChanged((s) => { settings = s; renderTime(); renderExposure(); });
} else {
  setInterval(() => { lastMs = Date.now(); renderTime(); }, 1000); // 브라우저로 직접 열었을 때
}
renderTime();
renderExposure();
