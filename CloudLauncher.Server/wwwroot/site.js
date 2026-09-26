/* CloudLauncher website. Everything here is optional: without it the page still reads, links still
   download, screenshots still show (a screenshot that fails to load is simply removed). */
(function () {
  "use strict";

  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };

  function ready(fn) {
    if (document.readyState !== "loading") fn();
    else document.addEventListener("DOMContentLoaded", fn);
  }

  function getJson(url) {
    return fetch(url, { cache: "no-store", headers: { Accept: "application/json" } })
      .then(function (r) { return r.ok && r.status !== 204 ? r.json() : null; })
      .catch(function () { return null; });
  }

  function fmtSize(bytes) {
    var mb = bytes / 1048576;
    return (mb >= 10 ? Math.round(mb) : Math.round(mb * 10) / 10) + " MB";
  }

  function fmtDate(iso) {
    try {
      var d = new Date(iso);
      if (isNaN(d.getTime())) return "";
      return d.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
    } catch (e) { return ""; }
  }

  function setAll(sel, text) {
    $$(sel).forEach(function (el) { el.textContent = text; });
  }

  // Screenshots are optional: one that fails to load is removed instead of showing a broken box.
  // An image that failed before this script ran is already complete with no width.
  function dropBrokenImages() {
    $$("img[data-optional]").forEach(function (img) {
      if (img.complete && img.naturalWidth === 0) img.remove();
      else img.addEventListener("error", function () { img.remove(); });
    });
  }
  dropBrokenImages();

  function show(sel) {
    $$(sel).forEach(function (el) { el.hidden = false; });
  }

  // ------------------------------------------------ version, size, date

  function loadLatest() {
    getJson("/launcher/latest").then(function (info) {
      if (!info || !info.version) return;
      setAll("[data-version]", info.version);
      show("[data-version-line]");
      if (info.installerSize > 0) {
        setAll("[data-size]", fmtSize(info.installerSize));
        show("[data-size-line]");
      }
      var date = info.releasedAt ? fmtDate(info.releasedAt) : "";
      if (date) {
        setAll("[data-date]", date);
        show("[data-date-line]");
      }
    });
  }

  // ------------------------------------------------ what's new
  //
  // /launcher/releases is the launcher's own changelog: newest first, each with plain-text notes
  // whose first paragraph is a one-line summary and whose later paragraphs may open with an
  // ALL-CAPS run-in heading ("THE GRAPH. Dragging a node ..."). Rendered as text, never as HTML.

  function tidy(s) {
    return s.replace(/ -- /g, " \u2013 ").replace(/->/g, "\u2192");
  }

  function paragraph(text) {
    var p = document.createElement("p");
    var m = text.match(/^([^a-z]{4,}?[.:])\s+([\s\S]*)$/);
    if (m && /[A-Z][^A-Z]*[A-Z]/.test(m[1])) {
      var b = document.createElement("b");
      b.className = "runin";
      b.textContent = tidy(m[1]);
      p.appendChild(b);
      p.appendChild(document.createTextNode(" " + tidy(m[2])));
    } else {
      p.textContent = tidy(text);
    }
    return p;
  }

  function paragraphsOf(notes) {
    return String(notes || "").split(/\r?\n\s*\r?\n/).map(function (s) {
      return s.replace(/\s*\r?\n\s*/g, " ").trim();
    }).filter(Boolean);
  }

  function releaseCard(rel, latest) {
    var card = document.createElement("article");
    card.className = "rel px";
    var head = document.createElement("div");
    head.className = "rel-head";
    var h = document.createElement("h3");
    h.textContent = rel.version;
    head.appendChild(h);
    var date = fmtDate(rel.releasedAt);
    if (date) {
      var t = document.createElement("time");
      t.dateTime = rel.releasedAt;
      t.textContent = date;
      head.appendChild(t);
    }
    if (latest) {
      var pill = document.createElement("span");
      pill.className = "pill shared px rs";
      pill.textContent = "Latest";
      head.appendChild(pill);
    }
    card.appendChild(head);

    var paras = paragraphsOf(rel.notes);
    if (paras.length) {
      var lead = document.createElement("p");
      lead.textContent = tidy(paras[0]);
      card.appendChild(lead);
    }
    if (paras.length > 1) {
      var det = document.createElement("details");
      var sum = document.createElement("summary");
      sum.textContent = "Full notes";
      det.appendChild(sum);
      var box = document.createElement("div");
      box.className = "notes";
      paras.slice(1).forEach(function (t) { box.appendChild(paragraph(t)); });
      det.appendChild(box);
      card.appendChild(det);
    }
    return card;
  }

  function loadReleases() {
    var host = $("[data-releases]");
    if (!host) return;
    getJson("/launcher/releases").then(function (list) {
      if (!Array.isArray(list) || !list.length) return;
      list = list.filter(function (r) { return r && r.version; });
      if (!list.length) return;
      list.slice(0, 3).forEach(function (rel, i) { host.appendChild(releaseCard(rel, i === 0)); });
      if (list.length > 3) {
        var more = document.createElement("details");
        more.className = "older rel px";
        var sum = document.createElement("summary");
        sum.textContent = "Older releases";
        more.appendChild(sum);
        var box = document.createElement("div");
        box.className = "notes";
        list.slice(3, 15).forEach(function (rel) {
          var p = document.createElement("p");
          var b = document.createElement("b");
          b.textContent = rel.version + (rel.releasedAt ? " \u00b7 " + fmtDate(rel.releasedAt) : "");
          p.appendChild(b);
          var first = paragraphsOf(rel.notes)[0];
          if (first) {
            var sentence = first.match(/^[\s\S]*?[.!?](?=\s+["\u201c(A-Z]|$)/);   // the first sentence
            p.appendChild(document.createTextNode(" " + tidy(sentence ? sentence[0] : first)));
          }
          box.appendChild(p);
        });
        more.appendChild(box);
        host.appendChild(more);
      }
      show("[data-if-releases]");
      snapSoon();
    });
  }

  // ------------------------------------------------ screenshots
  //
  // A frame with a real capture becomes a button that opens it full size. A frame whose capture is
  // missing keeps its drawing and stays a plain figure.

  var viewer, viewerImg, viewerTitle, lastFrame;

  function openViewer(frame) {
    var img = $(".shot > img", frame);
    if (!img || !viewer) return;
    lastFrame = frame;
    viewerImg.src = img.currentSrc || img.src;
    viewerImg.alt = img.alt;
    if (viewerTitle) viewerTitle.textContent = img.alt;
    if (typeof viewer.showModal === "function") { viewer.showModal(); snapSoon(); }
    else window.open(viewerImg.src, "_blank", "noopener");
  }

  function closeViewer() {
    if (viewer && viewer.open) viewer.close();
  }

  function armFrame(frame) {
    var img = $(".shot > img", frame);
    if (!img || frame.classList.contains("zoomable")) return;
    var arm = function () {
      if (!img.isConnected || !img.naturalWidth) return;
      frame.classList.add("zoomable");
      frame.setAttribute("role", "button");
      frame.setAttribute("tabindex", "0");
      frame.setAttribute("aria-label", "View larger: " + img.alt);
    };
    if (img.complete) arm();
    else img.addEventListener("load", arm);
    frame.addEventListener("click", function () { if (frame.classList.contains("zoomable")) openViewer(frame); });
    frame.addEventListener("keydown", function (e) {
      if (!frame.classList.contains("zoomable")) return;
      if (e.key === "Enter" || e.key === " ") { e.preventDefault(); openViewer(frame); }
    });
  }

  function setupShots() {
    viewer = $("#viewer");
    if (viewer) {
      viewerImg = $("img", viewer);
      viewerTitle = $(".viewer-title", viewer);
      viewer.addEventListener("click", function (e) {
        // the backdrop, the picture and the close button all close it
        if (e.target === viewer || e.target === viewerImg || e.target.closest("[data-close]")) closeViewer();
      });
      viewer.addEventListener("close", function () {
        viewerImg.removeAttribute("src");
        if (lastFrame) lastFrame.focus({ preventScroll: true });
      });
    }
    $$(".win").forEach(function (frame) {
      if (!frame.closest("#viewer")) armFrame(frame);
    });
  }

  // ------------------------------------------------ tabs (WAI-ARIA tabs pattern)

  function setupTabs() {
    $$("[data-tabs]").forEach(function (list) {
      var tabs = $$("[role=tab]", list);
      function select(tab, focus) {
        tabs.forEach(function (t) {
          var on = t === tab;
          t.setAttribute("aria-selected", on ? "true" : "false");
          t.tabIndex = on ? 0 : -1;
          var panel = document.getElementById(t.getAttribute("aria-controls"));
          if (panel) panel.hidden = !on;
        });
        if (focus) tab.focus();
        snapSoon();
      }
      // Both panels are in the page for readers without script; with it, only the chosen one shows.
      select(tabs.filter(function (t) { return t.getAttribute("aria-selected") === "true"; })[0] || tabs[0], false);
      tabs.forEach(function (tab, i) {
        tab.addEventListener("click", function () { select(tab, false); });
        tab.addEventListener("keydown", function (e) {
          var next = null;
          if (e.key === "ArrowRight" || e.key === "ArrowDown") next = tabs[(i + 1) % tabs.length];
          else if (e.key === "ArrowLeft" || e.key === "ArrowUp") next = tabs[(i - 1 + tabs.length) % tabs.length];
          else if (e.key === "Home") next = tabs[0];
          else if (e.key === "End") next = tabs[tabs.length - 1];
          if (next) { e.preventDefault(); select(next, true); }
        });
      });
    });
  }

  // ------------------------------------------------ appearance demo
  //
  // The same derivation as ThemeService.ApplySlateAccent: hover is 12 % lighter, pressed 12 %
  // darker, and accent-coloured text uses Readable(accent, bg) (step toward white until the
  // brightness gap is 0.34). Text on an accent fill is whichever of the two Slate text colours
  // has more contrast, as in the launcher. The page then nudges link text on to 4.5:1, which
  // the launcher does not need.

  var ACCENTS = [
    ["Rust", "#601B00"], ["Terracotta", "#D9805E"], ["Ember", "#E06C4B"], ["Amber", "#E0A458"],
    ["Moss", "#7BB36A"], ["Mint", "#5CC8A8"], ["Sky", "#5BA8E0"], ["Cobalt", "#5C7CFA"],
    ["Lavender", "#9B86E0"], ["Orchid", "#D076C9"], ["Rose", "#E5698A"], ["Slate", "#9AA5B4"],
    ["Bone", "#D8D2C4"]
  ];
  var BG = [0x16, 0x16, 0x15], WHITE = [255, 255, 255], BLACK = [0, 0, 0];
  var DARK_TEXT = [0x14, 0x14, 0x13], LIGHT_TEXT = [0xF5, 0xF4, 0xEF];

  function rgb(hex) { var n = parseInt(hex.slice(1), 16); return [(n >> 16) & 255, (n >> 8) & 255, n & 255]; }
  function hex(c) { return "#" + c.map(function (v) { return (v < 16 ? "0" : "") + v.toString(16); }).join(""); }
  function mix(a, b, t) { return a.map(function (v, i) { return Math.round(v + (b[i] - v) * t); }); }
  function luma(c) { return (0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2]) / 255; }
  function readable(c, bg) {
    var target = luma(bg) < 0.5 ? WHITE : BLACK, r = c;
    for (var i = 0; i < 8 && Math.abs(luma(r) - luma(bg)) < 0.34; i++) r = mix(r, target, 0.16);
    return r;
  }
  function lin(v) { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }
  function relLum(c) { return 0.2126 * lin(c[0]) + 0.7152 * lin(c[1]) + 0.0722 * lin(c[2]); }
  function contrast(a, b) {
    var x = relLum(a), y = relLum(b);
    return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
  }

  var ACCENT_PROPS = ["--accent", "--accent-hover", "--accent-pressed", "--on-accent", "--accent-text", "--accent-text-hover"];

  function applyAccent(value) {
    var s = document.documentElement.style;
    if (value.toUpperCase() === "#601B00") {           // the stylesheet's own values
      ACCENT_PROPS.forEach(function (p) { s.removeProperty(p); });
      return;
    }
    var a = rgb(value);
    var text = readable(a, BG);
    for (var i = 0; i < 8 && contrast(text, BG) < 4.5; i++) text = mix(text, WHITE, 0.16);
    var on = contrast(DARK_TEXT, a) > contrast(LIGHT_TEXT, a) ? DARK_TEXT : LIGHT_TEXT;
    s.setProperty("--accent", hex(a));
    s.setProperty("--accent-hover", hex(mix(a, WHITE, 0.12)));
    s.setProperty("--accent-pressed", hex(mix(a, BLACK, 0.12)));
    s.setProperty("--on-accent", hex(on));
    s.setProperty("--accent-text", hex(text));
    s.setProperty("--accent-text-hover", hex(mix(text, WHITE, 0.22)));
  }

  // Slate's staircase for any radius, as site.css has it for radius 3 (see the comment there).
  function L(n) { return n === 0 ? "0" : n === 1 ? "var(--p)" : "calc(var(--p)*" + n + ")"; }
  function R(n) { return n === 0 ? "100%" : n === 1 ? "calc(100% - var(--p))" : "calc(100% - var(--p)*" + n + ")"; }
  function poly(r) {
    if (r <= 0) return "none";
    var pts = [], last = "";
    function to(x, y) { var p = x + " " + y; if (p !== last) { pts.push(p); last = p; } }
    function tr(i) { return i < r ? r - i : 0; }
    to(L(r), "0"); to(R(r), "0");
    for (var i = 0; i < r; i++) { to(R(tr(i)), L(i + 1)); to(R(tr(i + 1)), L(i + 1)); }
    to("100%", R(r));
    for (var k = r - 1; k >= 0; k--) { to(R(tr(k)), R(k + 1)); to(R(tr(k)), R(k)); }
    to(L(r), "100%");
    for (k = 0; k < r; k++) { to(L(tr(k)), R(k + 1)); to(L(tr(k + 1)), R(k + 1)); }
    to("0", L(r));
    for (k = r - 1; k >= 0; k--) { to(L(tr(k)), L(k + 1)); to(L(tr(k)), L(k)); }
    if (pts[pts.length - 1] === pts[0]) pts.pop();
    return "polygon(" + pts.join(",") + ")";
  }
  function fill(r) {
    var k = Math.max(r - 1, 0), layers = [];
    for (var j = 0; j <= k; j++) {
      var x = 1 + (k - j), y = 1 + j;
      layers.push("linear-gradient(var(--face) 0 0) " + L(x) + " " + L(y) + "/calc(100% - var(--p)*" + 2 * x +
        ") calc(100% - var(--p)*" + 2 * y + ") no-repeat");
    }
    return layers.join(",");
  }
  function shapeRule(sel, r) {
    return sel + "{--clip:" + poly(r) + ";--ring:" + poly(r + 2) + ";--fill:" + fill(r) + "}";
  }

  function applyCorners(r) {
    var tag = document.getElementById("corner-style");
    if (r === 3) { if (tag) tag.remove(); return; }
    if (!tag) {
      tag = document.createElement("style");
      tag.id = "corner-style";
      document.head.appendChild(tag);
    }
    tag.textContent = shapeRule(".px", r) + shapeRule(".px.rs", Math.min(r, 2)) +
      ".shot{clip-path:" + poly(Math.max(r - 1, 0)) + "}";
  }

  // A radio group with a roving tabindex: Tab enters it once, arrows move the choice.
  function radioGroup(host, items, onPick) {
    var buttons = items.map(function (item) {
      var b = document.createElement("button");
      b.type = "button";
      b.setAttribute("role", "radio");
      b.className = item.className;
      if (item.style) b.setAttribute("style", item.style);
      if (item.title) b.title = item.title;
      if (item.html) b.innerHTML = item.html;
      else b.textContent = item.label;
      if (item.aria) b.setAttribute("aria-label", item.aria);
      host.appendChild(b);
      return b;
    });
    function pick(index, focus) {
      buttons.forEach(function (b, i) {
        b.setAttribute("aria-checked", i === index ? "true" : "false");
        b.tabIndex = i === index ? 0 : -1;
      });
      if (focus) buttons[index].focus();
      onPick(items[index].value);
      snapSoon();
    }
    buttons.forEach(function (b, i) {
      b.addEventListener("click", function () { pick(i, false); });
      b.addEventListener("keydown", function (e) {
        var n = null;
        if (e.key === "ArrowRight" || e.key === "ArrowDown") n = (i + 1) % buttons.length;
        else if (e.key === "ArrowLeft" || e.key === "ArrowUp") n = (i - 1 + buttons.length) % buttons.length;
        else if (e.key === "Home") n = 0;
        else if (e.key === "End") n = buttons.length - 1;
        if (n !== null) { e.preventDefault(); pick(n, true); }
      });
    });
    return pick;
  }

  function setupLook() {
    var demo = $("#look-demo");
    if (!demo) return;
    var acc = $("[data-accents]", demo), cor = $("[data-corners]", demo);
    var pickAccent = radioGroup(acc, ACCENTS.map(function (a) {
      return { value: a[1], className: "swatch px rs", style: "--sw:" + a[1], title: a[0], aria: a[0], html: "" };
    }), applyAccent);
    var pickCorner = radioGroup(cor, [0, 1, 2, 3, 4].map(function (r) {
      return { value: r, className: "px rs", label: String(r), aria: r === 0 ? "Square" : r + (r === 1 ? " step" : " steps") };
    }), function (r) {
      document.documentElement.setAttribute("data-r", String(r));
      applyCorners(r);
    });
    pickAccent(0, false);
    pickCorner(3, false);
    demo.hidden = false;
  }

  // ------------------------------------------------ invitation page

  function setupInvite() {
    var linkBox = $("[data-invite-link]");
    if (!linkBox) return;
    var m = location.pathname.match(/^\/invitations\/([A-Za-z0-9_=-]{1,64})\/?$/);
    var token = m ? m[1] : "";
    var link = token ? location.origin + "/invitations/" + token : "";
    if (link) {
      linkBox.textContent = link;
      show("[data-has-token]");
    } else {
      show("[data-no-token]");
    }
    var copy = $("[data-copy]");
    var status = $("[data-copy-status]");
    if (!copy || !link) return;
    copy.addEventListener("click", function () {
      var done = function (ok) {
        if (!status) return;
        status.textContent = ok ? "Copied. Paste it into Redeem a link on the Sharing page." : "Select the link above and copy it.";
      };
      if (navigator.clipboard && window.isSecureContext) {
        navigator.clipboard.writeText(link).then(function () { done(true); }, function () { done(fallbackCopy(link)); });
      } else {
        done(fallbackCopy(link));
      }
    });
  }

  function fallbackCopy(text) {
    try {
      var ta = document.createElement("textarea");
      ta.value = text;
      ta.setAttribute("readonly", "");
      ta.style.position = "fixed";
      ta.style.opacity = "0";
      document.body.appendChild(ta);
      ta.select();
      var ok = document.execCommand("copy");
      ta.remove();
      return ok;
    } catch (e) { return false; }
  }

  // ------------------------------------------------ pixel snapping
  //
  // Slate's shapes are drawn in whole device pixels (SlateBorder rounds its step to them). Chrome
  // snaps a clip-path's box to whole CSS pixels, so at 125 % or 150 % a box that layout places
  // between pixels (a right-aligned button, a card in a third of an odd width) gets one soft column.
  // Each .px therefore gets the fraction that moves its painted shape onto the nearest point that is
  // a whole pixel in both CSS and device terms (every 2 CSS px at 150 %, every 4 at 125 %).

  var snapQueued = false;
  function snapAll() {
    snapQueued = false;
    var dpr = window.devicePixelRatio || 1;
    var root = document.documentElement.style;
    root.setProperty("--px2", (Math.max(1, Math.round(2 * dpr)) / dpr).toFixed(4) + "px");
    root.setProperty("--px1", (Math.max(1, Math.round(dpr)) / dpr).toFixed(4) + "px");

    var grid = 0;                                        // CSS px that are also whole device px
    for (var k = 1; k <= 4 && !grid; k++) if (Math.abs(k * dpr - Math.round(k * dpr)) < 1e-6) grid = k;
    var snap = grid
      ? function (v) { return Math.round(v / grid) * grid; }
      : function (v) { return Math.round(v * dpr) / dpr; };   // odd scales: the nearest device pixel

    var sx = window.scrollX || 0, sy = window.scrollY || 0;
    var els = $$(".px");
    var rects = els.map(function (el) { return el.getBoundingClientRect(); });   // read all, then write
    els.forEach(function (el, i) {
      var r = rects[i], s = el.style;
      if (!r.width || !r.height) return;
      var left = r.left + sx, top = r.top + sy, right = r.right + sx, bottom = r.bottom + sy;
      s.setProperty("--sl", (snap(left) - left).toFixed(4) + "px");
      s.setProperty("--st", (snap(top) - top).toFixed(4) + "px");
      s.setProperty("--sr", (right - snap(right)).toFixed(4) + "px");
      s.setProperty("--sb", (bottom - snap(bottom)).toFixed(4) + "px");
    });
  }
  function snapSoon() {
    if (snapQueued) return;
    snapQueued = true;
    requestAnimationFrame(snapAll);
  }

  ready(function () {
    loadLatest();
    loadReleases();
    setupShots();
    setupTabs();
    setupLook();
    setupInvite();
    snapSoon();
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(snapSoon);
    window.addEventListener("load", snapSoon);
    window.addEventListener("resize", snapSoon);
    setTimeout(snapSoon, 700);                          // after the hero's entrance motion
    if ("ResizeObserver" in window) new ResizeObserver(snapSoon).observe(document.body);
    document.addEventListener("toggle", snapSoon, true); // <details> opening moves what is below it
  });
})();
