import * as THREE from "three";
import CameraControls from "./lib/camera-controls.module.js";
import { mergeGeometries } from "./lib/BufferGeometryUtils.js";

// Interface do Dimensionador: painel lateral, visualizador da rede de sprinklers (3D e planta 2D) e comunicação com a janela (WebView2).
const $ = (id) => document.getElementById(id);
const host = window.chrome?.webview;
const fmt = (v, d = 2) => (v === null || v === undefined || Number.isNaN(Number(v))) ? "—" : Number(v).toLocaleString("pt-BR", { minimumFractionDigits: d, maximumFractionDigits: d });
const num = (s) => { const v = parseFloat(String(s ?? "").replace(",", ".")); return Number.isFinite(v) ? v : null; };
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const send = (msg) => host?.postMessage(msg);

let state = { path: "", dataBase: "", network: null, pumps: [], selectedPumpId: null, mode: "iso", area: new Set(), selecting: false, candidates: null, hidden: new Set(), isolated: null };

// ---------- Cena ----------
const viewport = $("viewport");
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setPixelRatio(window.devicePixelRatio);
viewport.appendChild(renderer.domElement);
const scene = new THREE.Scene();
const perspective = new THREE.PerspectiveCamera(45, 1, 0.05, 5000);
perspective.up.set(0, 0, 1);
perspective.position.set(60, -60, 50);
const ortho = new THREE.OrthographicCamera(-50, 50, 50, -50, -1000, 1000);
ortho.up.set(0, 1, 0);
let camera = perspective;
// Navegação igual à do Visor IFC (camera-controls): esquerdo orbita, roda pressionada move, roda aproxima na direção do cursor,
// direito abre o menu; o clique num elemento passa a ser o centro da órbita. Na planta 2D o esquerdo move e não há rotação.
CameraControls.install({ THREE });
function makeControls(cam, plan) {
  const c = new CameraControls(cam, renderer.domElement); const A = CameraControls.ACTION;
  c.mouseButtons.left = plan ? A.TRUCK : A.ROTATE; c.mouseButtons.middle = A.TRUCK; c.mouseButtons.right = A.NONE; c.mouseButtons.wheel = plan ? A.ZOOM : A.DOLLY;
  c.dollyToCursor = true; c.infinityDolly = !plan; c.minDistance = 0.3; c.smoothTime = 0.2; c.draggingSmoothTime = 0.125;
  if (plan) { c.azimuthRotateSpeed = 0; c.polarRotateSpeed = 0; c.minZoom = 0.05; c.maxZoom = 200; }
  c.enabled = false; return c;
}
const controls3d = makeControls(perspective, false), controls2d = makeControls(ortho, true);
let controls = controls3d; controls.enabled = true;
const clock = new THREE.Clock();
function orbitAt(point) { if (state.mode === "2d") return; const min = controls3d.minDistance; controls3d.minDistance = 0.01; controls3d.setOrbitPoint(point.x, point.y, point.z); controls3d.minDistance = min; }
scene.add(new THREE.HemisphereLight(0xffffff, 0x8a8f99, 2.2));
const sun = new THREE.DirectionalLight(0xffffff, 1.4); sun.position.set(40, -30, 80); scene.add(sun);
const world = new THREE.Group(); scene.add(world);
const toScene = (p) => new THREE.Vector3(...p).applyMatrix4(world.matrixWorld);

function resize() {
  const w = viewport.clientWidth, h = Math.max(viewport.clientHeight, 1);
  renderer.setSize(w, h, false);
  perspective.aspect = w / h; perspective.updateProjectionMatrix();
  const half = (ortho.top - ortho.bottom) / 2, cy = (ortho.top + ortho.bottom) / 2, cx = (ortho.left + ortho.right) / 2;
  ortho.left = cx - half * w / h; ortho.right = cx + half * w / h; ortho.updateProjectionMatrix();
}
new ResizeObserver(resize).observe(viewport);
function applyTheme() { scene.background = new THREE.Color(getComputedStyle(document.documentElement).getPropertyValue("--fundo3d").trim() || "#f4f6f9"); }
applyTheme();

const unitCylinder = new THREE.CylinderGeometry(1, 1, 1, 10);
const unitSphere = new THREE.SphereGeometry(1, 14, 10);
const unitBox = new THREE.BoxGeometry(1, 1, 1);
const up = new THREE.Vector3(0, 1, 0);
let objects = [];      // { mesh, kind, data, baseColor }
let selected = null;
let ifcGeometry = null; // Map GlobalId → BufferGeometry (geometria original do IFC, em metros relativos à rede)

const RAMP = [[0, 0x2f6fde], [0.33, 0x1fb3a5], [0.66, 0xf2c230], [1, 0xd8342b]];
function ramp(t) {
  t = Math.min(1, Math.max(0, t));
  for (let i = 1; i < RAMP.length; i++) if (t <= RAMP[i][0]) {
    const [t0, c0] = RAMP[i - 1], [t1, c1] = RAMP[i];
    return new THREE.Color(c0).lerp(new THREE.Color(c1), (t - t0) / (t1 - t0));
  }
  return new THREE.Color(RAMP.at(-1)[1]);
}

