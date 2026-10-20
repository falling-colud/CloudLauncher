// The live planning-board viewer. On its own it runs at /plans/view/<token> (the "Open live board"
// button on a board posted in Discord); inside Discord, ../discord-activity/activity.js finds the token for
// the channel the Activity was opened in and calls start().
//
// The server draws each board as SVG (the same drawing as the picture Discord gets), so this only
// shows it, pans and zooms it, and swaps it when the board changes: the state request below is a
// long poll that answers the moment a launcher stores a new revision.

const $ = (id) => document.getElementById(id);

const MIN_ZOOM = 0.08;
const MAX_ZOOM = 4;
const GRID = 24;

export function start({ token, board: wanted = null }) {
  const stage = $("stage");
  const host = $("board");
  const tabs = $("tabs");
  const live = $("live");
  const note = $("note");
  const png = $("png");
  const base = `/plans/view/${encodeURIComponent(token)}`;

  let revision = null;
  let updatedAt = null;
  let pack = "";
  let boards = [];
  let current = wanted;
  let shownHash = null;
  let view = { x: 0, y: 0, k: 1 };
  let box = { x: 0, y: 0, w: 1, h: 1 }; // the SVG's view box, in board units
  let fitted = false;
  let loading = null;

  // ── data ───────────────────────────────────────────────────────────────────

  async function poll() {
    let delay = 2000;
    for (;;) {
      try {
        const after = revision === null ? "" : `after=${revision}&wait=25`;
        const res = await fetch(`${base}/state?${after}`, { cache: "no-store" });
        if (res.status === 404) return ended();
        if (res.status === 204) { setLive(true); continue; }
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        apply(await res.json());
        setLive(true);
        delay = 2000;
      } catch {
        setLive(false);
        await sleep(delay);
        delay = Math.min(delay * 2, 30000);
      }
    }
  }

  function apply(state) {
    revision = state.revision;
    updatedAt = state.updatedAt ? new Date(state.updatedAt) : null;
    pack = state.pack || "";
    boards = state.boards || [];
    $("pack").textContent = pack;
    if (!boards.some((b) => b.id === current)) current = boards[0]?.id ?? null;
    renderTabs();
    renderNote();
    if (!current) return empty();
    const board = boards.find((b) => b.id === current);
    if (board.hash !== shownHash) load(board);
  }

  async function load(board) {
    const ticket = (loading = {});
    try {
      const res = await fetch(`${base}/board/${encodeURIComponent(board.id)}.svg`, { cache: "no-cache" });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const text = await res.text();
      if (ticket !== loading) return; // a newer board was asked for meanwhile
      const svg = new DOMParser().parseFromString(text, "image/svg+xml").documentElement;
      if (svg.nodeName !== "svg") throw new Error("not an SVG");
      const [x, y, w, h] = (svg.getAttribute("viewBox") || "0 0 1 1").split(/[\s,]+/).map(Number);
      box = { x, y, w, h };
      stage.querySelector(".message")?.remove();
      host.replaceChildren(document.importNode(svg, true));
      shownHash = board.hash;
      png.href = `${base}/board/${encodeURIComponent(board.id)}.png`;
      png.download = `${board.name}.png`;
      document.title = `${board.name} · ${pack} · CloudLauncher`;
      // A board nobody has moved around follows its own size as it grows; one they have stays put.
      if (!fitted || !moved) fit();
      else paint();
    } catch {
      if (ticket === loading) message("Couldn't load this board", "Trying again in a moment.", true);
      setTimeout(() => { if (ticket === loading) { shownHash = null; apply({ revision, updatedAt, pack, boards }); } }, 4000);
    }
  }

  // ── chrome ─────────────────────────────────────────────────────────────────

  function renderTabs() {
    tabs.replaceChildren(...boards.map((b) => {
      const tab = document.createElement("button");
      tab.type = "button";
      tab.className = "tab";
      tab.setAttribute("role", "tab");
      tab.setAttribute("aria-selected", String(b.id === current));
      tab.textContent = b.name;
      const count = document.createElement("span");
      count.className = "count";
      count.textContent = b.cards;
      tab.append(count);
      tab.addEventListener("click", () => select(b.id));
      return tab;
    }));
    tabs.hidden = boards.length < 2;
    tabs.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest", inline: "nearest" });
  }

  function select(id) {
    if (id === current) return;
    current = id;
    shownHash = null;
    fitted = false;
    const url = new URL(location.href);
    url.searchParams.set("board", id);
    history.replaceState(null, "", url);
    renderTabs();
    load(boards.find((b) => b.id === id));
  }

  function renderNote() {
    if (!updatedAt) { note.textContent = ""; return; }
    note.textContent = `Updated ${relative(updatedAt)}`;
  }

  function setLive(on) {
    live.classList.toggle("on", on);
    live.title = on ? "The board updates here as soon as it changes" : "Reconnecting…";
  }

  function ended() {
    setLive(false);
    host.replaceChildren();
    message("This board link has ended", "The channel it belonged to was unlinked from the pack.");
  }

  function empty() {
    host.replaceChildren();
    message("Nothing on these boards yet", "They show up here as soon as someone adds a card.");
  }

  function message(title, text, transient = false) {
    const div = document.createElement("div");
    div.className = "message";
    const strong = document.createElement("strong");
    strong.textContent = title;
    const p = document.createElement("div");
    p.textContent = text;
    div.append(strong, p);
    host.replaceChildren();
    stage.querySelector(".message")?.remove();
    stage.append(div);
    if (transient) setTimeout(() => div.remove(), 6000);
  }

  // ── pan and zoom ───────────────────────────────────────────────────────────

  function fit() {
    const r = stage.getBoundingClientRect();
    moved = false;
    // Not laid out yet (a hidden tab or pane): the ResizeObserver below fits once there is room.
    if (r.width < 20 || r.height < 20) { fitted = false; return; }
    const k = clamp(Math.min(r.width / box.w, r.height / box.h) * 0.96, MIN_ZOOM, 1.5);
    view = { k, x: (r.width - box.w * k) / 2, y: (r.height - box.h * k) / 2 };
    fitted = true;
    paint();
  }

  // Until someone pans or zooms, the board keeps fitting the window as it changes size.
  let moved = false;
  new ResizeObserver(() => {
    if (shownHash === null) return;
    if (moved && fitted) paint();
    else fit();
  }).observe(stage);

  function zoomAt(px, py, factor) {
    moved = true;
    const k = clamp(view.k * factor, MIN_ZOOM, MAX_ZOOM);
    const f = k / view.k;
    view = { k, x: px - (px - view.x) * f, y: py - (py - view.y) * f };
    paint();
  }

  function paint() {
    host.style.transform = `translate(${view.x}px, ${view.y}px) scale(${view.k})`;
    // The grid follows the board: lines on whole cells of board space, the minor ones dropped once
    // they would crowd together.
    const cell = GRID * view.k;
    const ox = view.x - box.x * view.k;
    const oy = view.y - box.y * view.k;
    const major = cell * 4;
    const minorOn = cell >= 9;
    stage.style.backgroundImage = [
      "linear-gradient(to right, var(--grid-major) 1px, transparent 1px)",
      "linear-gradient(to bottom, var(--grid-major) 1px, transparent 1px)",
      ...(minorOn ? ["linear-gradient(to right, var(--grid) 1px, transparent 1px)",
                     "linear-gradient(to bottom, var(--grid) 1px, transparent 1px)"] : [])
    ].join(",");
    const sizes = [`${major}px ${major}px`, `${major}px ${major}px`, ...(minorOn ? [`${cell}px ${cell}px`, `${cell}px ${cell}px`] : [])];
    stage.style.backgroundSize = sizes.join(",");
    const pos = `${mod(ox, major)}px ${mod(oy, major)}px`;
    const minorPos = `${mod(ox, cell)}px ${mod(oy, cell)}px`;
    stage.style.backgroundPosition = [pos, pos, ...(minorOn ? [minorPos, minorPos] : [])].join(",");
  }

  const pointers = new Map();
  let pinch = null;

  stage.addEventListener("pointerdown", (e) => {
    stage.setPointerCapture(e.pointerId);
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    stage.classList.add("dragging");
    if (pointers.size === 2) pinch = pinchState();
  });

  stage.addEventListener("pointermove", (e) => {
    const last = pointers.get(e.pointerId);
    if (!last) return;
    const now = { x: e.clientX, y: e.clientY };
    pointers.set(e.pointerId, now);
    if (pointers.size === 1) {
      view.x += now.x - last.x;
      view.y += now.y - last.y;
      moved = true;
      paint();
    } else if (pointers.size === 2 && pinch) {
      const next = pinchState();
      const r = stage.getBoundingClientRect();
      zoomAt(next.cx - r.left, next.cy - r.top, next.d / pinch.d);
      view.x += next.cx - pinch.cx;
      view.y += next.cy - pinch.cy;
      paint();
      pinch = next;
    }
  });

  const release = (e) => {
    pointers.delete(e.pointerId);
    if (pointers.size < 2) pinch = null;
    if (pointers.size === 0) stage.classList.remove("dragging");
  };
  stage.addEventListener("pointerup", release);
  stage.addEventListener("pointercancel", release);

  function pinchState() {
    const [a, b] = [...pointers.values()];
    return { cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2, d: Math.max(1, Math.hypot(a.x - b.x, a.y - b.y)) };
  }

  stage.addEventListener("wheel", (e) => {
    e.preventDefault();
    const r = stage.getBoundingClientRect();
    // A trackpad pinch arrives as a wheel with ctrl held and small deltas.
    const factor = Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0015));
    zoomAt(e.clientX - r.left, e.clientY - r.top, factor);
  }, { passive: false });

  stage.addEventListener("dblclick", (e) => {
    const r = stage.getBoundingClientRect();
    zoomAt(e.clientX - r.left, e.clientY - r.top, 1.6);
  });

  window.addEventListener("keydown", (e) => {
    if (e.target instanceof HTMLInputElement) return;
    const r = stage.getBoundingClientRect();
    if (e.key === "+" || e.key === "=") zoomAt(r.width / 2, r.height / 2, 1.25);
    else if (e.key === "-") zoomAt(r.width / 2, r.height / 2, 0.8);
    else if (e.key === "0") fit();
  });

  $("fit").addEventListener("click", fit);
  setInterval(renderNote, 30000);

  poll();
}

function relative(date) {
  const seconds = Math.round((date.getTime() - Date.now()) / 1000);
  const fmt = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
  const abs = Math.abs(seconds);
  if (abs < 60) return fmt.format(seconds, "second");
  if (abs < 3600) return fmt.format(Math.round(seconds / 60), "minute");
  if (abs < 86400) return fmt.format(Math.round(seconds / 3600), "hour");
  return fmt.format(Math.round(seconds / 86400), "day");
}

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
const mod = (v, m) => ((v % m) + m) % m;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

if (location.pathname.startsWith("/plans/view/")) {
  const token = decodeURIComponent(location.pathname.slice("/plans/view/".length).replace(/\/+$/, ""));
  start({ token, board: new URLSearchParams(location.search).get("board") });
}
