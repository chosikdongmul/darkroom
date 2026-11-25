// Exposure Clock — 시계 + 지금 밖에서 찍는다면 맞는 노출.
// 해의 고도로 맑은 날 밝기(EV, ISO 100 기준)를 추정하고 날씨만큼 뺀 뒤, 그 밝기에 맞는 조리개·셔터·ISO를 보여 준다.
// 날씨를 못 받았으면(인터넷 없음 등) 시간에 따른 맑은 날 기준 값만.
const $ = (id) => document.getElementById(id);
const pad = (n) => String(n).padStart(2, "0");
const DAYS = ["일요일", "월요일", "화요일", "수요일", "목요일", "금요일", "토요일"];
const EV_MIN = 2, EV_MAX = 17;
const WEATHER_MAX_AGE = 60 * 60 * 1000; // 1시간 넘게 못 받았으면 날씨는 빼고 계산

// 해 고도(°) → 맑은 날 EV. Sunny 16(EV 15)에서 박명·밤으로 내려가는 근사 곡선.
const EV_CURVE = [[-18, 3], [-12, 4], [-8, 7], [-4, 10], [0, 11.5], [6, 13], [15, 14], [30, 15], [90, 15]];
function clearSkyEv(alt) {
  if (alt <= EV_CURVE[0][0]) return EV_CURVE[0][1];
  for (let i = 1; i < EV_CURVE.length; i++) {
    const [a1, e1] = EV_CURVE[i - 1], [a2, e2] = EV_CURVE[i];
    if (alt <= a2) return e1 + ((alt - a1) / (a2 - a1)) * (e2 - e1);
  }
  return 15;
}

// WMO 날씨 코드 → 맑은 날보다 어두운 정도(스톱)
function weatherStops(code) {
  if (code <= 1) return 0;
  if (code === 2) return 1;
  if (code === 3 || code === 45 || code === 48) return 2;
  if ([71, 73, 75, 77, 85, 86].includes(code)) return 1.5;   // 눈: 흐려도 눈에 반사돼 밝다
  if ([51, 53, 55, 56, 57, 61, 80].includes(code)) return 2.5;
  if (code >= 95) return 3.5;
  return 3;                                                   // 비
}

// 밝기 → 조리개·셔터·ISO. 손으로 들고 찍을 수 있게 셔터가 1/60보다 느려지면 ISO를 올린다(최대 6400).
function program(ev) {
  const n = ev >= 13 ? 8 : ev >= 10 ? 5.6 : 2.8;
  let iso = 100, t = (n * n) / Math.pow(2, ev);
  while (t > 1 / 60 && iso < 6400) { iso *= 2; t /= 2; }
  return { n, iso, t };
}

const SHUTTERS = [1 / 8000, 1 / 4000, 1 / 2000, 1 / 1000, 1 / 500, 1 / 250, 1 / 125, 1 / 60, 1 / 30, 1 / 15, 1 / 8, 1 / 4, 1 / 2, 1, 2, 4, 8, 15, 30];
function shutterText(t) {
  let best = SHUTTERS[0];
  for (const s of SHUTTERS) if (Math.abs(Math.log2(s / t)) < Math.abs(Math.log2(best / t))) best = s;
  return best < 1 ? `1/${Math.round(1 / best)}` : `${best}s`;
}

let settings = window.darkroom ? darkroom.settings : { format24h: true, showSeconds: true, showDecor: true };
let lastMs = Date.now();
let sun = null;
let weather = null, weatherAt = 0;

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
  $("day").textContent = `${d.getFullYear()}년 ${DAYS[d.getDay()]}`;
}

let lastKey = "";
function renderExposure() {
  $("decor").hidden = !settings.showDecor;
  if (!sun) return;
  const fresh = weather && Date.now() - weatherAt < WEATHER_MAX_AGE;
  // 밤에는 구름이 밝기에 덜 영향을 준다 → 해가 지평선 아래로 내려갈수록 날씨 보정을 줄인다
  const daylight = Math.max(0, Math.min(1, (sun.altitude + 6) / 12));
  const ev = clearSkyEv(sun.altitude) - (fresh ? weatherStops(weather.code) * daylight : 0);
  const { n, iso, t } = program(ev);
  const exp = `f/${n} · ${shutterText(t)} · ISO ${iso}`;
  const note = `EV ${Math.round(ev)} · ${fresh ? weather.description : "맑은 날 기준"}`;
  const key = exp + note;
  if (key === lastKey) return; // 바뀔 때만 다시 그림
  lastKey = key;
  $("exp").textContent = exp;
  $("ev").textContent = note;
  const p = Math.max(0, Math.min(1, (ev - EV_MIN) / (EV_MAX - EV_MIN)));
  $("needle").style.transform = `translateX(${p * 100}%)`;
}

if (window.darkroom) {
  darkroom.subscribe("time.now", (v) => { lastMs = v.epochMs; renderTime(); if (new Date(lastMs).getSeconds() === 0) renderExposure(); });
  darkroom.subscribe("sun.position", (p) => { sun = p; renderExposure(); });
  darkroom.subscribe("weather.current", (w) => { weather = w; weatherAt = Date.now(); renderExposure(); });
  darkroom.onSettingsChanged((s) => { settings = s; lastKey = ""; renderTime(); renderExposure(); });
} else {
  setInterval(() => { lastMs = Date.now(); renderTime(); }, 1000); // 브라우저로 직접 열었을 때
}
renderTime();
renderExposure();