// ---------- Geometria original do IFC (web-ifc), com cache local ----------
function cacheDb() {
  return new Promise((resolve) => {
    try { const r = indexedDB.open("grelha-geometria", 1); r.onupgradeneeded = () => r.result.createObjectStore("modelos"); r.onsuccess = () => resolve(r.result); r.onerror = () => resolve(null); }
    catch { resolve(null); }
  });
}
async function cacheGet(key) {
  const db = await cacheDb(); if (!db) return null;
  return new Promise((resolve) => { const q = db.transaction("modelos").objectStore("modelos").get(key); q.onsuccess = () => resolve(q.result ?? null); q.onerror = () => resolve(null); });
}
async function cachePut(key, value) {
  const db = await cacheDb(); if (!db) return;
  await new Promise((resolve) => { const tx = db.transaction("modelos", "readwrite"); tx.objectStore("modelos").clear(); tx.objectStore("modelos").put(value, key); tx.oncomplete = resolve; tx.onerror = resolve; });
}
function toGeometry(item) {
  const geo = new THREE.BufferGeometry();
  geo.setAttribute("position", new THREE.BufferAttribute(item.pos, 3)); geo.setAttribute("normal", new THREE.BufferAttribute(item.nor, 3)); geo.setIndex(new THREE.BufferAttribute(item.idx, 1));
  return geo;
}
async function loadIfcGeometry(base, origin) {
  let key = null;
  try {
    const head = await fetch(`${base}.network.ifc`, { method: "HEAD" });
    if (!head.ok) return null;
    key = `${base}|${head.headers.get("last-modified")}|${head.headers.get("content-length")}`;
    const cached = await cacheGet(key);
    if (cached) return new Map(cached.map((item) => [item.id, toGeometry(item)]));
  } catch { /* segue sem cache */ }
  const geometry = await readIfcGeometry(base, origin);
  if (geometry && key) {
    const items = [...geometry].map(([id, g]) => ({ id, pos: g.attributes.position.array, nor: g.attributes.normal.array, idx: g.index.array }));
    cachePut(key, items);
  }
  return geometry;
}
async function readIfcGeometry(base, origin) {
  const busy = document.createElement("div"); busy.className = "carregando"; busy.innerHTML = `<div class="giro"></div><span>Carregando a geometria do IFC da rede…</span>`;
  viewport.parentElement.appendChild(busy);
  try {
    const response = await fetch(`${base}.network.ifc`); if (!response.ok) return null;
    const bytes = new Uint8Array(await response.arrayBuffer());
    const WebIFC = await import("./lib/web-ifc-api.js");
    const api = new WebIFC.IfcAPI(); api.SetWasmPath("./lib/"); await api.Init();
    const model = api.OpenModel(bytes, { COORDINATE_TO_ORIGIN: false });
    // web-ifc entrega em metros com Y para cima; volta para Z para cima e para a origem da rede.
    const toNetwork = new THREE.Matrix4().makeTranslation(-origin.X, -origin.Y, -origin.Z)
      .multiply(new THREE.Matrix4().set(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1));
    const parts = new Map();
    api.StreamAllMeshes(model, (flat) => {
      const pieces = [];
      for (let i = 0; i < flat.geometries.size(); i++) {
        const placed = flat.geometries.get(i);
        const g = api.GetGeometry(model, placed.geometryExpressID);
        const v = api.GetVertexArray(g.GetVertexData(), g.GetVertexDataSize());
        const index = api.GetIndexArray(g.GetIndexData(), g.GetIndexDataSize());
        g.delete();
        const pos = new Float32Array(v.length / 2), nor = new Float32Array(v.length / 2);
        for (let k = 0, j = 0; k < v.length; k += 6, j += 3) { pos[j] = v[k]; pos[j + 1] = v[k + 1]; pos[j + 2] = v[k + 2]; nor[j] = v[k + 3]; nor[j + 1] = v[k + 4]; nor[j + 2] = v[k + 5]; }
        const geo = new THREE.BufferGeometry();
        geo.setAttribute("position", new THREE.BufferAttribute(pos, 3)); geo.setAttribute("normal", new THREE.BufferAttribute(nor, 3));
        geo.setIndex(new THREE.BufferAttribute(index, 1));
        geo.applyMatrix4(toNetwork.clone().multiply(new THREE.Matrix4().fromArray(placed.flatTransformation)));
        pieces.push(geo);
      }
      if (!pieces.length) return;
      const id = api.GetLine(model, flat.expressID)?.GlobalId?.value;
      if (id) parts.set(id, pieces.length === 1 ? pieces[0] : mergeGeometries(pieces, false));
    });
    api.CloseModel(model);
    return parts;
  } catch (error) { console.warn("Geometria do IFC indisponível:", error); return null; }
  finally { busy.remove(); }
}

// Como no Visor IFC: a rede inteira vai numa só malha em lote (BatchedMesh), desenhada em uma chamada pela placa de vídeo.
// Cor, ocultar e isolar só mudam o estado de cada item; nada é recriado.
let batch = null;
const plain = (g) => { const c = g.clone(); c.deleteAttribute("uv"); return c; };
const unitCylinderB = plain(unitCylinder), unitSphereB = plain(unitSphere), unitBoxB = plain(unitBox);
function setVisible(o, v) { o.visible = v; batch?.setVisibleAt(o.iid, v); }
function setColor(o, c) { batch?.setColorAt(o.iid, c); }
function boxOf(o) { return o.box.clone().applyMatrix4(world.matrixWorld); }
function build(network) {
  world.clear(); objects = []; selected = null; batch = null;
  const thick = $("vReal").checked ? 1 : 2.5;
  const items = [];
  const add = (geo, matrix, kind, data) => items.push({ geo, matrix, kind, data });
  const at = (p, s) => new THREE.Matrix4().compose(new THREE.Vector3(...p), new THREE.Quaternion(), new THREE.Vector3(s, s, s));
  const identity = new THREE.Matrix4();
  for (const p of network.Pipes) {
    const geo = ifcGeometry?.get(p.Id);
    if (geo) { add(geo, identity, "pipe", p); continue; }
    const a = new THREE.Vector3(...p.A), b = new THREE.Vector3(...p.B);
    const dir = b.clone().sub(a); const len = dir.length(); if (len < 1e-4) continue;
    const r = Math.max(p.D_mm / 2000 * thick, 0.008);
    add(unitCylinderB, new THREE.Matrix4().compose(a.clone().add(b).multiplyScalar(0.5), new THREE.Quaternion().setFromUnitVectors(up, dir.normalize()), new THREE.Vector3(r, len, r)), "pipe", p);
  }
  for (const s of network.Sprinklers) add(ifcGeometry?.get(s.Id) ?? unitSphereB, ifcGeometry?.has(s.Id) ? identity : at(s.P, s.Number != null ? 0.14 : 0.05), "sprinkler", s);
  for (const d of network.Devices) {
    const geo = ifcGeometry?.get(d.Id);
    add(geo ?? (d.Kind === "Fitting" ? unitSphereB : unitBoxB), geo ? identity : at(d.P, d.Kind === "Pump" ? 0.45 : d.Kind === "Valve" ? 0.16 : 0.035), d.Kind.toLowerCase(), d);
  }
  const unique = [...new Set(items.map((i) => i.geo))];
  const vertices = unique.reduce((n, g) => n + g.attributes.position.count, 0), indices = unique.reduce((n, g) => n + (g.index?.count ?? 0), 0);
  batch = new THREE.BatchedMesh(items.length, vertices, indices, new THREE.MeshLambertMaterial({ side: THREE.DoubleSide }));
  batch.sortObjects = false;
  const ids = new Map();
  for (const it of unique) { if (!it.boundingBox) it.computeBoundingBox(); ids.set(it, batch.addGeometry(it)); }
  for (const it of items) {
    const iid = batch.addInstance(ids.get(it.geo)); batch.setMatrixAt(iid, it.matrix);
    objects[iid] = { iid, kind: it.kind, data: it.data, visible: true, box: it.geo.boundingBox.clone().applyMatrix4(it.matrix) };
  }
  world.add(batch);
  // Marcadores de seleção da área (anel em volta do bico na planta).
  for (const o of objects) if (o.kind === "sprinkler") {
    const ring = new THREE.Mesh(new THREE.RingGeometry(0.22, 0.32, 24), new THREE.MeshBasicMaterial({ color: 0xd63384, side: THREE.DoubleSide }));
    ring.position.set(...o.data.P); ring.visible = false; world.add(ring); o.ring = ring;
  }
  $("vReal").parentElement.hidden = !!ifcGeometry;
  // Modelo reto: gira a rede para que os ramais do pavimento com mais bicos fiquem na horizontal.
  const main = [...(network.Layouts ?? [])].sort((a, b) => objects.filter((o) => o.data.Storey === b.Storey).length - objects.filter((o) => o.data.Storey === a.Storey).length)[0];
  world.rotation.set(0, 0, main ? -main.BranchAngle_rad : 0); world.updateMatrixWorld(true);
  floors(); recolor(); applyVisibility(); fit(visibleBox()); buildLabels();
  $("vazio3d").hidden = true;
}
function visibleBox() { const b = new THREE.Box3(); for (const o of objects) if (o.visible) b.union(boxOf(o)); return b; }

