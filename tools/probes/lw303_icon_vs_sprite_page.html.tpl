<title>Does the Sprite Match the Icon?</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500&family=IBM+Plex+Sans:wght@400;500;600&family=IBM+Plex+Serif:ital@0;1&display=swap">
<style>
:root{
  --ground:#e8e9eb; --surface:#f7f8f9; --raise:#ffffff;
  --ink:#171a1e; --muted:#666d77; --line:#d3d7dc;
  --accent:#3d5f80; --warn:#8b6410; --crit:#973a2c;
  --check-a:#dcdee1; --check-b:#e9ebed;
  --sans:"IBM Plex Sans",system-ui,-apple-system,Segoe UI,sans-serif;
  --mono:"IBM Plex Mono",ui-monospace,SFMono-Regular,Consolas,monospace;
  --serif:"IBM Plex Serif",Georgia,serif;
}
@media (prefers-color-scheme:dark){
  :root:not([data-theme="light"]){
    --ground:#131619; --surface:#1b1f23; --raise:#22272c;
    --ink:#e4e7ea; --muted:#8e959e; --line:#2b3138;
    --accent:#86aacb; --warn:#c79a3b; --crit:#c9705f;
    --check-a:#191d21; --check-b:#20252a;
  }
}
:root[data-theme="dark"]{
  --ground:#131619; --surface:#1b1f23; --raise:#22272c;
  --ink:#e4e7ea; --muted:#8e959e; --line:#2b3138;
  --accent:#86aacb; --warn:#c79a3b; --crit:#c9705f;
  --check-a:#191d21; --check-b:#20252a;
}
*{box-sizing:border-box}
body{margin:0;background:var(--ground);color:var(--ink);font-family:var(--sans);
     font-size:15px;line-height:1.55;-webkit-font-smoothing:antialiased}
.wrap{max-width:1240px;margin:0 auto;padding:44px 24px 96px}
h1{font-size:clamp(28px,4vw,40px);font-weight:600;letter-spacing:-.02em;margin:0;text-wrap:balance}
.eyebrow{font-family:var(--mono);font-size:11px;letter-spacing:.16em;text-transform:uppercase;
         color:var(--accent);margin:0 0 10px}
.lede{font-family:var(--serif);font-size:17px;line-height:1.62;color:var(--muted);
      max-width:64ch;margin:16px 0 0}
.lede b{color:var(--ink);font-weight:400;font-style:italic}

.stats{display:flex;flex-wrap:wrap;gap:1px;margin:34px 0 0;background:var(--line);
       border:1px solid var(--line);border-radius:3px;overflow:hidden}
.stat{flex:1 1 150px;background:var(--surface);padding:14px 16px}
.stat .n{font-family:var(--mono);font-size:24px;font-weight:500;letter-spacing:-.02em;
         font-variant-numeric:tabular-nums;display:block}
.stat .k{font-family:var(--mono);font-size:10.5px;letter-spacing:.12em;text-transform:uppercase;
         color:var(--muted)}
.stat.flag .n{color:var(--warn)}

.bar{position:sticky;top:0;z-index:20;margin:28px 0 0;padding:12px 0;background:var(--ground);
     border-bottom:1px solid var(--line);display:flex;flex-wrap:wrap;gap:10px;align-items:center}
input[type=search],select{font-family:var(--mono);font-size:12.5px;color:var(--ink);
  background:var(--surface);border:1px solid var(--line);border-radius:3px;padding:7px 10px}
input[type=search]{min-width:210px}
input[type=search]:focus-visible,select:focus-visible,button:focus-visible{outline:2px solid var(--accent);outline-offset:1px}
.chips{display:flex;flex-wrap:wrap;gap:5px}
.chip{font-family:var(--mono);font-size:11px;letter-spacing:.04em;padding:5px 9px;border-radius:2px;
      border:1px solid var(--line);background:var(--surface);color:var(--muted);cursor:pointer}
.chip[aria-pressed="true"]{background:var(--accent);border-color:var(--accent);color:var(--ground)}
.chip .c{opacity:.6;margin-left:5px;font-variant-numeric:tabular-nums}
.spacer{flex:1 1 auto}

.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:1px;
      background:var(--line);border:1px solid var(--line);border-radius:3px;margin-top:26px;overflow:hidden}
