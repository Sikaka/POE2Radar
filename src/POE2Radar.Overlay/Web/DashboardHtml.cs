namespace POE2Radar.Overlay.Web;

/// <summary>
/// Self-contained web dashboard served at <c>GET /</c> by <see cref="ApiServer"/>. One inlined
/// HTML/CSS/JS document — no external assets beyond Google Fonts. The Console tab reads/writes
/// radar/visual settings via <c>/api/settings</c> (the only writes it makes — flags + calibration,
/// never flask/automation); the Entities and Landmarks tabs poll the same-origin read endpoints
/// (<c>/state</c>, <c>/entities</c>, <c>/landmarks</c>).
/// </summary>
internal static class DashboardHtml
{
    public const string Page = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>POE2Radar — Console</title>
<!-- Self-contained: no external fonts/CDNs. Falls back to local system serif/mono fonts. -->
<style>
  :root{
    --bg:#0a0907; --bg2:#100d09; --panel:#15110b; --panel2:#1b1610;
    --line:#3a2f1d; --line-soft:#271f14;
    --ink:#e8dcc2; --ink-dim:#9c8e72; --ink-faint:#6b5f49;
    --gold:#c8a049; --gold-bright:#ecca7e; --gold-deep:#8a6d34;
    --blood:#9c342a; --blood-bright:#d6584a;
    --rare:#f1e36b; --magic:#7f93ff; --unique:#d2641e; --normal:#cdc6b4;
    --good:#79b06a; --poi:#4bb3c4;
    --shadow:0 18px 40px -20px rgba(0,0,0,.9);
  }
  *{box-sizing:border-box}
  html,body{height:100%}
  body{
    margin:0; background:
      radial-gradient(120% 90% at 50% -10%, #1a150d 0%, var(--bg) 55%) fixed,
      var(--bg);
    color:var(--ink);
    font-family:"IBM Plex Mono","Consolas",ui-monospace,monospace;
    font-size:13px; line-height:1.5;
    -webkit-font-smoothing:antialiased;
    overflow:hidden;
  }
  /* grain + vignette atmosphere */
  body::before{
    content:""; position:fixed; inset:0; pointer-events:none; z-index:999;
    background:radial-gradient(120% 120% at 50% 40%, transparent 58%, rgba(0,0,0,.55) 100%);
    mix-blend-mode:multiply;
  }
  body::after{
    content:""; position:fixed; inset:0; pointer-events:none; z-index:998; opacity:.045;
    background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='160' height='160'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='.9' numOctaves='2'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)'/%3E%3C/svg%3E");
  }

  .shell{display:grid; grid-template-rows:auto 1fr; height:100vh}

  /* ── masthead ── */
  header{
    display:flex; align-items:center; gap:20px; padding:14px 26px;
    border-bottom:1px solid var(--line);
    background:linear-gradient(180deg, rgba(30,24,14,.6), transparent);
  }
  .mark{display:flex; align-items:baseline; gap:12px}
  .mark h1{
    font-family:"Cinzel","Georgia",serif; font-weight:700; font-size:22px; margin:0;
    letter-spacing:.14em; color:var(--gold-bright);
    text-shadow:0 1px 0 #000, 0 0 22px rgba(200,160,73,.25);
  }
  .mark .sub{font-size:10px; letter-spacing:.42em; color:var(--ink-faint); text-transform:uppercase}
  .hgap{flex:1}
  .conn{display:flex; align-items:center; gap:9px; font-size:11px; letter-spacing:.1em; color:var(--ink-dim); text-transform:uppercase}
  .dot{width:9px; height:9px; border-radius:50%; background:var(--blood); box-shadow:0 0 0 0 rgba(214,88,74,.5); }
  .conn.live .dot{background:var(--good); animation:pulse 2.2s infinite}
  @keyframes pulse{0%{box-shadow:0 0 0 0 rgba(121,176,106,.5)}70%{box-shadow:0 0 0 7px rgba(121,176,106,0)}100%{box-shadow:0 0 0 0 rgba(121,176,106,0)}}
  .area-chip{
    font-family:"Cinzel","Georgia",serif; letter-spacing:.08em; color:var(--ink);
    border:1px solid var(--line); padding:5px 14px; border-radius:2px;
    background:var(--panel); font-size:13px;
  }
  .area-chip b{color:var(--gold-bright); font-weight:600}

