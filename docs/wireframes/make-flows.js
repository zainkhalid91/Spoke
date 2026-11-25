// Splits docs/UI_FLOW.md into one HTML page per Mermaid diagram (flows-NN.html) for render.sh.
const fs = require("fs"), path = require("path");
const md = fs.readFileSync(path.join(__dirname, "..", "UI_FLOW.md"), "utf8");
const re = /^## (.+)\n[\s\S]*?```mermaid\n([\s\S]*?)```/gm;
let m, i = 0;
while ((m = re.exec(md))) {
  i++;
  const esc = s => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  fs.writeFileSync(path.join(__dirname, `flows-${String(i).padStart(2, "0")}.html`), `<!doctype html><html><head><meta charset="utf-8">
<style>body{background:#0c0d10;color:#e7e9ee;font-family:"Cascadia Mono",Consolas,monospace;margin:0;padding:24px 32px;width:1216px}
h2{font-size:13px;letter-spacing:.12em;text-transform:uppercase;color:#9aa0ab;font-weight:600;margin:0 0 18px}
.mermaid{display:flex;justify-content:center}</style>
<script src="https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js"></script></head><body>
<h2>Flow ${esc(m[1])}</h2><pre class="mermaid">${esc(m[2])}</pre>
<script>mermaid.initialize({startOnLoad:true,theme:"base",themeVariables:{darkMode:true,background:"#0c0d10",primaryColor:"#1b1e24",primaryBorderColor:"#3a3f4a",primaryTextColor:"#e7e9ee",lineColor:"#f5a524",secondaryColor:"#16181c",tertiaryColor:"#121418",noteBkgColor:"#22252c",noteTextColor:"#e7e9ee",noteBorderColor:"#f5a524",actorBkg:"#1b1e24",actorBorder:"#f5a524",actorTextColor:"#e7e9ee",signalColor:"#e7e9ee",signalTextColor:"#e7e9ee",labelBoxBkgColor:"#1b1e24",fontFamily:"Cascadia Mono, Consolas, monospace",fontSize:"14px"},flowchart:{useMaxWidth:true},state:{useMaxWidth:true}});</script>
</body></html>`);
}
console.log("wrote", i, "flow pages");