.card{background:var(--surface);padding:14px 15px 15px;display:flex;flex-direction:column;gap:10px}
.card.hide{display:none}
.head{grid-column:1/-1;background:var(--ground);padding:18px 15px 8px}
.head p{margin:0}
.name{display:flex;align-items:baseline;gap:8px}
.name h3{margin:0;font-size:16px;font-weight:600;letter-spacing:-.01em}
.tier{font-family:var(--mono);font-size:10px;letter-spacing:.1em;color:var(--muted);
      border:1px solid var(--line);border-radius:2px;padding:1px 5px;white-space:nowrap}
.meta{font-family:var(--mono);font-size:11px;color:var(--muted);display:flex;flex-wrap:wrap;gap:4px 10px;
      font-variant-numeric:tabular-nums}
.meta .hue{display:inline-flex;align-items:center;gap:5px}
.dot{width:9px;height:9px;border-radius:50%;border:1px solid rgba(128,128,128,.45)}
.badge{font-family:var(--mono);font-size:10px;letter-spacing:.06em;padding:1px 5px;border-radius:2px;
       border:1px solid currentColor;color:var(--warn)}

.plates{display:flex;gap:9px;align-items:flex-end}
.plate{display:flex;flex-direction:column;gap:5px}
.frame{height:96px;display:grid;place-items:center;border:1px solid var(--line);border-radius:2px;
  background-color:var(--check-b);
  background-image:linear-gradient(45deg,var(--check-a) 25%,transparent 25%,transparent 75%,var(--check-a) 75%),
                   linear-gradient(45deg,var(--check-a) 25%,transparent 25%,transparent 75%,var(--check-a) 75%);
  background-size:12px 12px;background-position:0 0,6px 6px}
.frame img{image-rendering:pixelated;display:block}
.frame.icon{width:96px}
.frame.icon img{width:88px;height:88px}
.frame.spr{width:148px}
.frame.spr img{width:144px}
.frame.none{width:148px;font-family:var(--mono);font-size:10px;line-height:1.4;color:var(--warn);
            padding:8px;text-align:center;border-style:dashed}
.cap{font-family:var(--mono);font-size:9.5px;letter-spacing:.11em;text-transform:uppercase;color:var(--muted)}
body.novan .plate.van{display:none}

.ramp{display:flex;height:15px;border:1px solid var(--line);border-radius:2px;overflow:hidden}
.ramp i{flex:1}
.ramp i.tr{background-color:var(--check-b);
  background-image:linear-gradient(45deg,var(--check-a) 25%,transparent 25%,transparent 75%,var(--check-a) 75%);
  background-size:8px 8px}

.section{margin-top:56px;scroll-margin-top:160px}
.section h2{font-size:20px;font-weight:600;letter-spacing:-.01em;margin:0 0 6px}
.section p{color:var(--muted);max-width:66ch;margin:0 0 20px}
.press{width:100%;border-collapse:collapse;font-size:13px}
.press th{font-family:var(--mono);font-size:10px;letter-spacing:.12em;text-transform:uppercase;
          color:var(--muted);text-align:left;font-weight:400;padding:0 12px 8px 0;border-bottom:1px solid var(--line)}
.press td{padding:9px 12px 9px 0;border-bottom:1px solid var(--line);vertical-align:top}
.press td.n{font-family:var(--mono);font-variant-numeric:tabular-nums;white-space:nowrap}
.press td.who{color:var(--muted);font-size:12px}
.wheel{display:flex;gap:2px;flex-wrap:wrap}
.wheel i{width:11px;height:11px;border-radius:50%;border:1px solid rgba(128,128,128,.4)}
.gap-hot{color:var(--crit)}
.scroll{overflow-x:auto}
.foot{margin-top:48px;padding-top:18px;border-top:1px solid var(--line);
      font-family:var(--mono);font-size:11px;color:var(--muted);line-height:1.7}
@media (prefers-reduced-motion:no-preference){.card{transition:background .12s ease}}
.card:hover{background:var(--raise)}
</style>

