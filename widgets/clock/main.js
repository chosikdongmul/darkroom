// Exposure Clock — 시계 + 지금 배경화면 사진의 노출값.
// f값·셔터·ISO는 배경화면 사진의 EXIF(wallpaper.current). 없는 값은 f/4 · 1/250 · ISO 100으로 채운다.
// 아래 줄은 그 노출의 EV(ISO 100 기준)와 그 밝기에 맞는 빛, 오른쪽 눈금은 EV 2~17에서의 위치.
const $ = (id) => document.getElementById(id);
const pad = (n) => String(n).padStart(2, "0");
const DAYS = ["일요일", "월요일", "화요일", "수요일", "목요일", "금요일", "토요일"];

const DEFAULTS = { aperture: "f/4", shutter: "1/250", iso: 100 };
const EV_MIN = 2, EV_MAX = 17;

// EV(ISO 100) → 그 노출이 맞는 대표적인 빛
const LIGHT = [
  [16, "눈·모래 위 햇빛"], [15, "맑은 날 햇빛"], [14, "옅은 구름"], [13, "흐린 날"],
  [12, "흐린 날 그늘"], [11, "해 질 녘"], [10, "해 진 직후"], [9, "밝은 밤거리"],
  [8, "밝은 실내"], [7, "실내"], [5, "어두운 실내"], [-99, "야경·촛불"],
];

const fNumber = (s) => parseFloat(String(s).replace(/^f\//i, ""));
function seconds(s) {
  const m = String(s).match(/^1\/(\d+(?:\.\d+)?)/);
  return m ? 1 / parseFloat(m[1]) : parseFloat(s);
}

let settings = window.darkroom ? darkroom.settings : { format24h: true, showSeconds: true, showDecor: true };
let lastMs = Date.now();
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
  const e = exif || {};
  const aperture = e.aperture || DEFAULTS.aperture;
  const shutter = e.shutter || DEFAULTS.shutter;
  const iso = e.iso || DEFAULTS.iso;
  $("exp").textContent = `${aperture} · ${shutter} · ISO ${iso}`;

  const n = fNumber(aperture), t = seconds(shutter);
  if (!(n > 0 && t > 0 && iso > 0)) { $("ev").textContent = ""; return; }
  const ev = Math.log2((n * n) / t) - Math.log2(iso / 100);
  const rounded = Math.round(ev);
  $("ev").textContent = `EV ${rounded} · ${LIGHT.find(([min]) => rounded >= min)[1]}`;
  const p = Math.max(0, Math.min(1, (ev - EV_MIN) / (EV_MAX - EV_MIN)));
  $("needle").style.transform = `translateX(${p * 100}%)`;
}

if (window.darkroom) {
  darkroom.subscribe("time.now", (v) => { lastMs = v.epochMs; renderTime(); });
  darkroom.subscribe("wallpaper.current", (w) => { exif = w && w.exif; renderExposure(); });
  darkroom.onSettingsChanged((s) => { settings = s; renderTime(); renderExposure(); });
} else {
  setInterval(() => { lastMs = Date.now(); renderTime(); }, 1000); // 브라우저로 직접 열었을 때
}
renderTime();
renderExposure();