function recolor() {
  const mode = $("colorir").value;
  const pipes = objects.filter((o) => o.kind === "pipe").map((o) => o.data);
  const max = { vazao: Math.max(...pipes.map((p) => p.Q_l_s ?? 0), 1e-6), velocidade: Math.max(...pipes.map((p) => p.V_m_s ?? 0), 1e-6) };
  const diameters = [...new Set(pipes.map((p) => p.D_mm))].sort((a, b) => a - b);
  const heads = objects.filter((o) => o.kind === "sprinkler" && o.data.Number != null).map((o) => o.data.Pressure_mca);
  const pMin = Math.min(...heads), pMax = Math.max(...heads);
  for (const o of objects) {
    let color;
    if (o.kind === "pipe") {
      const q = o.data.Q_l_s;
      if (mode === "diametro") color = ramp(diameters.indexOf(o.data.D_mm) / Math.max(diameters.length - 1, 1));
      else if (q == null || q < 1e-4) color = new THREE.Color(0xb8bec8);
      else if (mode === "velocidade") color = ramp((o.data.V_m_s ?? 0) / max.velocidade);
      else if (mode === "vazao") color = ramp(q / max.vazao);
      else color = new THREE.Color(0xe8711f);
    } else if (o.kind === "sprinkler") {
      color = o.data.Number != null ? ramp((o.data.Pressure_mca - pMin) / Math.max(pMax - pMin, 1e-6)) : new THREE.Color(0x9aa3ad);
    } else if (o.kind === "pump") color = new THREE.Color(o.data.Active ? 0xe8711f : 0x7a8594);
    else if (o.kind === "valve") color = new THREE.Color(0x2f6fde);
    else color = new THREE.Color(0x8b939e);
    setColor(o, color);
    o.baseColor = color.clone();
  }
  if (selected) highlight(selected, true);
  legend(mode, { max, diameters, pMin, pMax });
}

function legend(mode, info) {
  const el = $("legenda"); el.hidden = !state.network;
  const bar = `linear-gradient(90deg, ${RAMP.map(([t, c]) => `#${c.toString(16).padStart(6, "0")} ${t * 100}%`).join(",")})`;
  const scale = (title, a, b) => `<b>${title}</b><div class="barra" style="background:${bar}"></div><div class="esc"><span>${a}</span><span>${b}</span></div>`;
  const item = (c, t) => `<div class="item"><span class="cor" style="background:${c}"></span>${t}</div>`;
  const pressure = Number.isFinite(info.pMin) ? `<div style="margin-top:6px">Pressão nos bicos da área (m.c.a.)</div><div class="barra" style="background:${bar}"></div><div class="esc"><span>${fmt(info.pMin)}</span><span>${fmt(info.pMax)}</span></div>` : "";
  if (mode === "vazao") el.innerHTML = scale("Vazão nos tubos (L/s)", "0", fmt(info.max.vazao)) + item("#b8bec8", "Sem vazão");
  else if (mode === "velocidade") el.innerHTML = scale("Velocidade (m/s)", "0", fmt(info.max.velocidade)) + item("#b8bec8", "Sem vazão");
  else if (mode === "diametro") el.innerHTML = scale("Diâmetro interno (mm)", fmt(info.diameters[0], 1), fmt(info.diameters.at(-1), 1));
  else el.innerHTML = `<b>Situação do cálculo</b>` + item("#e8711f", "Tubo com vazão") + item("#b8bec8", "Tubo sem vazão") + item("#2f6fde", "Válvula") + item("#e8711f", "Bomba em operação") + pressure;
  if (state.mode === "2d") el.innerHTML += item("#d63384", `Área escolhida: ${state.area.size} bico(s)`);
}

// ---------- Visibilidade, pavimentos e câmera ----------
function currentLayout() { return state.network?.Layouts?.find((l) => l.Storey === $("pavimento").value) ?? null; }
function floorRange() {
  if (state.mode !== "2d") return null;
  const layout = currentLayout(); return layout ? { storey: layout.Storey, z: [layout.Elevation_m - 2.0, layout.Elevation_m + 1.0] } : null;
}
function inFloor(o, range) {
  if (!range) return true;
  if (o.kind === "sprinkler" && o.data.Storey) return o.data.Storey === range.storey;
  const zs = o.kind === "pipe" ? [o.data.A[2], o.data.B[2]] : [o.data.P[2]];
  return zs.some((z) => z >= range.z[0] && z <= range.z[1]);
}
function applyVisibility() {
  const show = { fitting: $("vConexoes").checked, valve: $("vValvulas").checked, pump: $("vValvulas").checked };
  const range = floorRange();
  for (const o of objects) {
    let visible;
    if (o.kind === "pipe") visible = $("vSemVazao").checked || (o.data.Q_l_s ?? 0) > 1e-4;
    else if (o.kind === "sprinkler") visible = o.data.Number != null || $("vBicos").checked || state.mode === "2d";
    else visible = show[o.kind] ?? true;
    setVisible(o, visible && inFloor(o, range) && !state.hidden.has(o.data.Id) && (!state.isolated || state.isolated.has(o.data.Id)));
    if (o.ring) o.ring.visible = state.mode === "2d" && state.area.has(o.data.Id) && inFloor(o, range);
  }
}
function floors() {
  const sel = $("pavimento"); const previous = sel.value;
  const layouts = [...(state.network?.Layouts ?? [])].sort((a, b) => a.Elevation_m - b.Elevation_m);
  const count = (storey) => objects.filter((o) => o.kind === "sprinkler" && o.data.Storey === storey).length;
  sel.innerHTML = layouts.map((l) => `<option value="${esc(l.Storey)}">${esc(l.Storey)} · ${count(l.Storey)} bicos</option>`).join("");
  const area = objects.find((o) => o.kind === "sprinkler" && o.data.Number != null);
  if (previous && layouts.some((l) => l.Storey === previous)) sel.value = previous;
  else if (area?.data.Storey) sel.value = area.data.Storey;
}