<div class="wrap">
  <p class="eyebrow">LW-303 &middot; weapon colour verification</p>
  <h1>Does the sprite match the icon?</h1>
  <p class="lede">Every equippable weapon, with the icon a player sees in the menu beside the
  battle drawing it will actually swing, recoloured by the transform. <b>Left is the promise,
  middle is the delivery, right is where it started.</b> Nothing here is typed in: every palette is
  recomputed from the item data and the pristine sprite sheet each time this page is built.</p>

  <div class="stats" id="stats"></div>

  <div class="bar">
    <input type="search" id="q" placeholder="find a weapon" aria-label="Find a weapon">
    <select id="sort" aria-label="Sort order">
      <option value="cat">Group by category</option>
      <option value="pal">Group by palette</option>
      <option value="hue">Order by hue</option>
      <option value="drift">Worst tint drift first</option>
    </select>
    <div class="chips" id="chips"></div>
    <span class="spacer"></span>
    <button class="chip" id="van" aria-pressed="true">Vanilla column</button>
  </div>

  <div class="grid" id="grid"></div>

  <section class="section">
    <h2>Where the colours collide</h2>
    <p>127 weapons share 13 palettes, so two weapons on the same palette cannot both wear their own
    colour at the same instant. A pair clashes when the two want hues more than 20 degrees apart,
    which is roughly where the difference stops reading as shading and starts reading as the wrong
    weapon. Every palette here is contested; what separates them is how badly.</p>
    <div class="scroll"><table class="press" id="press"></table></div>
  </section>

  <p class="foot" id="foot"></p>
</div>

<script>
const DATA = /*PAYLOAD*/null;
const grid = document.getElementById('grid');
const esc = s => String(s).replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const hueCss = d => 'hsl(' + d + ' 62% 50%)';

function store(k, v){ try{ if(v === undefined) return localStorage.getItem(k); localStorage.setItem(k, v); }catch(e){} }

const cats = [...new Set(DATA.weapons.map(w => w.cat))].sort();
const pals = [...new Set(DATA.weapons.map(w => w.pal))];
const drifted = DATA.weapons.filter(w => w.drift !== null && w.drift > 25);
const noSprite = DATA.weapons.filter(w => w.tile === null);

document.getElementById('stats').innerHTML = [
  [DATA.weapons.length, 'weapons coloured', ''],
  [cats.length, 'categories', ''],
  [pals.length, 'shared palettes', ''],
  [drifted.length, 'icons off their authored tint', 'flag'],
  [noSprite.length, 'without an identified sprite', noSprite.length ? 'flag' : ''],
].map(a => '<div class="stat ' + a[2] + '"><span class="n">' + a[0] + '</span><span class="k">' + a[1] + '</span></div>').join('');

const active = new Set();
document.getElementById('chips').innerHTML = cats.map(c =>
  '<button class="chip" data-cat="' + esc(c) + '" aria-pressed="false">' + esc(c) +
  '<span class="c">' + DATA.weapons.filter(w => w.cat === c).length + '</span></button>').join('');

function cardHtml(w){
  const drift = (w.drift !== null && w.drift > 25)
    ? '<span class="badge" title="the icon renders at ' + w.hue + ' degrees but was authored ' + w.authored + '">drift ' + w.drift + '&deg;</span>'
    : '';
  const battle = w.tile === null
    ? '<div class="frame spr none">' + esc(w.why || 'no sprite identified') + '</div>'
    : '<div class="frame spr"><img src="' + w.spr + '" alt="' + esc(w.name) + ' battle sprite, recoloured"></div>';
  const van = w.tile === null ? '' :
    '<div class="plate van"><div class="frame spr"><img src="' + w.van + '" alt="the same drawing in vanilla colours"></div>' +
    '<span class="cap">vanilla</span></div>';
  const ramp = w.sw.map(c => c
    ? '<i style="background:' + c + '" title="' + c + '"></i>'
    : '<i class="tr" title="slot 0, transparent"></i>').join('');
  const tile = w.tile === null ? 'no tile' : 'tile #' + w.tile + ' &middot; ' + w.dim;
  return '<article class="card" data-cat="' + esc(w.cat) + '" data-name="' + esc(w.name.toLowerCase()) + '">' +
    '<div class="name"><h3>' + esc(w.name) + '</h3><span class="tier">tier ' + (w.tier === null ? '?' : w.tier) + '</span></div>' +
    '<div class="meta"><span>' + esc(w.cat) + '</span><span>pal ' + w.pal + '</span><span>' + tile + '</span>' +
      '<span class="hue"><i class="dot" style="background:' + hueCss(w.hue) + '"></i>' + w.hue + '&deg; ' + esc(w.mode) + '</span>' +
      drift + '</div>' +
    '<div class="plates">' +
      '<div class="plate"><div class="frame icon">' +
        (w.icon ? '<img src="' + w.icon + '" alt="' + esc(w.name) + ' menu icon">' : '') +
      '</div><span class="cap">icon</span></div>' +
      '<div class="plate">' + battle + '<span class="cap">battle</span></div>' +
      van +
    '</div>' +
    '<div class="ramp" title="the 16 palette codes that reach the game">' + ramp + '</div>' +
  '</article>';
}

