// Draws a Spoke wheel as SVG. Low-fidelity wireframe helper, not app code.
// spokeWheel(el, opts): opts = {cx, cy, items:[{label,type,sub,ghost}], hover, accent, hub:{t,s}, rOut, rIn, petals:[...], hubSize, dimItems, numbers}
function spokeWheel(el, o) {
  const NS = "http://www.w3.org/2000/svg";
  const n = o.items.length, rO = o.rOut || 170, rI = o.rIn || 74, gap = 0.035;
  const acc = o.accent || "#f5a524";
  const W = (rO + 140) * 2;
  const svg = document.createElementNS(NS, "svg");
  svg.setAttribute("width", W); svg.setAttribute("height", W);
  svg.setAttribute("viewBox", `${-W/2} ${-W/2} ${W} ${W}`);
  svg.style.position = "absolute"; svg.style.left = (o.cx - W/2) + "px"; svg.style.top = (o.cy - W/2) + "px";
  svg.style.overflow = "visible";
  const mk = (t, a, p) => { const e = document.createElementNS(NS, t); for (const k in a) e.setAttribute(k, a[k]); (p || svg).appendChild(e); return e; };
  const defs = mk("defs", {});
  const f = mk("filter", { id: "sh" + o.cx, x: "-50%", y: "-50%", width: "200%", height: "200%" }, defs);
  mk("feDropShadow", { dx: 0, dy: 10, stdDeviation: 18, "flood-color": "#000", "flood-opacity": .55 }, f);
  const pt = (r, a) => [r * Math.cos(a), r * Math.sin(a)];
  const seg = (r1, r2, a1, a2) => {
    const [x1, y1] = pt(r2, a1), [x2, y2] = pt(r2, a2), [x3, y3] = pt(r1, a2), [x4, y4] = pt(r1, a1);
    const L = a2 - a1 > Math.PI ? 1 : 0;
    return `M${x1} ${y1} A${r2} ${r2} 0 ${L} 1 ${x2} ${y2} L${x3} ${y3} A${r1} ${r1} 0 ${L} 0 ${x4} ${y4}Z`;
  };
  const g = mk("g", { filter: `url(#sh${o.cx})` });
  const step = 2 * Math.PI / n, start = -Math.PI / 2 - step / 2;
  o.items.forEach((it, i) => {
    const a1 = start + i * step + gap, a2 = start + (i + 1) * step - gap, am = (a1 + a2) / 2;
    const hov = i === o.hover, lift = hov ? 10 : 0;
    const [dx, dy] = pt(lift, am);
    const sg = mk("g", { transform: `translate(${dx} ${dy})`, opacity: it.ghost ? .35 : 1 }, g);
    mk("path", { d: seg(rI, rO, a1, a2), fill: hov ? "#262a32" : (it.fill || "#1b1e24"), stroke: hov ? "#3d424d" : "#2a2e36", "stroke-width": 1 }, sg);
    if (hov) mk("path", { d: seg(rI, rI + 4, a1, a2), fill: acc }, sg);
    if (it.chip) mk("path", { d: seg(rO - 3, rO, a1 + .02, a2 - .02), fill: it.chip, opacity: .9 }, sg);
    const rc = (rI + rO) / 2 + 4, [ix, iy] = pt(rc, am);
    if (it.thumb) {
      mk("rect", { x: ix - 22, y: iy - 26, width: 44, height: 30, rx: 4, fill: "#2a2e36", stroke: "#3a3f4a" }, sg);
      mk("path", { d: `M${ix-18} ${iy}l10-12 8 8 6-5 12 9z`, fill: "#3f4551" }, sg);
    } else if (it.text) {
      const t = mk("text", { x: ix, y: iy - 10, "text-anchor": "middle", fill: "#c9ccd3", "font-family": "Cascadia Mono, Consolas, monospace", "font-size": 10 }, sg);
      t.textContent = it.text;
    } else {
      mk("rect", { x: ix - 14, y: iy - 26, width: 28, height: 28, rx: 7, fill: "none", stroke: hov ? acc : "#5a606c", "stroke-width": 1.5 }, sg);
      mk("path", { d: `M${ix-14} ${iy-26}l28 28M${ix+14} ${iy-26}l-28 28`, stroke: "#3a3f4a", "stroke-width": 1 }, sg);
    }
    const lb = mk("text", { x: ix, y: iy + 18, "text-anchor": "middle", fill: hov ? "#ffffff" : "#aeb3bd", "font-family": "Cascadia Mono, Consolas, monospace", "font-size": 11, "letter-spacing": ".04em" }, sg);
    lb.textContent = it.label;
    if (it.sub) { const s = mk("text", { x: ix, y: iy + 31, "text-anchor": "middle", fill: "#6b717c", "font-family": "Cascadia Mono, Consolas, monospace", "font-size": 9 }, sg); s.textContent = it.sub; }
    if (o.numbers !== false) {
      const [nx, ny] = pt(rI + 12, am);
      const nt = mk("text", { x: nx, y: ny + 3.5, "text-anchor": "middle", fill: hov ? acc : "#5a606c", "font-family": "Cascadia Mono, Consolas, monospace", "font-size": 9 }, sg);
      nt.textContent = i < 9 ? String(i + 1) : "";
    }
    if (it.fan) { const [fx, fy] = pt(rO - 9, am); const ft = mk("text", { x: fx, y: fy + 4, "text-anchor": "middle", fill: acc, "font-size": 12 }, sg); ft.textContent = "▸"; }
  });
  // petals (sub-actions)
  if (o.petals && o.hover != null) {
    const am = start + (o.hover + .5) * step, pw = .28, pn = o.petals.length;
    o.petals.forEach((p, j) => {
      const c = am + (j - (pn - 1) / 2) * (pw + .03);
      const pg = mk("g", {}, g);
      mk("path", { d: seg(rO + 20, rO + 92, c - pw / 2, c + pw / 2), fill: j === o.petalHover ? "#262a32" : "#1b1e24", stroke: j === o.petalHover ? acc : "#2a2e36", "stroke-width": j === o.petalHover ? 1.5 : 1 }, pg);
      const [px, py] = pt(rO + 56, c);
      mk("rect", { x: px - 9, y: py - 16, width: 18, height: 18, rx: 5, fill: "none", stroke: "#5a606c", "stroke-width": 1.3 }, pg);
      const t = mk("text", { x: px, y: py + 16, "text-anchor": "middle", fill: "#c9ccd3", "font-family": "Cascadia Mono, Consolas, monospace", "font-size": 10 }, pg);
      t.textContent = p;
      const [nx, ny] = pt(rO + 28, c);
      const nt = mk("text", { x: nx, y: ny + 3, "text-anchor": "middle", fill: "#6b717c", "font-family": "Cascadia Mono, Consolas, monospace", "font-size": 8 }, pg); nt.textContent = j + 1;
    });
  }
  // needle (the spoke)
  if (o.hover != null) {
    const am = start + (o.hover + .5) * step;
    const [x1, y1] = pt(o.hubSize || 58, am), [x2, y2] = pt(rI + 2, am);
    mk("line", { x1: 0, y1: 0, x2, y2, stroke: acc, "stroke-width": 2, "stroke-linecap": "round", opacity: .9 });
    mk("circle", { cx: x2, cy: y2, r: 3.5, fill: acc });
  }
  // hub
  const hs = o.hubSize || 58;
  mk("circle", { cx: 0, cy: 0, r: hs, fill: "#121418", stroke: "#2a2e36", "stroke-width": 1, filter: `url(#sh${o.cx})` });
  mk("circle", { cx: 0, cy: 0, r: 5, fill: o.hover != null ? acc : "#3a3f4a" });
  const ht = mk("text", { x: 0, y: -14, "text-anchor": "middle", fill: "#e7e9ee", "font-family": "Cascadia Mono, Consolas, monospace", "font-size": o.hub && o.hub.t && o.hub.t.length > 11 ? 10 : 12, "font-weight": 600, "letter-spacing": ".08em" });
  ht.textContent = (o.hub && o.hub.t) || "";
  const hs2 = mk("text", { x: 0, y: 24, "text-anchor": "middle", fill: "#7d838e", "font-family": "Cascadia Mono, Consolas, monospace", "font-size": 9, "letter-spacing": ".06em" });
  hs2.textContent = (o.hub && o.hub.s) || "";
  if (o.hub && o.hub.s2) { const h3 = mk("text", { x: 0, y: 36, "text-anchor": "middle", fill: "#7d838e", "font-family": "Cascadia Mono, Consolas, monospace", "font-size": 9 }); h3.textContent = o.hub.s2; }
  if (o.hub && o.hub.prog != null) {
    const r = hs - 6, a = -Math.PI / 2 + 2 * Math.PI * o.hub.prog;
    mk("circle", { cx: 0, cy: 0, r, fill: "none", stroke: "#2a2e36", "stroke-width": 3 });
    const [ex, ey] = pt(r, a);
    mk("path", { d: `M0 ${-r} A${r} ${r} 0 ${o.hub.prog > .5 ? 1 : 0} 1 ${ex} ${ey}`, fill: "none", stroke: acc, "stroke-width": 3, "stroke-linecap": "round" });
  }
  el.appendChild(svg);
  return svg;
}
// Wheel row of pills under the wheel
function wheelRow(el, cx, y, names, on, accent) {
  const d = document.createElement("div");
  d.style.cssText = `position:absolute;left:${cx}px;top:${y}px;transform:translateX(-50%);display:flex;gap:6px;align-items:center;font:11px 'Cascadia Mono',Consolas,monospace;letter-spacing:.06em`;
  names.forEach((nm, i) => {
    const p = document.createElement("span");
    p.textContent = (i === on ? "● " : "") + nm.toUpperCase();
    p.style.cssText = `padding:5px 11px;border-radius:12px;border:1px solid ${i === on ? (accent || "#f5a524") : "#2a2e36"};color:${i === on ? "#e7e9ee" : "#7d838e"};background:${i === on ? "#1d2026" : "#121418cc"}`;
    d.appendChild(p);
  });
  const k = document.createElement("span"); k.textContent = "TAB ⇥"; k.style.cssText = "color:#5a606c;margin-left:6px"; d.appendChild(k);
  el.appendChild(d);
}
function desk(el) {
  el.insertAdjacentHTML("afterbegin", `<div class="desk"></div>
  <div class="win" style="left:70px;top:60px;width:620px;height:420px"><div class="tb">notes.txt — Notepad</div>${'<div class="ln"></div>'.repeat(9)}<div style="margin:6px 14px;font:13px Consolas;color:#9aa0ab">The quick brown fox|</div></div>
  <div class="win" style="left:640px;top:180px;width:560px;height:440px"><div class="tb">Browser</div>${'<div class="ln"></div>'.repeat(12)}</div>
  <div class="taskbar"><div class="tray"><i></i><i></i><i class="sp"></i><i></i></div></div>`);
}
function cursor(el, x, y) {
  el.insertAdjacentHTML("beforeend", `<svg style="position:absolute;left:${x}px;top:${y}px;z-index:8" width="18" height="24" viewBox="0 0 18 24"><path d="M1 1v19l5-5 3.5 7 3-1.5L9 13.5h7z" fill="#fff" stroke="#000" stroke-width="1.2"/></svg>`);
}
