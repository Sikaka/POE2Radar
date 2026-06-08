namespace POE2Radar.Research;

/// <summary>The single-page DevTree explorer UI, served at "/" by <see cref="DevTreeServer"/>.
/// Vanilla JS + fetch; no build step, no external assets. Lazy-loads memory/UI nodes on expand.</summary>
internal static class DevTreeHtml
{
    public const string Page = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>POE2 DevTree</title>
<style>
  :root { --bg:#0d1117; --panel:#161b22; --line:#30363d; --fg:#c9d1d9; --mut:#8b949e;
          --acc:#58a6ff; --ptr:#79c0ff; --flt:#7ee787; --int:#d2a8ff; --str:#ffa657; --warn:#f85149; }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--fg); font:13px/1.5 Consolas,Menlo,monospace; }
  header { padding:8px 12px; background:var(--panel); border-bottom:1px solid var(--line); display:flex; gap:12px; align-items:center; }
  header b { color:var(--acc); }
  header .mut { color:var(--mut); font-size:12px; }
  .tabs { display:flex; gap:4px; padding:6px 12px 0; background:var(--panel); }
  .tabs button { background:transparent; color:var(--mut); border:1px solid transparent; border-bottom:none;
                 padding:6px 14px; cursor:pointer; border-radius:6px 6px 0 0; font:inherit; }
  .tabs button.on { color:var(--fg); background:var(--bg); border-color:var(--line); }
  .pane { display:none; padding:12px; }
  .pane.on { display:block; }
  .bar { display:flex; gap:6px; align-items:center; flex-wrap:wrap; margin-bottom:10px; }
  input,select,button.act { background:#0b0f14; color:var(--fg); border:1px solid var(--line); border-radius:6px; padding:5px 8px; font:inherit; }
  button.act { cursor:pointer; }
  button.act:hover { border-color:var(--acc); }
  .root { background:#0b0f14; border:1px solid var(--line); color:var(--ptr); border-radius:6px; padding:4px 9px; cursor:pointer; font:inherit; }
  .root:hover { border-color:var(--acc); }
  .root small { color:var(--mut); }
  table { border-collapse:collapse; width:100%; }
  td,th { padding:2px 8px; border-bottom:1px solid #21262d; white-space:nowrap; text-align:left; vertical-align:top; }
  th { color:var(--mut); font-weight:normal; position:sticky; top:0; background:var(--bg); }
  .off { color:var(--mut); }
  .hex { color:var(--mut); }
  .ptr { color:var(--ptr); cursor:pointer; }
  .ptr:hover { text-decoration:underline; }
  .flt { color:var(--flt); }
  .int { color:var(--int); }
  .str { color:var(--str); }
  .vec { color:#ffd479; }
  .asc { color:var(--mut); }
  .tw { color:var(--mut); cursor:pointer; user-select:none; display:inline-block; width:14px; }
  .node { margin-left:0; }
  .kids { margin-left:16px; border-left:1px solid var(--line); padding-left:8px; }
  .uirow { padding:1px 0; }
  .vis { color:var(--flt); } .invis { color:var(--mut); }
  .err { color:var(--warn); }
  .hint { color:var(--mut); font-size:12px; }
  .mut { color:var(--mut); }
  a.lnk { color:var(--acc); cursor:pointer; }
  .card { border:1px solid var(--line); border-radius:8px; padding:10px 12px; background:var(--panel); margin-top:8px; }
  .crumb { font-size:12px; margin-bottom:8px; padding-bottom:6px; border-bottom:1px solid var(--line); white-space:normal; }
  .focus b { font-size:14px; }
  .focus .act, .focus .lnk { margin-left:10px; }
  .uin { cursor:pointer; }
</style>
</head>
<body>
<header><b>POE2 DevTree</b><span class="mut">read-only memory explorer · click pointers to descend</span>
  <span id="status" class="mut"></span></header>
<div class="tabs">
  <button data-tab="mem" class="on">Memory</button>
  <button data-tab="ui">UI Tree</button>
  <button data-tab="ent">Entities</button>
  <button data-tab="search">Search</button>
</div>

<div id="mem" class="pane on">
  <div class="bar" id="roots"></div>
  <div class="bar">
    <input id="addr" placeholder="0x... address" size="20">
    <select id="len"><option>0x100</option><option selected>0x200</option><option>0x400</option><option>0x800</option><option>0x1000</option></select>
    <button class="act" onclick="goMem()">Go</button>
    <span class="hint">8-byte slots · ptr=blue (click to expand) · float=green · int=purple · str=orange · vec=yellow</span>
  </div>
  <div id="memtree"></div>
</div>

<div id="ui" class="pane">
  <div class="bar">
    <button class="act" onclick="uiSnapshot()">① Snapshot tree</button>
    <button class="act" onclick="uiDiff()">② Diff vs snapshot</button>
    <input id="uidifffilter" placeholder="filter diff (addr/path/flags)" size="24" oninput="paintUiDiff()">
    <span id="uidiffstatus" class="hint">Snapshot → (optionally act in game) → Diff. Click any element to explore its parent / children / values.</span>
  </div>
  <div id="uidiff"></div>
  <div id="uiexplore"></div>
</div>

<div id="ent" class="pane">
  <div class="bar"><button class="act" onclick="loadEnt()">Load awake entities</button>
    <input id="entfilter" placeholder="filter metadata" size="24" oninput="renderEnt()">
    <span id="entcount" class="hint"></span></div>
  <div id="entlist"></div>
</div>

<div id="search" class="pane">
  <div class="bar">
    <select id="stype"><option value="int">int (4b)</option><option value="float">float</option><option value="str">string (utf-16)</option><option value="ptr">pointer (8b)</option></select>
    <input id="sval" placeholder="value" size="20" onkeydown="if(event.key==='Enter')goSearch()">
    <button class="act" onclick="goSearch()">Search</button>
    <span id="sinfo" class="hint">scans private regions; capped at 200 hits</span>
  </div>
  <div id="sresults"></div>
</div>

<script>
const $ = s => document.querySelector(s);
const status = m => $('#status').textContent = m;
async function api(p){ const r = await fetch(p); if(!r.ok){ const e=await r.json().catch(()=>({error:r.status})); throw new Error(e.error||r.status);} return r.json(); }

// ── tabs ──
document.querySelectorAll('.tabs button').forEach(b=>b.onclick=()=>{
  document.querySelectorAll('.tabs button').forEach(x=>x.classList.toggle('on',x===b));
  document.querySelectorAll('.pane').forEach(p=>p.classList.toggle('on',p.id===b.dataset.tab));
});

// ── roots ──
async function loadRoots(){
  try{
    const rs = await api('/api/roots');
    $('#roots').innerHTML='';
    rs.forEach(r=>{
      const btn=document.createElement('button'); btn.className='root';
      btn.innerHTML=`${r.name} <small>${r.hex}</small>`;
      btn.title=r.note;
      btn.onclick=()=>{ if(r.hex!=='0x0') openMem(r.hex); };
      $('#roots').appendChild(btn);
    });
    status('chain resolved');
  }catch(e){ status('roots: '+e.message); }
}

// ── memory slot tree ──
function openMem(hex){
  document.querySelector('.tabs button[data-tab=mem]').click();
  $('#addr').value=hex; goMem();
}
function goMem(){
  const addr=$('#addr').value.trim(); if(!addr) return;
  $('#memtree').innerHTML=''; renderMem($('#memtree'), addr, $('#len').value);
}
async function renderMem(host, addr, len){
  host.innerHTML='<span class="hint">loading '+addr+'…</span>';
  let d; try{ d=await api(`/api/mem?addr=${addr}&len=${len||'0x200'}`);}catch(e){ host.innerHTML='<span class="err">'+e.message+'</span>'; return; }
  const rows = d.rows.map(s=>{
    const tw = s.ptrReadable ? `<span class="tw" data-ptr="${s.ptr}">▸</span>` : '<span class="tw"></span>';
    const ptr = s.ptr ? `<span class="ptr" data-ptr="${s.ptr}">${s.ptr}</span>` : '';
    const fl = (Number.isFinite(s.f0)&&Math.abs(s.f0)>1e-6&&Math.abs(s.f0)<1e9)?`<span class="flt">${s.f0.toFixed(3)}</span>`:'';
    const fl1= (Number.isFinite(s.f1)&&Math.abs(s.f1)>1e-6&&Math.abs(s.f1)<1e9)?` <span class="flt">${s.f1.toFixed(3)}</span>`:'';
    const ints = `<span class="int">${s.i32Lo}</span>${s.i32Hi?(' '+'<span class="int">'+s.i32Hi+'</span>'):''}`;
    const vec = s.vecCount>0?`<span class="vec">vec[${s.vecCount}]</span>`:'';
    const str = s.str?`<span class="str">${escapeHtml(s.str)}</span>`:'';
    return `<tr data-off="${s.off}">
      <td class="off">+0x${s.off.toString(16).toUpperCase().padStart(3,'0')}</td>
      <td>${tw}</td>
      <td class="hex">${s.hex}</td>
      <td>${ptr} ${vec}</td>
      <td>${fl}${fl1}</td>
      <td>${ints}</td>
      <td class="asc">${escapeHtml(s.ascii||'')}</td>
      <td>${str}</td>
    </tr><tr class="sub" data-suboff="${s.off}"><td colspan="8" style="padding:0"></td></tr>`;
  }).join('');
  host.innerHTML = `<div class="node"><div class="hint">${d.addr} · ${d.len} bytes</div>
    <table><tr><th>off</th><th></th><th>bytes</th><th>pointer / vec</th><th>float</th><th>int32</th><th>ascii</th><th>string</th></tr>${rows}</table></div>`;
  host.querySelectorAll('.ptr').forEach(p=>p.onclick=()=>openMem(p.dataset.ptr));
  host.querySelectorAll('.tw[data-ptr]').forEach(tw=>tw.onclick=()=>{
    const subCell = tw.closest('tr').nextElementSibling.firstElementChild;
    if(tw.textContent==='▾'){ tw.textContent='▸'; subCell.innerHTML=''; return; }
    tw.textContent='▾';
    const box=document.createElement('div'); box.className='kids'; subCell.appendChild(box);
    renderMem(box, tw.dataset.ptr, '0x100');
  });
}
function escapeHtml(s){ return s.replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c])); }

// ── UI explorer: ONE flat snapshot powers both the diff AND navigation (parent/children/values).
//    Click any element — in the diff or the tree — to focus it; walk up via the breadcrumb, down via
//    the children list, and inspect its raw bytes inline without leaving the tab.
let uiBase=null, uiCur=null, uiKids=null, lastDiff=null, uiFocus=null;
async function uiFlat(){ const d=await api('/api/ui-flat'); if(d.error) throw new Error(d.error); return d; }
function buildKids(map){ const k=new Map(); map.forEach(n=>{ if(!k.has(n.parent)) k.set(n.parent,[]); k.get(n.parent).push(n); }); return k; }
function uiRootNode(){ for(const n of uiCur.values()) if(n.path==='root') return n; return null; }

async function uiSnapshot(){
  try{
    const d=await uiFlat();
    uiCur=new Map(d.nodes.map(n=>[n.addr,n])); uiBase=uiCur; uiKids=buildKids(uiCur); lastDiff=null;
    $('#uidiffstatus').textContent=`snapshot: ${d.nodes.length} elements${d.capped?' (capped!)':''} · explore below, or act in game then Diff`;
    $('#uidiff').innerHTML=''; const r=uiRootNode(); if(r) focusUi(r.addr);
  }catch(e){ $('#uidiffstatus').textContent='snapshot failed: '+e.message; }
}
async function uiDiff(){
  if(!uiBase){ $('#uidiffstatus').textContent='take a snapshot first (①)'; return; }
  let d; try{ d=await uiFlat(); }catch(e){ $('#uidiffstatus').textContent='diff failed: '+e.message; return; }
  uiCur=new Map(d.nodes.map(n=>[n.addr,n])); uiKids=buildKids(uiCur);   // explore the CURRENT tree
  const shown=[],hidden=[],flags=[],added=[],removed=[];
  uiCur.forEach((n,addr)=>{ const b=uiBase.get(addr);
    if(!b){ added.push(n); return; }
    if(b.visible!==n.visible) (n.visible?shown:hidden).push(n);
    else if(b.flags!==n.flags) flags.push(Object.assign({was:b.flags},n));
  });
  uiBase.forEach((b,addr)=>{ if(!uiCur.has(addr)) removed.push(b); });
  const tot=shown.length+hidden.length+flags.length+added.length+removed.length;
  $('#uidiffstatus').textContent=`${tot} change(s): ${shown.length} shown, ${hidden.length} hidden, ${flags.length} flags, ${added.length} added, ${removed.length} removed · click one to explore`;
  lastDiff={shown,hidden,flags,added,removed}; paintUiDiff();
}
function paintUiDiff(){
  if(!lastDiff){ $('#uidiff').innerHTML=''; return; }
  const f=$('#uidifffilter').value.toLowerCase();
  const sect=(title,arr,cls)=>{
    const rows=arr.filter(n=>!f||(n.addr+n.path+n.flags).toLowerCase().includes(f));
    if(!rows.length) return '';
    return `<div class="hint" style="margin-top:8px">${title} (${rows.length}${rows.length!==arr.length?(' of '+arr.length):''})</div>`+
      rows.map(n=>`<div class="uirow"><span class="${cls}">●</span> <span class="ptr uin" data-a="${n.addr}">${n.addr}</span>
        <span class="hint">path=${n.path} flags=${n.flags}${n.was?(' (was '+n.was+')'):''} children=${n.childCount}</span></div>`).join('');
  };
  const html=sect('SHOWN',lastDiff.shown,'vis')+sect('HIDDEN',lastDiff.hidden,'invis')
    +sect('FLAGS CHANGED',lastDiff.flags,'vec')+sect('ADDED',lastDiff.added,'flt')+sect('REMOVED',lastDiff.removed,'err');
  $('#uidiff').innerHTML=html||'<span class="hint">no changes between snapshot and now</span>';
  $('#uidiff').querySelectorAll('.uin').forEach(p=>p.onclick=()=>focusUi(p.dataset.a));
}

// Focus one element: ancestry breadcrumb (click to go up) → fields + inline bytes → children (click to descend).
function focusUi(addr){
  if(!addr||addr==='0x0') return;
  uiFocus=addr;
  const host=$('#uiexplore');
  const n=uiCur && uiCur.get(addr);
  if(!n){ host.innerHTML=`<div class="card err">${addr} is not in the current tree (removed since snapshot — re-snapshot to refresh)</div>`; return; }
  let cur=n, guard=0; const chain=[];
  while(cur && guard++<128){ chain.unshift(cur); cur=uiCur.get(cur.parent); }
  const crumb=chain.map((c,i)=>`<span class="ptr uin" data-a="${c.addr}">${i===chain.length-1?'● ':''}${shortAddr(c.addr)}</span>`).join(' <span class="mut">›</span> ');
  const vis=n.visible?'<span class="vis">✓ visible</span>':'<span class="invis">· hidden</span>';
  const kids=uiKids.get(addr)||[];
  const kidRows=kids.map(c=>{
    const cv=c.visible?'<span class="vis">✓</span>':'<span class="invis">·</span>';
    const arrow=c.childCount>0?'▸':'·';
    return `<div class="uirow"><span class="tw">${arrow}</span>${cv}
      <span class="ptr uin" data-a="${c.addr}">${c.addr}</span>
      <span class="hint">[${c.path.split('/').pop()}] flags=${c.flags} children=${c.childCount}</span></div>`;
  }).join('') || '<span class="hint">no children</span>';
  host.innerHTML=`<div class="card">
    <div class="crumb">${crumb}</div>
    <div class="focus"><b class="ptr" id="focusaddr">${n.addr}</b> ${vis}
      <span class="hint">flags=${n.flags} children=${n.childCount} parent=</span><span class="ptr uin" data-a="${n.parent}">${n.parent}</span>
      <button class="act" id="bytesbtn">show bytes ▾</button><a class="lnk" id="memlink">open in Memory tab ↗</a></div>
    <div id="focusbytes"></div>
    <div class="hint" style="margin-top:8px">children (${kids.length})</div>
    <div class="kids">${kidRows}</div></div>`;
  host.querySelectorAll('.uin').forEach(p=>p.onclick=()=>focusUi(p.dataset.a));
  $('#focusaddr').onclick=()=>openMem(n.addr);
  $('#memlink').onclick=()=>openMem(n.addr);
  const bb=$('#bytesbtn'); bb.onclick=()=>{ const fb=$('#focusbytes');
    if(fb.innerHTML){ fb.innerHTML=''; bb.textContent='show bytes ▾'; return; }
    bb.textContent='hide bytes ▴'; renderMem(fb, n.addr, '0x200'); };
}
function shortAddr(a){ return a.length>10?('…'+a.slice(-7)):a; }

// ── entities ──
let ents=[];
async function loadEnt(){
  try{ ents=await api('/api/entities'); }catch(e){ $('#entlist').innerHTML='<span class="err">'+e.message+'</span>'; return; }
  renderEnt();
}
function renderEnt(){
  const f=$('#entfilter').value.toLowerCase();
  const rows=ents.filter(e=>!f||e.metadata.toLowerCase().includes(f));
  $('#entcount').textContent=`${rows.length}/${ents.length}`;
  $('#entlist').innerHTML='<table><tr><th>id</th><th>addr</th><th>metadata</th></tr>'+
    rows.map(e=>`<tr><td class="int">${e.id}</td><td><span class="ptr" data-a="${e.addr}">${e.addr}</span></td>
      <td>${escapeHtml(e.metadata)} <a class="lnk" data-c="${e.addr}">components</a></td></tr>
      <tr class="sub"><td colspan="3" style="padding:0"></td></tr>`).join('')+'</table>';
  $('#entlist').querySelectorAll('.ptr[data-a]').forEach(p=>p.onclick=()=>openMem(p.dataset.a));
  $('#entlist').querySelectorAll('.lnk[data-c]').forEach(l=>l.onclick=async()=>{
    const cell=l.closest('tr').nextElementSibling.firstElementChild;
    if(cell.innerHTML){ cell.innerHTML=''; return; }
    const cs=await api('/api/components?addr='+l.dataset.c);
    const box=document.createElement('div'); box.className='kids';
    box.innerHTML=cs.map(c=>`<div class="uirow"><span class="ptr" data-a="${c.addr}">${c.addr}</span> ${escapeHtml(c.name)}</div>`).join('')||'<span class="hint">no components</span>';
    box.querySelectorAll('.ptr[data-a]').forEach(p=>p.onclick=()=>openMem(p.dataset.a));
    cell.appendChild(box);
  });
}

// ── search ──
async function goSearch(){
  const t=$('#stype').value, v=$('#sval').value.trim(); if(!v) return;
  $('#sresults').innerHTML='<span class="hint">scanning…</span>';
  let d; try{ d=await api(`/api/search?type=${t}&value=${encodeURIComponent(v)}`);}catch(e){ $('#sresults').innerHTML='<span class="err">'+e.message+'</span>'; return; }
  if(d.error){ $('#sresults').innerHTML='<span class="err">'+d.error+'</span>'; return; }
  $('#sinfo').textContent=`${d.hits.length} hit(s)${d.capped?' (capped)':''} · scanned ${d.scannedMB} MB`;
  $('#sresults').innerHTML='<table><tr><th>#</th><th>address</th></tr>'+
    d.hits.map((h,i)=>`<tr><td class="off">${i}</td><td><span class="ptr" data-a="${h}">${h}</span></td></tr>`).join('')+'</table>';
  $('#sresults').querySelectorAll('.ptr[data-a]').forEach(p=>p.onclick=()=>openMem(p.dataset.a));
}

loadRoots();
setInterval(loadRoots, 4000);   // keep roots fresh across zoning
</script>
</body>
</html>
""";
}