function fit(target, padding = 1.25) {
  const box = new THREE.Box3();
  box.copy(target.isObject3D ? visibleBox() : target);
  if (box.isEmpty()) return;
  const center = box.getCenter(new THREE.Vector3()); const size = box.getSize(new THREE.Vector3());
  if (camera.isOrthographicCamera) {
    const w = viewport.clientWidth, h = Math.max(viewport.clientHeight, 1);
    const half = Math.max(size.y / 2, size.x / 2 * h / w) * padding;
    ortho.top = half; ortho.bottom = -half; ortho.left = -half * w / h; ortho.right = half * w / h; ortho.updateProjectionMatrix();
    controls2d.zoomTo(1, false); controls2d.setLookAt(center.x, center.y, center.z + 200, center.x, center.y, center.z, false); return;
  }
  const position = controls3d.getPosition(new THREE.Vector3()), orbitTarget = controls3d.getTarget(new THREE.Vector3());
  const dir = position.sub(orbitTarget).normalize(); if (dir.lengthSq() === 0) dir.set(1, -1, 0.8).normalize();
  const dist = size.length() / 2 / Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * padding || 10;
  camera.near = Math.max(dist / 2000, 0.01); camera.far = dist * 50; camera.updateProjectionMatrix();
  const eye = center.clone().addScaledVector(dir, dist);
  controls3d.setLookAt(eye.x, eye.y, eye.z, center.x, center.y, center.z, true);
}

function setMode(mode) {
  state.mode = mode;
  document.querySelectorAll("[data-vista]").forEach((b) => b.classList.toggle("ativo", b.dataset.vista === mode));
  $("grupo2d").hidden = mode !== "2d";
  if (mode !== "2d") setSelecting(false);
  camera = mode === "2d" ? ortho : perspective;
  controls.enabled = false; controls = mode === "2d" ? controls2d : controls3d; controls.enabled = true;
  applyVisibility(); legend($("colorir").value, legendInfo());
  if (mode === "2d") { const b = floorBox(); fit(b.isEmpty() ? world : b, 1.1); }
  else {
    const box = visibleBox(); if (box.isEmpty()) return;
    const center = box.getCenter(new THREE.Vector3());
    const dir = mode === "topo" ? new THREE.Vector3(0, -0.001, 1) : new THREE.Vector3(1, -1, 0.8);
    controls3d.setLookAt(center.x + dir.x, center.y + dir.y, center.z + dir.z, center.x, center.y, center.z, false); fit(world, 1.1);
  }
}
function legendInfo() {
  const pipes = objects.filter((o) => o.kind === "pipe").map((o) => o.data);
  const heads = objects.filter((o) => o.kind === "sprinkler" && o.data.Number != null).map((o) => o.data.Pressure_mca);
  return { max: { vazao: Math.max(...pipes.map((p) => p.Q_l_s ?? 0), 1e-6), velocidade: Math.max(...pipes.map((p) => p.V_m_s ?? 0), 1e-6) },
    diameters: [...new Set(pipes.map((p) => p.D_mm))].sort((a, b) => a - b), pMin: Math.min(...heads), pMax: Math.max(...heads) };
}
function floorBox() { return visibleBox(); }
function areaBox() {
  const box = new THREE.Box3();
  for (const o of objects) if (o.kind === "sprinkler" && o.data.Number != null) box.expandByPoint(toScene(o.data.P));
  return box.expandByScalar(2);
}

// ---------- Rótulos ----------
let labels = [];
function buildLabels() {
  const layer = $("rotulos"); layer.innerHTML = ""; labels = [];
  for (const o of objects) if (o.kind === "sprinkler" && o.data.Number != null) {
    const el = document.createElement("div"); el.className = "rot"; el.textContent = `B${o.data.Number} · ${fmt(o.data.Pressure_mca)}`;
    layer.appendChild(el); labels.push({ el, o });
  }
}
function updateLabels() {
  const on = $("vRotulos").checked; const w = viewport.clientWidth, h = viewport.clientHeight;
  for (const { el, o } of labels) {
    const p = toScene(o.data.P).project(camera);
    const visible = on && o.visible && p.z < 1 && Math.abs(p.x) < 1.1 && Math.abs(p.y) < 1.1;
    el.style.display = visible ? "" : "none";
    if (visible) { el.style.left = `${(p.x + 1) / 2 * w}px`; el.style.top = `${(1 - p.y) / 2 * h}px`; }
  }
}
(function loop() { requestAnimationFrame(loop); controls.update(clock.getDelta()); renderer.render(scene, camera); updateLabels(); })();

// ---------- Seleção de elementos e da área (2D) ----------
const raycaster = new THREE.Raycaster(); const pointer = new THREE.Vector2();
let down = null, rect = null;
// Na seleção da área o clique é tratado antes dos controles de órbita (fase de captura), que não chegam a mover a vista.
renderer.domElement.addEventListener("pointerdown", (e) => {
  down = [e.clientX, e.clientY, e.shiftKey, e.ctrlKey];
  if (state.selecting && e.button === 0) {
    e.stopImmediatePropagation();
    rect = document.createElement("div"); rect.className = "retangulo"; viewport.parentElement.appendChild(rect);
  }
}, { capture: true });
window.addEventListener("pointermove", (e) => {
  if (!rect || !down) return;
  const r = viewport.getBoundingClientRect();
  const x0 = Math.min(down[0], e.clientX) - r.left, y0 = Math.min(down[1], e.clientY) - r.top;
  Object.assign(rect.style, { left: `${x0 + viewport.offsetLeft}px`, top: `${y0 + viewport.offsetTop}px`, width: `${Math.abs(e.clientX - down[0])}px`, height: `${Math.abs(e.clientY - down[1])}px` });
});
window.addEventListener("pointerup", (e) => {
  if (!down) return;
  if (!rect && e.target !== renderer.domElement) { down = null; return; }
  const moved = Math.hypot(e.clientX - down[0], e.clientY - down[1]) > 4;
  if (rect) {
    rect.remove(); rect = null;
    if (moved) selectRect(down, [e.clientX, e.clientY], down[2], down[3]);
    else { const hit = pick(e); if (hit?.kind === "sprinkler") toggleArea(hit.data.Id); }
  } else if (!moved && e.button === 0) { const hit = pickHit(e); select(hit?.o ?? null); if (hit) orbitAt(hit.point); }
  down = null;
});
renderer.domElement.addEventListener("dblclick", (e) => { if (state.selecting) return; const hit = pick(e); if (hit) { select(hit); fit(boxOf(hit).expandByScalar(1.5)); } });

