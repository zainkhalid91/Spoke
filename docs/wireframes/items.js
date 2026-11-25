function DEV_ITEMS(){return [{label:"VS CODE",sub:"app"},{label:"TERMINAL",sub:"app"},{label:"REPOS",sub:"folder"},{label:"SEARCH",sub:"4 inside",fan:1},{label:"GITHUB",sub:"url"},{label:"SNIP",sub:"screenshot"},{label:"EMAIL SIG",sub:"text"},{label:"MUTE",sub:"system"}];}
// Settings window shell. shell(active, mainHtml) → returns the .main element
function shell(active, html, tag){
  const navs=["Wheels","Triggers","Appearance","Sounds","Clipboard","Gallery","General","License"];
  document.body.insertAdjacentHTML("beforeend",`<div class="label-tag">${tag}</div>
  <div class="sw"><div class="title"><span class="logo"></span>Spoke Settings<span class="wc"><span>—</span><span>▢</span><span>✕</span></span></div>
  <div class="body"><div class="nav">${navs.map(n=>`<a class="${n==active?'on':''}"><i></i>${n}</a>`).join("")}<div class="sp"></div><div class="trial">TRIAL · 5 DAYS LEFT</div></div>
  <div class="main">${html}</div></div></div>`);
  return document.querySelector(".main");
}