  /* ── body grid ── */
  .body{display:grid; grid-template-columns:300px 1fr; gap:0; min-height:0}
  aside{
    border-right:1px solid var(--line); padding:22px 22px 0;
    overflow-y:auto; background:linear-gradient(180deg, rgba(20,16,10,.5), transparent 220px);
  }
  main{display:grid; grid-template-rows:auto 1fr; min-height:0; min-width:0}

  /* ── vitals ── */
  .vital{margin-bottom:18px}
  .vital .vlabel{display:flex; justify-content:space-between; font-size:10px; letter-spacing:.18em; text-transform:uppercase; color:var(--ink-dim); margin-bottom:6px}
  .vital .vlabel .num{color:var(--ink); font-weight:600}
  .bar{height:9px; border:1px solid var(--line); background:#0c0a07; border-radius:1px; overflow:hidden; position:relative}
  .bar > i{display:block; height:100%; transition:width .35s ease}
  .bar.hp > i{background:linear-gradient(90deg,#6e1f18,var(--blood-bright))}
  .bar.mana > i{background:linear-gradient(90deg,#23306e,var(--magic))}

  .sect{font-family:"Cinzel","Georgia",serif; font-size:12px; letter-spacing:.22em; text-transform:uppercase; color:var(--gold); margin:24px 0 12px; display:flex; align-items:center; gap:10px}
  .sect::after{content:""; flex:1; height:1px; background:linear-gradient(90deg,var(--line),transparent)}

  .kv{display:flex; justify-content:space-between; padding:5px 0; border-bottom:1px dotted var(--line-soft); font-size:12px}
  .kv span:first-child{color:var(--ink-faint); letter-spacing:.04em}
  .kv span:last-child{color:var(--ink); font-weight:500}

  .tally{display:grid; grid-template-columns:1fr 1fr; gap:7px; margin-top:4px}
  .tally .t{border:1px solid var(--line-soft); background:var(--panel); padding:9px 10px; border-radius:2px}
  .tally .t .n{font-size:20px; font-weight:600; color:var(--gold-bright); font-family:"Cinzel","Georgia",serif; line-height:1}
  .tally .t .l{font-size:9px; letter-spacing:.16em; text-transform:uppercase; color:var(--ink-faint); margin-top:4px}

  /* ── tabs ── */
  .tabs{display:flex; gap:2px; padding:14px 26px 0; border-bottom:1px solid var(--line)}
  .tab{
    font-family:"Cinzel","Georgia",serif; font-size:12px; letter-spacing:.16em; text-transform:uppercase;
    color:var(--ink-faint); background:transparent; border:1px solid transparent; border-bottom:none;
    padding:9px 20px; cursor:pointer; border-radius:3px 3px 0 0; position:relative; top:1px;
  }
  .tab:hover{color:var(--ink-dim)}
  .tab.on{color:var(--gold-bright); background:var(--panel); border-color:var(--line); }
  .tab.on::after{content:""; position:absolute; left:0; right:0; bottom:-1px; height:2px; background:var(--panel)}

  .view{overflow:auto; padding:22px 26px; min-height:0}
  .view[hidden]{display:none}

  /* ── controls ── */
  .controls{display:flex; flex-wrap:wrap; gap:8px; align-items:center; margin-bottom:16px}
  .chip{
    font-size:11px; letter-spacing:.06em; color:var(--ink-dim);
    border:1px solid var(--line-soft); background:var(--panel); padding:6px 12px; border-radius:14px; cursor:pointer;
    transition:all .15s;
  }
  .chip:hover{border-color:var(--gold-deep); color:var(--ink)}
  .chip.on{background:var(--gold-deep); border-color:var(--gold); color:#1a140a; font-weight:600}
  input[type=search]{
    font-family:inherit; font-size:12px; color:var(--ink); background:#0c0a07;
    border:1px solid var(--line); border-radius:2px; padding:7px 12px; min-width:200px; flex:1;
  }
  input[type=search]:focus{outline:none; border-color:var(--gold-deep)}
  input[type=search]::placeholder{color:var(--ink-faint)}

  /* ── tables ── */
  table{width:100%; border-collapse:collapse; font-size:12px}
  thead th{
    text-align:left; font-weight:500; font-size:10px; letter-spacing:.14em; text-transform:uppercase;
    color:var(--ink-faint); padding:8px 10px; border-bottom:1px solid var(--line); position:sticky; top:-22px;
    background:var(--bg);
  }
  tbody td{padding:7px 10px; border-bottom:1px solid var(--line-soft); white-space:nowrap}
  tbody tr:hover{background:rgba(200,160,73,.05)}
  .meta{color:var(--ink-faint); font-size:11px; max-width:380px; overflow:hidden; text-overflow:ellipsis}
  .rar-Normal{color:var(--normal)} .rar-Magic{color:var(--magic)} .rar-Rare{color:var(--rare)} .rar-Unique{color:var(--unique)}
  .pill{font-size:9px; letter-spacing:.1em; text-transform:uppercase; padding:2px 7px; border-radius:10px; border:1px solid currentColor}
  .friendly{color:var(--good)} .hostile{color:var(--blood-bright)}
  .num-r{text-align:right; color:var(--ink-dim)}
  .hpbar{width:60px; height:6px; border:1px solid var(--line); border-radius:1px; overflow:hidden; display:inline-block; vertical-align:middle}
  .hpbar > i{display:block; height:100%; background:linear-gradient(90deg,#6e1f18,var(--blood-bright))}

  .lm{display:flex; align-items:center; gap:14px; padding:11px 14px; border:1px solid var(--line-soft); border-radius:3px; margin-bottom:8px; background:var(--panel)}
  .lm:hover{border-color:var(--gold-deep)}
  .lm .name{font-family:"Spectral","Georgia",serif; font-size:15px; color:var(--gold-bright); font-style:italic}
  .lm .path{font-size:10px; color:var(--ink-faint); overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .lm .dist{margin-left:auto; font-family:"Cinzel","Georgia",serif; color:var(--ink); font-size:14px; flex:none}
  .lm .dist small{color:var(--ink-faint); font-size:9px; letter-spacing:.1em; display:block; text-align:right}

  .empty{color:var(--ink-faint); text-align:center; padding:60px 0; font-style:italic; font-family:"Spectral","Georgia",serif; font-size:15px}
  ::-webkit-scrollbar{width:10px;height:10px}
  ::-webkit-scrollbar-thumb{background:var(--line); border-radius:5px; border:2px solid var(--bg)}
  ::-webkit-scrollbar-track{background:transparent}

  /* ── console / control panel ── */
  .panel-grid{display:grid; grid-template-columns:repeat(auto-fill,minmax(330px,1fr)); gap:22px; align-items:start}
  .card{border:1px solid var(--line); border-radius:4px; background:var(--panel); padding:18px 22px; box-shadow:var(--shadow)}
  .card h3{font-family:"Cinzel","Georgia",serif; font-size:12px; letter-spacing:.2em; text-transform:uppercase; color:var(--gold); margin:0 0 8px}
  .card h3 .tag{color:var(--ink-faint); font-size:10px; letter-spacing:.1em}
  .row{display:flex; align-items:center; justify-content:space-between; gap:16px; padding:11px 0; border-bottom:1px dotted var(--line-soft)}
  .row:last-child{border-bottom:none}
  .row .rl{font-size:12px; color:var(--ink); min-width:0}
  .row .rl small{display:block; color:var(--ink-faint); font-size:10px; letter-spacing:.03em; margin-top:3px; line-height:1.4}
  .sw{position:relative; width:44px; height:23px; flex:none; cursor:pointer; display:inline-block}
  .sw input{opacity:0; width:0; height:0; position:absolute}
  .sw .track{position:absolute; inset:0; background:#0c0a07; border:1px solid var(--line); border-radius:12px; transition:.2s}
  .sw .knob{position:absolute; top:3px; left:3px; width:15px; height:15px; border-radius:50%; background:var(--ink-faint); transition:.2s}
  .sw input:checked ~ .track{background:var(--gold-deep); border-color:var(--gold)}
  .sw input:checked ~ .knob{transform:translateX(21px); background:var(--gold-bright); box-shadow:0 0 9px -1px var(--gold-bright)}
  .numin{font-family:inherit; font-size:12px; color:var(--ink); background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:6px 9px; width:96px; text-align:right}
  .numin:focus{outline:none; border-color:var(--gold-deep)}
  .ro{color:var(--gold-bright); font-family:"Cinzel","Georgia",serif; font-size:14px}
  .hint-row{color:var(--ink-faint)!important; font-size:11px!important; font-style:italic}
  .saved{font-size:10px; letter-spacing:.18em; text-transform:uppercase; color:var(--good); opacity:0; transition:opacity .3s}
  .saved.show{opacity:1}

  /* ── dashboard nav list ── */
  .navrow{display:flex; align-items:center; gap:12px; padding:9px 12px; border:1px solid var(--line-soft); border-radius:3px; margin-bottom:6px; background:var(--panel); cursor:pointer}
  .navrow:hover{border-color:var(--gold-deep)}
  .navrow.sel{border-color:var(--gold); background:rgba(200,160,73,.07)}
  .navbtn{width:18px; height:18px; flex:none; border:1px solid var(--ink-faint); border-radius:50%; display:flex; align-items:center; justify-content:center; font-size:11px; color:#120d06; line-height:1}
  .navrow:not(.sel) .navbtn{color:var(--ink-faint)}
  .navname{flex:1; min-width:0; color:var(--ink); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-family:"Spectral","Georgia",serif; font-size:14px}
  .navrow.sel .navname{color:var(--gold-bright)}
  .navtag{font-size:9px; letter-spacing:.12em; text-transform:uppercase; color:var(--ink-faint); border:1px solid var(--line-soft); border-radius:10px; padding:2px 8px; flex:none}
  .navdist{font-family:"Cinzel","Georgia",serif; color:var(--ink-dim); font-size:13px; min-width:48px; text-align:right; flex:none}
</style>
</head>
<body>
<div class="shell">
  <header>
    <div class="mark">
      <h1>POE2RADAR</h1>
    </div>
    <div class="hgap"></div>
    <div class="area-chip" id="areaChip">— <b>·</b></div>
    <div class="conn" id="conn"><span class="dot"></span><span id="connTxt">offline</span></div>
  </header>

  <div class="body">
    <aside>
      <div class="vital">
        <div class="vlabel"><span>Life</span><span class="num" id="hpNum">—</span></div>
        <div class="bar hp"><i id="hpBar" style="width:0"></i></div>
      </div>
      <div class="vital">
        <div class="vlabel"><span>Mana</span><span class="num" id="mpNum">—</span></div>
        <div class="bar mana"><i id="mpBar" style="width:0"></i></div>
      </div>

      <div class="sect">Zone</div>
      <div class="kv"><span>Area code</span><span id="kArea">—</span></div>
      <div class="kv"><span>Area level</span><span id="kAlvl">—</span></div>
      <div class="kv"><span>Map open</span><span id="kMap">—</span></div>
      <div class="kv"><span>Auto-flask</span><span id="kFlask">—</span></div>

      <div class="sect">Census</div>
      <div class="tally">
        <div class="t"><div class="n" id="cEnt">0</div><div class="l">Entities</div></div>
        <div class="t"><div class="n" id="cPoi">0</div><div class="l">Points of Int.</div></div>
        <div class="t"><div class="n" id="cMon">0</div><div class="l">Monsters</div></div>
        <div class="t"><div class="n" id="cLm">0</div><div class="l">Landmarks</div></div>
      </div>
      <div style="height:24px"></div>
    </aside>

    <main>
      <div class="tabs">
        <button class="tab on" data-tab="dashboard">Dashboard</button>
        <button class="tab" data-tab="settings">Settings</button>
      </div>

      <section class="view" data-view="dashboard">
        <div class="controls">
          <input type="search" id="navSearch" placeholder="search entities, landmarks, tiles…" />
          <button class="chip on" id="navAlive">Alive only</button>
          <button class="chip" id="navClear">Clear paths</button>
          <span style="color:var(--ink-faint);font-size:11px" id="navCount"></span>
        </div>
        <div class="controls" id="kindChips">
          <button class="chip on" data-kind="all">All</button>
          <button class="chip" data-kind="landmarks">Landmarks &amp; tiles</button>
          <button class="chip" data-kind="entities">Entities</button>
        </div>
        <div id="navList"></div>
        <div class="empty" id="navEmpty" hidden>Nothing to navigate to here.</div>
      </section>

      <section class="view" data-view="settings" hidden>
        <div class="panel-grid">
          <div class="card">
            <h3>Radar Display</h3>
            <div class="row"><div class="rl">Show monsters<small>enemy dots on the map overlay</small></div>
              <label class="sw"><input type="checkbox" data-set="showMonsters"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Show terrain<small>walkable-terrain bitmap</small></div>
              <label class="sw"><input type="checkbox" data-set="showTerrain"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Hide junk entities<small>suppress cosmetic / FX / daemon dots</small></div>
              <label class="sw"><input type="checkbox" data-set="hideJunk"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Navigation paths<small>draw A&#42; routes to selected landmarks</small></div>
              <label class="sw"><input type="checkbox" data-set="showPath"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Curated landmark names<small>community labels (boss / reward / exits)</small></div>
              <label class="sw"><input type="checkbox" data-set="useCuratedLandmarks"><span class="track"></span><span class="knob"></span></label></div>
          </div>
          <div class="card">
            <h3>Monster HP Bars <span class="tag">&middot; by rarity</span></h3>
            <div class="row"><div class="rl">Normal</div>
              <label class="sw"><input type="checkbox" data-set="hpBarNormal"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl" style="color:var(--magic)">Magic</div>
              <label class="sw"><input type="checkbox" data-set="hpBarMagic"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl" style="color:var(--rare)">Rare</div>
              <label class="sw"><input type="checkbox" data-set="hpBarRare"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl" style="color:var(--unique)">Unique</div>
              <label class="sw"><input type="checkbox" data-set="hpBarUnique"><span class="track"></span><span class="knob"></span></label></div>
          </div>
          <div class="card">
            <h3>Map Calibration</h3>
            <div class="row"><div class="rl">Scale multiplier<small>projection scale of the map overlay</small></div>
              <input class="numin" type="number" step="0.01" data-set="scaleMul"></div>
            <div class="row"><div class="rl">Offset X</div><input class="numin" type="number" step="1" data-set="offX"></div>
            <div class="row"><div class="rl">Offset Y</div><input class="numin" type="number" step="1" data-set="offY"></div>
            <div class="row"><div class="rl hint-row">Adjust here &mdash; changes apply live (no in-game hotkeys).</div></div>
          </div>
          <div class="card">
            <h3>Auto-Flask</h3>
            <div class="row"><div class="rl">Life threshold %<small>tap life flask below this Life %</small></div>
              <input class="numin" type="number" step="1" min="0" max="100" data-set="lifeThresholdPct"></div>
            <div class="row"><div class="rl">Mana threshold %<small>tap mana flask below this Mana %</small></div>
              <input class="numin" type="number" step="1" min="0" max="100" data-set="manaThresholdPct"></div>
            <div class="row"><div class="rl">Life flask key</div>
              <input class="numin keyin" type="text" maxlength="1" data-set="lifeKey"></div>
            <div class="row"><div class="rl">Mana flask key</div>
              <input class="numin keyin" type="text" maxlength="1" data-set="manaKey"></div>
            <div class="row"><div class="rl">Life cooldown<small>min ms between life taps</small></div>
              <input class="numin" type="number" step="100" min="0" data-set="lifeCooldownMs"></div>
            <div class="row"><div class="rl">Mana cooldown<small>min ms between mana taps</small></div>
              <input class="numin" type="number" step="100" min="0" data-set="manaCooldownMs"></div>
            <div class="row"><div class="rl hint-row">F8 toggles auto-flask in-game. Status: <span id="flaskState">&mdash;</span></div></div>
          </div>
        </div>
        <div style="margin-top:18px; height:14px"><span class="saved" id="savedMsg">&#10003; saved to config</span></div>
      </section>

    </main>
  </div>
</div>

<script>
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
// Path palette — must match OverlayRenderer.PathPalette (route color by selection slot).
const PALETTE = ['#33E666','#FF8C1A','#4DB3FF','#FF4DB3','#F2E633','#9966FF','#33FFD9','#FF6666'];

let state=null, entities=[], landmarks=[], selected=new Map(); // id -> color slot
let activeTab='dashboard', kindFilter='all', aliveOnly=true, search='';

/* ── tabs ── */
$$('.tab').forEach(t=>t.onclick=()=>{
  activeTab=t.dataset.tab;
  $$('.tab').forEach(x=>x.classList.toggle('on',x===t));
  $$('.view').forEach(v=>v.hidden = v.dataset.view!==activeTab);
  if(activeTab==='settings') loadSettings();
  pump();
});

/* ── polling ── */
async function getJSON(u){ const r=await fetch(u,{cache:'no-store'}); if(!r.ok) throw 0; return r.json(); }
function setConn(live){ $('#conn').classList.toggle('live',live); $('#connTxt').textContent = live?'live':'offline'; }

async function tick(){
  try{
    state = await getJSON('/state');
    setConn(true);
    renderState();
    if(activeTab==='dashboard'){
      [entities, landmarks] = await Promise.all([getJSON('/entities?limit=2000'), getJSON('/landmarks')]);
      await refreshNav();
    }
    pump();
  }catch(e){ setConn(false); }
}
function pump(){ if(activeTab==='dashboard') renderDashboard(); }

/* ── dashboard: unified, searchable navigation-target list (drives the in-game path) ── */
async function refreshNav(){
  try{ const n=await getJSON('/api/nav'); selected=new Map((n.selected||[]).map(x=>[x.id, x.slot])); }catch(e){}
}
async function navToggle(id){
  if(selected.has(id)) selected.delete(id); else selected.set(id, selected.size); // optimistic
  renderDashboard();
  try{ await fetch('/api/nav',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({toggle:id})}); }catch(e){}
  await refreshNav(); renderDashboard();
}
async function navClearAll(){
  selected.clear(); renderDashboard();
  try{ await fetch('/api/nav',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({clear:true})}); }catch(e){}
  await refreshNav(); renderDashboard();
}
function prettify(m){
  if(!m) return 'Unknown';
  const s=m.split('/').pop().replace(/_/g,' ').replace(/([a-z])([A-Z])/g,'$1 $2').replace(/\s*\d+$/,'').trim();
  return s||m;
}
function navRows(){
  const rows=[];
  for(const l of landmarks) rows.push({id:'t:'+l.path, name:l.curatedName||l.name||'Landmark', kind:'Landmark', tag:'tile', dist:l.dist, key:l.path||''});
  for(const e of entities){
    if(aliveOnly && !e.alive) continue;
    const tag = e.poi ? 'POI' : (e.rarity && e.rarity!=='NonMonster' ? e.rarity : e.category);
    rows.push({id:'e:'+e.id, name:prettify(e.metadata), kind:e.category, tag, dist:e.dist, key:e.metadata||''});
  }
  return rows;
}
function renderDashboard(){
  let rows=navRows();
  if(kindFilter==='landmarks') rows=rows.filter(r=>r.kind==='Landmark');
  else if(kindFilter==='entities') rows=rows.filter(r=>r.kind!=='Landmark');
  if(search) rows=rows.filter(r=>r.name.toLowerCase().includes(search)||r.key.toLowerCase().includes(search));
  rows.sort((a,b)=>{ const sa=selected.has(a.id), sb=selected.has(b.id); if(sa!==sb) return sa?-1:1; return (a.dist||0)-(b.dist||0); });
  const shown=rows.slice(0,400);
  $('#navCount').textContent = rows.length+' targets'+(rows.length>shown.length?' · showing 400':'');
  $('#navEmpty').hidden = rows.length>0;
  $('#navList').innerHTML = shown.map(r=>{
    const sel=selected.has(r.id), col=sel?PALETTE[(selected.get(r.id)||0)%8]:'';
    return `<div class="navrow${sel?' sel':''}" data-id="${(r.id||'').replace(/"/g,'&quot;')}">
      <span class="navbtn" style="${sel?'background:'+col+';border-color:'+col:''}">${sel?'●':'○'}</span>
      <span class="navname">${r.name}</span>
      <span class="navtag">${r.tag}</span>
      <span class="navdist">${r.dist}</span>
    </div>`;
  }).join('');
  $$('#navList .navrow').forEach(el=>el.onclick=()=>navToggle(el.dataset.id));
}
$('#navSearch').oninput=e=>{ search=e.target.value.toLowerCase(); renderDashboard(); };
$('#navAlive').onclick=()=>{ aliveOnly=!aliveOnly; $('#navAlive').classList.toggle('on',aliveOnly); renderDashboard(); };
$('#navClear').onclick=navClearAll;
$$('#kindChips .chip').forEach(c=>c.onclick=()=>{ kindFilter=c.dataset.kind; $$('#kindChips .chip').forEach(x=>x.classList.toggle('on',x===c)); renderDashboard(); });

/* ── settings tab (writes radar/visual + flask via the loopback-gated /api/settings) ── */
async function loadSettings(){
  try{
    const s = await getJSON('/api/settings');
    $$('[data-set]').forEach(el=>{
      const k=el.dataset.set;
      if(el.type==='checkbox') el.checked=!!s[k];
      else if(el.classList.contains('keyin')) el.value=vkToChar(s[k]);
      else if(s[k]!==undefined) el.value=s[k];
    });
  }catch(e){}
}
async function saveSetting(key,val){
  try{
    await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({[key]:val})});
    const m=$('#savedMsg'); m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1100);
  }catch(e){}
}
function wireSettings(){
  $$('[data-set]').forEach(el=>{
    const k=el.dataset.set;
    if(el.type==='checkbox') el.onchange=()=>saveSetting(k,el.checked);
    else if(el.classList.contains('keyin')) el.onchange=()=>{ const vk=charToVk(el.value); if(vk) saveSetting(k,vk); el.value=vkToChar(vk); };
    else el.onchange=()=>{ const v=parseFloat(el.value); if(!isNaN(v)) saveSetting(k,v); };
  });
}
// Flask key inputs accept a single character ('1'-'9', letters) → Win32 VK (== ASCII of uppercase).
const charToVk = s => { const c=(s||'').trim().toUpperCase().charCodeAt(0); return isNaN(c)?0:c; };
const vkToChar = v => v ? String.fromCharCode(v) : '';

/* ── left rail ── */
function renderState(){
  const s=state; if(!s) return;
  const hp=Math.max(0,Math.min(100,s.hpPct||0)), mp=Math.max(0,Math.min(100,s.manaPct||0));
  $('#hpBar').style.width=hp+'%'; $('#mpBar').style.width=mp+'%';
  $('#hpNum').textContent=hp.toFixed(0)+'%'; $('#mpNum').textContent=mp.toFixed(0)+'%';
  $('#kArea').textContent=s.areaCode||'—';
  $('#kAlvl').textContent=s.areaLevel||'—';
  $('#kMap').textContent=s.mapVisible?'yes':'no';
  $('#kFlask').textContent=(s.autoFlask?'on':'off')+(s.flask?' · '+s.flask:'');
  const fs=$('#flaskState'); if(fs) fs.textContent=(s.autoFlask?'ON':'OFF')+(s.flask?' · '+s.flask:'');
  $('#cEnt').textContent=s.entityCount||0;
  $('#cPoi').textContent=s.poiCount||0;
  $('#cMon').textContent=(s.counts&&s.counts.Monster)||0;
  $('#cLm').textContent=s.landmarkCount||0;
  $('#areaChip').innerHTML = (s.areaCode? s.areaCode : '—') + ' <b>·</b> ' + (s.inGame?'in game':'town/menu');
}

wireSettings(); loadSettings();
tick(); setInterval(tick, 1000);
</script>
</body>
</html>
""";
}