function pickHit(e) {
  const r = renderer.domElement.getBoundingClientRect();
  pointer.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
  raycaster.setFromCamera(pointer, camera);
  const hits = batch ? raycaster.intersectObject(batch, false) : [];
  return hits.length ? { o: objects[hits[0].batchId], point: hits[0].point } : null;
}
function pick(e) {
  const r = renderer.domElement.getBoundingClientRect();
  pointer.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
  raycaster.setFromCamera(pointer, camera);
  return pickHit(e)?.o ?? null;
}
// Menu do botão direito (como no Visor IFC): aproximar, isolar, ocultar e mostrar todos.
const menu = document.createElement("div"); menu.className = "pop menu-ctx"; menu.hidden = true; document.body.appendChild(menu);
renderer.domElement.addEventListener("contextmenu", (e) => {
  e.preventDefault(); if (state.selecting) return;
  const hit = pickHit(e); if (hit) { select(hit.o); orbitAt(hit.point); }
  const items = [
    hit && ["Aproximar", () => fit(boxOf(hit.o).expandByScalar(1.5))],
    hit && ["Isolar", () => { state.isolated = new Set([hit.o.data.Id]); applyVisibility(); }],
    hit && hit.o.kind !== "pipe" && ["Isolar o ramal (tubos ligados)", () => { state.isolated = connected(hit.o); applyVisibility(); }],
    hit && ["Ocultar", () => { state.hidden.add(hit.o.data.Id); if (selected === hit.o) select(null); applyVisibility(); }],
    ["Mostrar todos", () => { state.hidden.clear(); state.isolated = null; applyVisibility(); }],
    ["Enquadrar", () => fit(state.mode === "2d" ? floorBox() : world)],
  ].filter(Boolean);
  menu.innerHTML = ""; for (const [text, action] of items) { const b = document.createElement("button"); b.textContent = text; b.onclick = () => { menu.hidden = true; action(); }; menu.appendChild(b); }
  Object.assign(menu.style, { left: `${e.clientX}px`, top: `${e.clientY}px`, right: "auto" }); menu.hidden = false;
});
document.addEventListener("pointerdown", (e) => { if (!menu.contains(e.target)) menu.hidden = true; });
// Teclas do Visor IFC: H oculta a seleção · I isola · M mostra tudo · F enquadra · Esc sai da ferramenta e limpa a seleção.
document.addEventListener("keydown", (e) => {
  if (e.target.closest?.("input, select, textarea") || e.ctrlKey || e.altKey) return;
  const k = e.key.toLowerCase();
  if (k === "escape") { menu.hidden = true; if (state.selecting) setSelecting(false); else select(null); }
  else if (k === "h" && selected) { state.hidden.add(selected.data.Id); select(null); applyVisibility(); }
  else if (k === "i" && selected) { state.isolated = new Set([selected.data.Id]); applyVisibility(); }
  else if (k === "m") { state.hidden.clear(); state.isolated = null; applyVisibility(); }
  else if (k === "f") fit(selected ? boxOf(selected).expandByScalar(2) : state.mode === "2d" ? floorBox() : world);
});
// Duplo clique na roda: enquadra tudo.
let lastMiddle = 0;
renderer.domElement.addEventListener("pointerdown", (e) => {
  if (e.button !== 1) return; const t = performance.now();
  if (t - lastMiddle < 350) { lastMiddle = 0; e.preventDefault(); fit(state.mode === "2d" ? floorBox() : world); } else lastMiddle = t;
});
// Elementos próximos ao selecionado (até 1 m), para isolar um trecho.
function connected(o) {
  const c = boxOf(o).expandByScalar(1.0); const ids = new Set();
  for (const x of objects) if (x.visible && c.intersectsBox(boxOf(x))) ids.add(x.data.Id);
  return ids;
}
function selectRect(a, b, add, remove) {
  const r = renderer.domElement.getBoundingClientRect();
  const [x0, x1] = [Math.min(a[0], b[0]) - r.left, Math.max(a[0], b[0]) - r.left], [y0, y1] = [Math.min(a[1], b[1]) - r.top, Math.max(a[1], b[1]) - r.top];
  if (!add && !remove) state.area.clear();
  for (const o of objects) if (o.kind === "sprinkler" && o.visible) {
    const p = toScene(o.data.P).project(camera);
    const sx = (p.x + 1) / 2 * r.width, sy = (1 - p.y) / 2 * r.height;
    if (sx >= x0 && sx <= x1 && sy >= y0 && sy <= y1) { if (remove) state.area.delete(o.data.Id); else state.area.add(o.data.Id); }
  }
  areaChanged();
}
function toggleArea(id) { if (state.area.has(id)) state.area.delete(id); else state.area.add(id); areaChanged(); }
function areaChanged() {
  applyVisibility(); legend($("colorir").value, legendInfo());
  const n = state.area.size;
  const pts = objects.filter((o) => o.kind === "sprinkler" && state.area.has(o.data.Id)).map((o) => o.data.P);
  let info = `${n} bico(s) selecionado(s).`;
  if (n >= 2) {
    const xs = pts.map((p) => p[0]), ys = pts.map((p) => p[1]);
    info += ` Retângulo envolvente dos bicos: ${fmt(Math.max(...xs) - Math.min(...xs), 1)} × ${fmt(Math.max(...ys) - Math.min(...ys), 1)} m.`;
    const area = num($("aArea").value); if (area) info += ` Área por bico: ${fmt(area / n)} m².`;
  }
  $("areaInfo").textContent = n ? info : "Na vista 2D, use \"Selecionar área\" e arraste sobre os bicos.";
  $("btnCalcArea").disabled = n === 0;
  shapeCheck();
}
// NBR 10897 (cálculo hidráulico): área retangular com lado paralelo aos ramais ≥ 1,2 √A, bicos = A ÷ área por bico, área contígua.
function shapeCheck() {
  const el = $("areaForma"); const selectedHeads = objects.filter((o) => o.kind === "sprinkler" && state.area.has(o.data.Id));
  if (!selectedHeads.length || !state.network?.Layouts) { el.innerHTML = ""; return; }
  const storeys = [...new Set(selectedHeads.map((o) => o.data.Storey))];
  const layout = state.network.Layouts.find((l) => l.Storey === storeys[0]);
  if (!layout) { el.innerHTML = ""; return; }
  const A = num($("aArea").value) ?? 0; const perHead = num($("aPorBico").value) ?? layout.SpacingAlong_m * layout.SpacingAcross_m;
  const required = Math.ceil(A / perHead - 1e-9), length = 1.2 * Math.sqrt(A), perLine = Math.min(required, Math.ceil(length / layout.SpacingAlong_m - 1e-9));
  const a = layout.BranchAngle_rad; const uv = selectedHeads.map((o) => [o.data.P[0] * Math.cos(a) + o.data.P[1] * Math.sin(a), -o.data.P[0] * Math.sin(a) + o.data.P[1] * Math.cos(a)]);
  const along = Math.max(...uv.map((p) => p[0])) - Math.min(...uv.map((p) => p[0])) + layout.SpacingAlong_m;
  const across = Math.max(...uv.map((p) => p[1])) - Math.min(...uv.map((p) => p[1])) + layout.SpacingAcross_m;
  const rows = [
    [storeys.length === 1, `Um só pavimento (${esc(storeys.join(", "))})`],
    [selectedHeads.length >= required, `Bicos: ${selectedHeads.length} de ${required} exigidos (${fmt(A, 0)} m² ÷ ${fmt(perHead)} m² por bico)`],
    [along >= length - 0.05 * layout.SpacingAlong_m, `Lado paralelo aos ramais: ${fmt(along, 1)} m (mínimo 1,2√A = ${fmt(length, 1)} m; ${perLine} bicos por ramal)`],
    [along * across <= 1.3 * Math.max(A, required * perHead), `Área contígua: retângulo de ${fmt(along * across, 0)} m² (até ${fmt(1.3 * Math.max(A, required * perHead), 0)} m²)`],
  ];
  el.innerHTML = `<table class="kv"><tr><th colspan="2">Forma da área – NBR 10897</th></tr>${rows.map(([ok, t]) => `<tr><td>${t}</td><td style="color:var(${ok ? "--ok" : "--erro"})">${ok ? "✔" : "✖"}</td></tr>`).join("")}</table>`;
}
function setSelecting(on) {
  state.selecting = on; $("btnSelArea").classList.toggle("ativo", on);
  renderer.domElement.style.cursor = on ? "crosshair" : "";
  if (on) { document.querySelector('input[name=origemArea][value="2d"]').checked = true; areaSource(); }
}

