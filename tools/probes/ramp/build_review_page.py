"""Build the ramp-engine review page: N sections (shields, helms, weapon families),
each with 1x strips on the game's measured white ground and click-to-zoom."""
import base64, json, os

SCRATCH = os.path.dirname(os.path.abspath(__file__))

def b64(name):
    return base64.b64encode(open(os.path.join(SCRATCH, name), "rb").read()).decode()

sections = []
meta = json.load(open(os.path.join(SCRATCH, "proto_metrics_shield_.json")))
sections.append({"key": "shield", "title": "Shields", "ids": meta["ids"], "names": meta["names"],
                 "small": "proto_strip_small.png", "card": "proto_strip_card.png"})
helm_meta = json.load(open(os.path.join(SCRATCH, "proto_metrics_helm_.json")))
sections.append({"key": "helm", "title": "Helms", "ids": helm_meta["ids"], "names": helm_meta["names"],
                 "small": "proto_strip_helm_small.png", "card": "proto_strip_helm_card.png"})
wm_path = os.path.join(SCRATCH, "weapon_manifest.json")
if os.path.exists(wm_path):
    for s in json.load(open(wm_path))["sections"]:
        sections.append({"key": "w" + s["cat"], "title": s["cat"], "ids": s["ids"],
                         "names": s["names"], "small": s["small"], "card": s["card"]})

metas = {s["key"]: {"ids": s["ids"], "names": s["names"]} for s in sections}

section_html = ""
for s in sections:
    for surf, label in (("small", "list icons 48px"), ("card", "equip cards 100px")):
        sid = f"{s['key']}-{surf}"
        section_html += f'''
  <h2>{s["title"]}: {label}</h2>
  <div class="rowlabels">
    <div class="labels" id="labels-{sid}">
      <div class="van">Vanilla</div><div>Shipped</div><div class="pro">Prototype</div><div>Proto + Glow</div>
    </div>
    <div class="striphost"><img id="strip-{sid}" alt="{s['title']} {label}"></div>
  </div>
  <div class="zoom" id="zoom-{sid}" hidden></div>'''

wire_js = "\n".join(
    f'  wire("{s["key"]}-{surf}", "{s["key"]}", "__IMG_{s["key"]}_{surf}__");'
    for s in sections for surf in ("small", "card"))

html = """<title>Ramp Engine Review</title>
<style>
  :root {
    --bg: #14161d; --panel: #1c1f2a; --line: #2c3040; --ink: #d9d3c3;
    --dim: #8d92a5; --gold: #c9a45c; --pass: #8fbf8a;
  }
  body { background: var(--bg); color: var(--ink); margin: 0;
         font: 15px/1.55 system-ui, "Segoe UI", sans-serif; }
  .wrap { max-width: 1720px; margin: 0 auto; padding: 28px 24px 64px; }
  h1 { font: 600 30px/1.2 Georgia, "Times New Roman", serif; margin: 0 0 4px;
       color: var(--gold); text-wrap: balance; }
  .sub { color: var(--dim); margin: 0 0 26px; max-width: 68ch; }
  h2 { font: 600 19px/1.3 Georgia, serif; color: var(--ink); margin: 34px 0 6px; }
  .rowlabels { display: grid; grid-template-columns: 92px 1fr; gap: 0 10px; align-items: start; }
  .labels { display: flex; flex-direction: column; font-size: 12px; color: var(--dim);
            text-transform: uppercase; letter-spacing: 0.08em; }
  .labels div { display: flex; align-items: center; }
  .labels .van { color: var(--gold); }
  .labels .pro { color: var(--pass); }
  .striphost { overflow-x: auto; border: 1px solid var(--line); border-radius: 6px;
               background: #ffffff; padding: 0; }
  .striphost img { display: block; image-rendering: pixelated; cursor: crosshair; }
  .zoom { display: grid; grid-template-columns: repeat(4, minmax(120px, 1fr)); gap: 14px;
          background: var(--panel); border: 1px solid var(--line); border-radius: 6px;
          padding: 16px; margin-top: 12px; }
  .zoom figure { margin: 0; text-align: center; }
  .zoom canvas { image-rendering: pixelated; width: 100%; max-width: 300px;
                 border: 1px solid var(--line); border-radius: 4px; background: #ffffff; }
  .zoom figcaption { font-size: 12px; color: var(--dim); margin-top: 6px;
                     text-transform: uppercase; letter-spacing: 0.08em; }
  .zoomname { grid-column: 1 / -1; font: 600 16px Georgia, serif; color: var(--gold); }
</style>
<div class="wrap">
  <h1>Ramp Engine Review</h1>
  <p class="sub">Every recoloured icon at exact game size on the game's measured menu
  white, vanilla always in the row. Rows: <b>vanilla</b>, <b>shipped</b> (the rejected
  rework), <b>prototype</b> (ramp engine; weapon colours drawn from the vanilla palette
  bank, unique within each set), <b>proto + glow</b> (the chosen treatment,
  contrast-proven rims). Click any icon to zoom all four.</p>
""" + section_html + """
</div>
<script>
  const METAS = __METAS__;
  const PAD = 2, LABEL_H = 14, ROWS = 4;
  function wire(sid, key, b64data) {
    const img = document.getElementById('strip-' + sid);
    if (!img) return;
    img.src = 'data:image/png;base64,' + b64data;
    img.addEventListener('load', () => {
      const N = METAS[key].ids.length;
      const tileW = (img.naturalWidth - PAD) / N - PAD;
      const tileH = (img.naturalHeight - LABEL_H - PAD) / ROWS - PAD;
      document.querySelectorAll('#labels-' + sid + ' div').forEach(d => {
        d.style.height = tileH + 'px'; d.style.marginBottom = PAD + 'px';
      });
      document.getElementById('labels-' + sid).style.paddingTop = (LABEL_H + PAD) + 'px';
      img.addEventListener('click', ev => {
        const r = img.getBoundingClientRect();
        const col = Math.min(N - 1, Math.max(0, Math.floor(
          (ev.clientX - r.left) * (img.naturalWidth / r.width) / (tileW + PAD))));
        const host = document.getElementById('zoom-' + sid);
        host.hidden = false;
        host.innerHTML = '<div class="zoomname">' + METAS[key].names[col] + '</div>';
        ['Vanilla', 'Shipped', 'Prototype', 'Proto + Glow'].forEach((label, ri) => {
          const fig = document.createElement('figure');
          const cv = document.createElement('canvas');
          cv.width = tileW; cv.height = tileH;
          cv.getContext('2d').drawImage(img,
            PAD + col * (tileW + PAD), LABEL_H + PAD + ri * (tileH + PAD),
            tileW, tileH, 0, 0, tileW, tileH);
          const cap = document.createElement('figcaption');
          cap.textContent = label;
          fig.appendChild(cv); fig.appendChild(cap); host.appendChild(fig);
        });
      });
    });
  }
__WIRE__
</script>
"""
html = html.replace("__METAS__", json.dumps(metas)).replace("__WIRE__", wire_js)
for s in sections:
    for surf in ("small", "card"):
        html = html.replace(f"__IMG_{s['key']}_{surf}__", b64(s[surf]))
out = os.path.join(SCRATCH, "shield_ramp_review.html")
open(out, "w", encoding="utf-8").write(html)
print(out, f"{len(html)/1e6:.1f} MB, {len(sections)} sections")