function heading(text){
  return '<div class="card head"><p class="eyebrow">' + esc(text) + '</p></div>';
}

function render(){
  const mode = document.getElementById('sort').value;
  const list = DATA.weapons.slice();
  let html = '';
  if(mode === 'cat' || mode === 'pal'){
    const key = w => mode === 'cat' ? w.cat : 'palette ' + w.pal;
    list.sort((a, b) => mode === 'cat'
      ? (a.cat.localeCompare(b.cat) || (a.tier || 0) - (b.tier || 0) || a.id - b.id)
      : (a.pal - b.pal || a.hue - b.hue));
    let cur = null;
    for(const w of list){
      if(key(w) !== cur){ cur = key(w); html += heading(cur); }
      html += cardHtml(w);
    }
  } else {
    list.sort((a, b) => mode === 'hue' ? a.hue - b.hue : (b.drift === null ? -1 : b.drift) - (a.drift === null ? -1 : a.drift));
    html = list.map(cardHtml).join('');
  }
  grid.innerHTML = html;
  filter();
}

function filter(){
  const q = document.getElementById('q').value.trim().toLowerCase();
  for(const el of grid.querySelectorAll('.card[data-name]')){
    const okCat = !active.size || active.has(el.dataset.cat);
    const okQ = !q || el.dataset.name.includes(q) || el.dataset.cat.toLowerCase().includes(q);
    el.classList.toggle('hide', !(okCat && okQ));
  }
  // a group heading with nothing left under it is a lie about what is on screen
  let heading = null, kept = 0;
  for(const el of grid.children){
    if(el.classList.contains('head')){
      if(heading) heading.classList.toggle('hide', kept === 0);
      heading = el; kept = 0;
    } else if(!el.classList.contains('hide')){
      kept++;
    }
  }
  if(heading) heading.classList.toggle('hide', kept === 0);
}

document.getElementById('chips').addEventListener('click', e => {
  const b = e.target.closest('.chip');
  if(!b) return;
  const c = b.dataset.cat;
  if(active.has(c)) active.delete(c); else active.add(c);
  b.setAttribute('aria-pressed', active.has(c));
  filter();
});
document.getElementById('q').addEventListener('input', filter);
document.getElementById('sort').addEventListener('change', e => { store('lw303.sort', e.target.value); render(); });

const vanBtn = document.getElementById('van');
function setVan(on){
  document.body.classList.toggle('novan', !on);
  vanBtn.setAttribute('aria-pressed', String(on));
  store('lw303.van', on ? '1' : '0');
}
vanBtn.addEventListener('click', () => setVan(document.body.classList.contains('novan')));
setVan(store('lw303.van') !== '0');
const savedSort = store('lw303.sort');
if(savedSort) document.getElementById('sort').value = savedSort;

document.getElementById('press').innerHTML =
  '<thead><tr><th>Palette</th><th>Tenants</th><th>Clashing pairs</th><th>Widest gap</th><th>Hues wanted</th><th>Sharing it with</th></tr></thead><tbody>' +
  DATA.pressure.map(p => '<tr>' +
    '<td class="n">' + p.pal + '</td>' +
    '<td class="n">' + p.n + '</td>' +
    '<td class="n ' + (p.share > 50 ? 'gap-hot' : '') + '">' + p.share + '% <span style="opacity:.6">' + p.clashing + ' of ' + p.pairs + '</span></td>' +
    '<td class="n">' + p.worst + '&deg;</td>' +
    '<td><span class="wheel">' + p.hues.map(h => '<i style="background:' + hueCss(h) + '" title="' + h + ' degrees"></i>').join('') + '</span></td>' +
    '<td class="who">' + esc(p.names.join(', ')) + '</td>' +
  '</tr>').join('') + '</tbody>';

document.getElementById('foot').textContent =
  'Built from data/items.json and the pristine unit/battle_wep_spr.bin by tools/probes/lw303_icon_vs_sprite_page.py. ' +
  'Sprite identity comes from tools/probes/lw301_sprite_labels.json, the owner identification of 2026-08-21. ' +
  'Icons are the shipped 48px renders, upscaled nearest-neighbour, exactly as the game upscales this art.';

render();
</script>