function highlight(o, on) { setColor(o, on ? new THREE.Color(0x00c2ff) : o.baseColor); }
function select(o) {
  if (selected) highlight(selected, false);
  selected = o; if (o) highlight(o, true);
  showProperties(o);
  document.querySelectorAll("#listaBicos tr").forEach((tr) => tr.classList.toggle("sel", !!o && tr.dataset.id === o.data.Id));
}
function kv(title, rows) {
  return `<table class="kv"><tr><th colspan="2">${esc(title)}</th></tr>${rows.filter(Boolean).map(([k, v]) => `<tr><td>${esc(k)}</td><td>${esc(v)}</td></tr>`).join("")}</table>`;
}
function showProperties(o) {
  const el = $("propriedades");
  if (!o) { el.innerHTML = `<p class="vazio">Clique num elemento da rede.</p>`; return; }
  const d = o.data; const pos = (p) => `${fmt(p[0])}; ${fmt(p[1])}; ${fmt(p[2])}`;
  const origin = state.network.Origin; const z = (p) => fmt(p[2] + origin.Z);
  if (o.kind === "pipe") {
    el.innerHTML = `<div class="nome">${esc(d.Type)} Ø ${fmt(d.D_mm, 1)} mm</div><div class="classe">Tubo (IfcPipeSegment)</div>`
      + kv("Identificação", [["GlobalId", d.Id], ["Diâmetro interno", `${fmt(d.D_mm, 1)} mm`], ["Comprimento", `${fmt(d.L_m, 3)} m`]])
      + kv("Cálculo hidráulico", d.Q_l_s == null ? [["Situação", "fora do trecho calculado"]] : [["Vazão", `${fmt(d.Q_l_s, 3)} L/s (${fmt(d.Q_l_s * 3.6)} m³/h)`],
        ["Velocidade", `${fmt(d.V_m_s)} m/s`], ["Comprimento equivalente", `${fmt(d.Leq_m)} m`], ["Perda de carga", `${fmt(d.Loss_m, 3)} m`]])
      + kv("Geometria (m)", [["Início X; Y; Z", pos(d.A)], ["Fim X; Y; Z", pos(d.B)], ["Cota do início", `${z(d.A)} m`]]);
  } else if (o.kind === "sprinkler") {
    el.innerHTML = `<div class="nome">${d.Number != null ? `Bico ${d.Number} da área de operação` : "Bico fora da área de operação"}</div><div class="classe">Sprinkler (IfcFireSuppressionTerminal)</div>`
      + kv("Identificação", [["GlobalId", d.Id], ["Tipo", d.Type ?? "—"], ["Na área escolhida (2D)", state.area.has(d.Id) ? "sim" : "não"]])
      + (d.Number != null ? kv("Cálculo hidráulico", [["Pressão", `${fmt(d.Pressure_mca)} m.c.a.`], ["Vazão", `${fmt(d.Q_l_s, 3)} L/s (${fmt(d.Q_l_s * 60, 1)} L/min)`]]) : "")
      + kv("Posição (m)", [["X; Y; Z", pos(d.P)], ["Cota", `${z(d.P)} m`]]);
  } else {
    const names = { pump: "Bomba (IfcPump)", valve: "Válvula (IfcValve)", fitting: "Conexão (IfcPipeFitting)" };
    el.innerHTML = `<div class="nome">${esc(d.Type)}</div><div class="classe">${names[o.kind]}</div>`
      + kv("Identificação", [["GlobalId", d.Id], ["Indicação", d.Indication || "—"], o.kind === "pump" && ["Situação", d.Active ? "bomba usada no cálculo" : "desligada no cálculo (reserva/jóquei)"]])
      + kv("Posição (m)", [["X; Y; Z", pos(d.P)], ["Cota", `${z(d.P)} m`]]);
  }
}

// ---------- Painel lateral ----------
function summary() {
  const n = state.network; const el = $("resumo");
  if (!n) { el.innerHTML = `<p class="vazio">Abra um IFC e clique em Analisar.</p>`; $("listaBicos").innerHTML = ""; return; }
  const s = n.Summary;
  el.innerHTML = kv("Área de operação calculada", [["Área", s.OperationArea], ["Densidade", `${fmt(s.Density_mm_min, 1)} mm/min`], ["Área de aplicação", `${fmt(s.DesignArea_m2)} m²`], ["Bicos", s.SprinklerCount]])
    + kv("Resultado", [["Vazão total", `${fmt(s.TotalFlow_l_s)} L/s (${fmt(s.TotalFlow_l_s * 3.6)} m³/h)`], ["Pressão mínima nos bicos", `${fmt(s.MinimumSprinklerPressure_mca)} m.c.a.`],
      ["Altura manométrica necessária", s.RequiredHead_m != null ? `${fmt(s.RequiredHead_m)} m` : "—"], ["Velocidade máxima", `${fmt(s.MaxVelocity_m_s)} m/s`]])
    + kv("Bombas do catálogo", [["Avaliadas", s.PumpsEvaluated], ["Atendem", s.PumpsMeeting], ["Primeira da lista", s.BestPump ?? "—"]])
    + (s.Warnings?.length ? `<div>${s.Warnings.map((w) => `<span class="chip alerta">${esc(w)}</span>`).join(" ")}</div>` : "");
  const heads = n.Sprinklers.filter((x) => x.Number != null).sort((a, b) => a.Number - b.Number);
  $("listaBicos").innerHTML = `<table class="lista"><tr><th>Bico</th><th>Pressão (m.c.a.)</th><th>Vazão (L/min)</th></tr>${heads.map((h) =>
    `<tr class="clicavel" data-id="${esc(h.Id)}"><td>B${h.Number}</td><td>${fmt(h.Pressure_mca)}</td><td>${fmt(h.Q_l_s * 60, 1)}</td></tr>`).join("")}</table>`;
  document.querySelectorAll("#listaBicos tr.clicavel").forEach((tr) => tr.onclick = () => {
    const o = objects.find((x) => x.data.Id === tr.dataset.id); if (o) { select(o); fit(boxOf(o).expandByScalar(3)); }
  });
  if (s.Density_mm_min) { $("aDensidade").value = fmt(s.Density_mm_min, 1); $("aArea").value = fmt(s.DesignArea_m2, 0); }
}

function parseCsv(text) {
  const rows = text.replace(/^﻿/, "").split(/\r?\n/).filter(Boolean).map((line) => {
    const out = []; let cur = "", q = false;
    for (let i = 0; i < line.length; i++) { const c = line[i];
      if (q && c === '"' && line[i + 1] === '"') { cur += '"'; i++; } else if (c === '"') q = !q; else if (c === ";" && !q) { out.push(cur); cur = ""; } else cur += c; }
    out.push(cur); return out; });
  const head = rows.shift() ?? []; return rows.map((r) => Object.fromEntries(head.map((h, i) => [h, r[i]])));
}
function pumpsTable() {
  const list = state.pumps.filter((p) => p.Atende === "Sim");
  $("listaBombas").innerHTML = !state.pumps.length ? `<p class="vazio">Sem seleção de bombas. Analise o IFC.</p>`
    : `<p class="vazio">${list.length} de ${state.pumps.length} bombas atendem. Clique numa bomba para ver a curva e os dados.</p>`
      + `<table class="lista"><tr><th>Bomba</th><th>cv</th><th>Q (m³/h)</th><th>H (m)</th></tr>${list.map((p) =>
        `<tr class="clicavel${p.Id === state.selectedPumpId ? " sel" : ""}" data-id="${esc(p.Id)}" title="${esc(p.Observacoes)}"><td>${esc(p.Modelo)}${p.Rotor_mm ? ` · ${esc(p.Rotor_mm)} mm` : ""}</td><td>${esc(p.Potencia_cv)}</td><td>${esc(p.Vazao_operacao_m3h)}</td><td>${esc(p.Altura_operacao_m)}</td></tr>`).join("")}</table>`;
  document.querySelectorAll("#listaBombas tr.clicavel").forEach((tr) => tr.onclick = () => {
    state.selectedPumpId = tr.dataset.id; pumpsTable(); updateButtons(); send({ cmd: "selectPump", id: tr.dataset.id });
  });
}

async function loadResults(base) {
  const sameModel = state.dataBase === base && ifcGeometry;
  state.dataBase = base;
  try {
    const r = await fetch(`${base}.network-3d.json?t=${Date.now()}`); if (!r.ok) throw new Error("sem rede 3D");
    state.network = await r.json();
    if (!sameModel) { build(state.network); ifcGeometry = await loadIfcGeometry(base, state.network.Origin); }
    build(state.network);
  } catch { state.network = null; }
  try { const r = await fetch(`${base}.pump-selection.csv?t=${Date.now()}`); state.pumps = r.ok ? parseCsv(await r.text()) : []; } catch { state.pumps = []; }
  state.selectedPumpId = state.pumps.find((p) => p.Atende === "Sim")?.Id ?? null;
  try {
    const r = await fetch(`${base}.operation-area.json?t=${Date.now()}`);
    const fromArea = /^Área /.test(state.network?.Summary?.OperationArea ?? "");
    if (r.ok && (fromArea || document.querySelector('input[name=origemArea][value="2d"]').checked)) {
      const a = await r.json(); state.area = new Set(a.SprinklerIds ?? []);
      if (fromArea) { document.querySelector('input[name=origemArea][value="2d"]').checked = true; $("areaManual").hidden = false; send({ cmd: "areaSource", manual: true }); }
    }
  } catch {}
  try { const r = await fetch(`${base}.area-candidates.json?t=${Date.now()}`); state.candidates = r.ok ? await r.json() : null; } catch { state.candidates = null; }
  const criteria = state.candidates?.Criteria;
  if (criteria?.AreaPerHead_m2 && !$("aPorBico").value) $("aPorBico").value = fmt(criteria.AreaPerHead_m2, 1);
  summary(); pumpsTable(); updateButtons(); areaChanged(); candidatesList();
}
function updateButtons() {
  $("btnBomba").disabled = !state.pumps.length;
  $("btnMemorial").disabled = !state.selectedPumpId;
  $("btnMemorial").title = state.selectedPumpId ? `Memorial com a bomba ${state.selectedPumpId}` : "Escolha uma bomba na aba Bombas";
}
function candidatesList() {
  const el = $("candidatas"); const c = state.candidates?.Candidates?.filter((x) => x.Error == null) ?? [];
  if (!c.length) { el.innerHTML = ""; return; }
  const sorted = [...c].sort((a, b) => b.RequiredHead_m - a.RequiredHead_m).slice(0, 8);
  el.innerHTML = `<p class="vazio" style="margin-top:8px">${c.length} áreas calculadas; as mais exigentes (clique para ver):</p><table class="lista"><tr><th>Pavimento</th><th>Bicos</th><th>Altura (m)</th></tr>${sorted.map((x, i) =>
    `<tr class="clicavel${x.Worst ? " sel" : ""}" data-i="${i}"><td>${esc(x.Storey)}${x.Worst ? " · usada" : ""}</td><td>${x.HeadIds.length}</td><td>${fmt(x.RequiredHead_m)}</td></tr>`).join("")}</table>`;
  el.querySelectorAll("tr.clicavel").forEach((tr) => tr.onclick = () => {
    const x = sorted[Number(tr.dataset.i)]; state.area = new Set(x.HeadIds);
    const storey = x.Storey.replace(" (memorial)", ""); if ([...$("pavimento").options].some((o) => o.value === storey)) $("pavimento").value = storey;
    if (state.mode !== "2d") setMode("2d"); else applyVisibility();
    areaChanged(); const b = new THREE.Box3(); for (const o of objects) if (state.area.has(o.data.Id)) b.expandByPoint(toScene(o.data.P)); if (!b.isEmpty()) fit(b.expandByScalar(4), 1.1);
  });
}
function areaSource() {
  const manual = document.querySelector("input[name=origemArea]:checked").value === "2d";
  $("areaManual").hidden = !manual;
  send({ cmd: "areaSource", manual });
}
function areaPayload() {
  return { Name: `Área escolhida no 2D (${state.area.size} bicos)`, SprinklerIds: [...state.area], Density_mm_min: num($("aDensidade").value), DesignArea_m2: num($("aArea").value),
    K: num($("aK").value), MinimumPressure_mca: num($("aPressao").value) ?? 0, AreaPerHead_m2: num($("aPorBico").value), RiskClass: $("aRisco").value };
}

// ---------- Eventos ----------
document.querySelectorAll(".abas button").forEach((b) => b.onclick = () => {
  document.querySelectorAll(".abas button").forEach((x) => x.classList.toggle("ativa", x === b));
  document.querySelectorAll(".aba").forEach((a) => a.hidden = a.id !== `aba-${b.dataset.aba}`);
});
const toggle = (btn, pop) => { $(btn).onclick = (e) => { e.stopPropagation(); $(pop).hidden = !$(pop).hidden; }; };
toggle("btnArquivos", "popArquivos"); toggle("btnMostrar", "popMostrar");
document.addEventListener("click", (e) => { for (const id of ["popArquivos", "popMostrar"]) if (!$(id).contains(e.target)) $(id).hidden = true; });
document.querySelectorAll("[data-abrir]").forEach((b) => b.onclick = () => { $("popArquivos").hidden = true; send({ cmd: "open", kind: b.dataset.abrir }); });
const runArgs = () => ({ source: $("alimentacao").value.trim(), tank: $("nivel").value.trim() });
$("btnAbrir").onclick = () => send({ cmd: "browse" });
$("btnAnalisar").onclick = () => send({ cmd: "analyze", ...runArgs() });
$("btnBomba").onclick = () => send({ cmd: "selectPump", id: state.selectedPumpId });
$("btnMemorial").onclick = () => state.selectedPumpId && send({ cmd: "memorial", id: state.selectedPumpId, ...runArgs() });
$("btnCurva").onclick = () => send({ cmd: "open", kind: "pumpcurve" });
$("btnEnquadrar").onclick = () => fit(state.mode === "2d" ? floorBox() : world);
$("btnAproximar").onclick = () => selected && fit(boxOf(selected).expandByScalar(2));
$("btnArea").onclick = () => { const b = areaBox(); if (!b.isEmpty()) fit(b, 1.1); };
document.querySelectorAll("[data-vista]").forEach((b) => b.onclick = () => setMode(b.dataset.vista));
$("pavimento").onchange = () => { applyVisibility(); fit(floorBox(), 1.1); };
$("btnSelArea").onclick = () => setSelecting(!state.selecting);
$("btnLimparArea").onclick = () => { state.area.clear(); areaChanged(); };
document.querySelectorAll("input[name=origemArea]").forEach((r) => r.onchange = areaSource);
$("aArea").oninput = areaChanged;
$("aPorBico").oninput = areaChanged;
$("btnAutoArea").onclick = () => {
  const { SprinklerIds, Name, ...criteria } = areaPayload();
  document.querySelector('input[name=origemArea][value="2d"]').checked = true; $("areaManual").hidden = false;
  send({ cmd: "analyzeAuto", area: criteria, ...runArgs() });
};
$("btnCalcArea").onclick = () => state.area.size && send({ cmd: "analyzeArea", area: areaPayload(), ...runArgs() });
$("colorir").onchange = recolor;
for (const id of ["vConexoes", "vValvulas", "vBicos", "vSemVazao"]) $(id).onchange = applyVisibility;
$("vReal").onchange = () => state.network && build(state.network);
$("btnLimpar").onclick = () => select(null);
$("btnSaida").onclick = () => { $("saida").hidden = !$("saida").hidden; $("btnSaida").textContent = $("saida").hidden ? "Saída ▴" : "Saída ▾"; };
$("btnTema").onclick = () => {
  const dark = document.documentElement.dataset.tema !== "escuro";
  document.documentElement.dataset.tema = dark ? "escuro" : ""; try { localStorage.setItem("grelha.tema", dark ? "escuro" : "claro"); } catch {}
  applyTheme();
};
try { if (localStorage.getItem("grelha.tema") === "escuro") { document.documentElement.dataset.tema = "escuro"; applyTheme(); } } catch {}

// ---------- Mensagens da janela ----------
function log(line) { const s = $("saida"); s.textContent += line + "\n"; s.scrollTop = s.scrollHeight; }
host?.addEventListener("message", (e) => {
  const m = e.data;
  if (m.type === "init") { $("versao").textContent = `versão ${m.version} · sprinklers · cálculo + bomba + memorial`; setFile(m.path); if (m.base) loadResults(m.base); }
  else if (m.type === "file") { setFile(m.path); state.network = null; state.pumps = []; state.dataBase = ""; ifcGeometry = null; state.area.clear(); world.clear(); objects = []; batch = null; $("vazio3d").hidden = false; summary(); pumpsTable(); updateButtons(); }
  else if (m.type === "log") log(m.line);
  else if (m.type === "status") $("status").textContent = m.text;
  else if (m.type === "busy") { $("ocupado").hidden = !m.on; if (m.text) $("ocupadoTxt").textContent = m.text; }
  else if (m.type === "done") { if (m.base) loadResults(m.base); }
  else if (m.type === "pumpChosen") { state.selectedPumpId = m.id; pumpsTable(); updateButtons(); }
});
function setFile(path) { state.path = path || ""; $("arquivo").textContent = path ? path.split(/[\\/]/).pop() : "nenhum IFC selecionado"; $("btnAnalisar").disabled = !path; }
updateButtons();
send({ cmd: "ready" });
// Fora da janela (navegador comum, para conferência): ?base=<caminho do IFC sem extensão> carrega os resultados.
if (!host) { const base = new URLSearchParams(location.search).get("base"); if (base) { setFile(base + ".ifc"); loadResults(base); } }

// Depuração no console do navegador.
window.grelhaDebug = { THREE, get batch() { return batch; }, get objects() { return objects; }, ortho, perspective, controls2d, controls3d, world, get camera() { return camera; } };
