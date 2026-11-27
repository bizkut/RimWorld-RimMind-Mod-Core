'use strict';
const $ = id => document.getElementById(id);
const esc = value => String(value ?? '—').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const number = (v, digits=1) => typeof v === 'number' && Number.isFinite(v) ? v.toLocaleString(undefined,{maximumFractionDigits:digits}) : '—';
const pct = v => typeof v === 'number' ? number(v * 100, 0) + '%' : '—';
const empty = () => ({snapshots:[], actions:[], llm:[], letters:[], geography:null, start:null, end:null, received:[]});
let current = empty(), comparison = empty(), runs = [], streams = {}, scheduled = false;
const charts = [ ['Alive & downed',['alive','downed']], ['Mood',['mood']], ['Food days',['food_days']], ['Wealth',['wealth']], ['Research',['research_done']], ['Bills & construction',['active_bills','pending_construction']], ['Hostiles',['hostiles']] ];
const colors = ['#69c4af','#f6ae6c'];
const names = {alive:'Alive',downed:'Downed',mood:'Mood',food_days:'Food days',wealth:'Wealth',research_done:'Research',active_bills:'Bills',pending_construction:'Construction',hostiles:'Hostiles'};
const tileColors = {'~':'#427d9b','^':'#777c80','"':'#826a45',',':'#847654','.':'#b3a77f','m':'#657d66','T':'#38684b','$':'#dcaf55','?':'#28343c','#':'#d2c9b5','S':'#cb9b67','G':'#8fb35b','@':'#f7f1df'};
function queue() { if (!scheduled) { scheduled = true; requestAnimationFrame(() => { scheduled=false; render(); }); } }
function add(target, event) {
  if (!event || typeof event !== 'object') return;
  if (event.type === 'session_start') target.start=event;
  else if (event.type === 'snapshot') target.snapshots.push(event);
  else if (event.type === 'geography') target.geography=event;
  else if (event.type === 'decision' || event.type === 'action') target.actions.push(event);
  else if (event.type === 'llm') target.llm.push(event);
  else if (event.type === 'letter') target.letters.push(event);
  else if (event.type === 'session_end') target.end=event;
  target.received.push(Date.now());
  queue();
}
async function stream(id, key) {
  if (streams[key]) streams[key].abort();
  const controller = new AbortController(); streams[key]=controller;
  if (!id) return;
  const target = key === 'current' ? current : comparison;
  try {
    const response = await fetch('/api/runs/'+encodeURIComponent(id)+'/events', {signal:controller.signal});
    if (!response.ok || !response.body) throw new Error('HTTP '+response.status);
    const reader=response.body.pipeThrough(new TextDecoderStream()).getReader(); let buffer='';
    while (true) {
      const {value,done}=await reader.read(); if (done) break;
      buffer+=value; let pos;
      while ((pos=buffer.indexOf('\n\n'))>=0) {
        const block=buffer.slice(0,pos); buffer=buffer.slice(pos+2);
        for (const line of block.split('\n')) if (line.startsWith('data:')) { try { add(target,JSON.parse(line.slice(5))); } catch (_) {} }
      }
    }
  } catch (error) { if (error.name !== 'AbortError') { $('header').textContent='Stream error: '+error.message; } }
}
function selectRun() { current=empty(); stream($('run').value,'current'); queue(); }
function selectCompare() { comparison=empty(); stream($('compare').value,'comparison'); queue(); }
function drawChart(canvas, keys) {
  const rect=canvas.getBoundingClientRect(), ratio=window.devicePixelRatio || 1;
  canvas.width=Math.max(1,Math.round(rect.width*ratio)); canvas.height=Math.round(145*ratio);
  const ctx=canvas.getContext('2d'); ctx.scale(ratio,ratio);
  const w=rect.width,h=145,left=49,right=10,top=12,bottom=25;
  const series=[];
  for (const [dataset,dashed] of [[current,false],[comparison,true]]) for (const [i,key] of keys.entries()) {
    const points=dataset.snapshots.map(s=>({x:typeof s.day==='number'?s.day:s.tick/60000,y:s.colony?.[key]})).filter(p=>Number.isFinite(p.x)&&Number.isFinite(p.y));
    if(points.length) series.push({points,color:colors[i],dashed});
  }
  const points=series.flatMap(s=>s.points); ctx.font='11px system-ui'; ctx.fillStyle='#9ab0bd';
  if (!points.length) {ctx.fillText('No data',left,35);return;}
  let xmin=Math.min(...points.map(p=>p.x)),xmax=Math.max(...points.map(p=>p.x)),ymin=Math.min(0,...points.map(p=>p.y)),ymax=Math.max(...points.map(p=>p.y));
  if(xmax===xmin) {xmin=Math.max(0,xmin-.5);xmax+=.5;} if(ymax===ymin) ymax=ymin+1;
  const x=p=>left+(p-xmin)/(xmax-xmin)*(w-left-right), y=p=>top+(ymax-p)/(ymax-ymin)*(h-top-bottom);
  ctx.strokeStyle='#354550';ctx.lineWidth=1;ctx.beginPath();ctx.moveTo(left,top);ctx.lineTo(left,h-bottom);ctx.lineTo(w-right,h-bottom);ctx.stroke();
  ctx.fillText(number(ymax,2),2,top+4);ctx.fillText(number(ymin,2),2,h-bottom);ctx.fillText(number(xmin,1),left,h-6);ctx.textAlign='right';ctx.fillText(number(xmax,1),w-right,h-6);ctx.textAlign='left';
  for(const s of series){ctx.strokeStyle=s.color;ctx.fillStyle=s.color;ctx.lineWidth=2;ctx.setLineDash(s.dashed?[5,4]:[]);ctx.beginPath();s.points.forEach((p,i)=>i?ctx.lineTo(x(p.x),y(p.y)):ctx.moveTo(x(p.x),y(p.y)));ctx.stroke();for(const p of s.points){ctx.beginPath();ctx.arc(x(p.x),y(p.y),2,0,Math.PI*2);ctx.fill();}}ctx.setLineDash([]);
}
function table(headers, rows) { return '<table><thead><tr>'+headers.map(h=>'<th>'+esc(h)+'</th>').join('')+'</tr></thead><tbody>'+rows.map(r=>'<tr>'+r.map(v=>'<td>'+esc(v)+'</td>').join('')+'</tr>').join('')+'</tbody></table>'; }
function render() {
  const run=runs.find(r=>r.id===$('run').value), last=current.snapshots.at(-1), c=last?.colony || {};
  current.received=current.received.filter(t=>t>Date.now()-5000);
  const tick=last?.tick ?? current.start?.tick ?? 0;
  $('header').textContent=`${current.start?.model || run?.model || 'Unknown model'} · day ${Math.floor(tick/60000)} hour ${Math.floor((tick%60000)/2500)} · ${current.end?.reason || run?.status || (run?.kind==='legacy'?'recorded':'streaming')} · ${number(current.received.length/5,1)} events/sec`;
  const badge=(label,ok)=>`<span class="badge ${ok===null?'':ok?'good':'bad'}">${esc(label)}: ${ok===null?'unknown':ok?'met':'not met'}</span>`;
  $('badges').innerHTML=badge('No starvation',c.alive==null||c.food_days==null?null:c.alive>0&&c.food_days>0)+badge('Bills',c.active_bills==null?null:c.active_bills>0)+badge('Construction',c.pending_construction==null?null:c.pending_construction>0);
  if (!$('charts').children.length) $('charts').innerHTML=charts.map(([title,keys])=>`<div class="chart"><h3>${esc(title)} <small>${keys.map((k,i)=>`<span style="color:${colors[i]}">${esc(names[k])}</span>`).join(' / ')}</small></h3><canvas></canvas></div>`).join('');
  document.querySelectorAll('.chart canvas').forEach((canvas,i)=>drawChart(canvas,charts[i][1]));
  const bar=(name,value)=>`<div class="bar"><span>${name}</span><progress max="1" value="${typeof value==='number'?Math.max(0,Math.min(1,value)):0}"></progress><span>${pct(value)}</span></div>`;
  $('colonists').innerHTML=(last?.colonists || []).map(p=>`<article class="card"><h3>${esc(p.name)} ${p.downed?'· downed':''}</h3>${bar('Mood',p.mood)}${bar('Food',p.food)}${bar('Rest',p.rest)}<div class="muted">Health: ${esc(typeof p.health==='number'?pct(p.health):p.health)}<br>Mental: ${esc(p.mental || 'None')}<br>Job: ${esc(p.job)}<br>Agent: ${esc(p.agent)}</div></article>`).join('') || '<span class="muted">No colonist data yet.</span>';
  const actions=current.actions.filter(e=>(!$('source').value||e.source===$('source').value)&&(!$('failures').checked||(e.type==='action'&&e.ok===false)));
  $('feed').innerHTML=actions.slice(-200).reverse().map(e=>`<div class="entry ${e.ok===false?'fail':''}"><div class="meta">Day ${number(e.day,2)} · ${esc(e.source)} · ${esc(e.pawn || e.npc || 'colony')} · ${esc(e.type)}</div><strong>${esc(e.type==='action'?e.tool:e.action)}</strong> ${e.type==='action'?(e.ok?'✓':'✕'):''}<div>${esc(e.reason || e.result || '')}</div>${e.reason&&e.result?`<div class="muted">${esc(e.result)}</div>`:''}</div>`).join('') || '<span class="muted">No matching decisions or actions.</span>';
  const tools=new Map(); for(const e of current.actions.filter(e=>e.type==='action')) {const t=tools.get(e.tool)||[0,0,0];t[0]++;if(e.ok===true)t[1]++;else if(e.ok===false)t[2]++;tools.set(e.tool,t);}
  $('tools').innerHTML=tools.size?table(['Tool','Calls','OK','Failed','Success'],[...tools].map(([name,v])=>[name,...v,pct(v[1]/v[0])])):'<span class="muted">No tool calls.</span>';
  const groups=new Map(); for(const e of current.llm){const key=(e.source||'unknown').split('|')[0].trim();const a=groups.get(key)||[];a.push(e);groups.set(key,a);}
  const llmRows=[...groups].map(([key,a])=>{const lat=a.map(e=>e.latency_ms).filter(Number.isFinite).sort((x,y)=>x-y), q=p=>lat.length?number(lat[Math.ceil(p*lat.length)-1],0)+' ms':'—', prompt=a.reduce((s,e)=>s+(e.prompt_tokens||0),0),cached=a.reduce((s,e)=>s+(e.cached_tokens||0),0),tokens=a.reduce((s,e)=>s+(e.prompt_tokens||0)+(e.completion_tokens||0),0);return [key,a.length,pct(a.filter(e=>e.ok).length/a.length),q(.5),q(.95),number(tokens,0),prompt?pct(cached/prompt):'—'];});
  $('llm').innerHTML=llmRows.length?table(['Source','Calls','Success','p50','p95','Tokens','Cached prompt'],llmRows):'<span class="muted">No LLM events.</span>';
  $('letters').innerHTML=current.letters.slice(-20).reverse().map(e=>`<div class="entry"><strong>${esc(e.label)}</strong> <span class="muted">${esc(e.kind)} · day ${number(e.day,2)}</span><div>${esc(e.text)}</div></div>`).join('') || '<span class="muted">No letters.</span>';
  renderMap(current.geography,last);
  $('screenshots').innerHTML=(run?.screenshots||[]).map(f=>`<a target="_blank" rel="noopener" href="/api/runs/${encodeURIComponent(run.id)}/screenshots/${encodeURIComponent(f)}"><img loading="lazy" src="/api/runs/${encodeURIComponent(run.id)}/screenshots/${encodeURIComponent(f)}" alt="${esc(f)}"><br>${esc(f)}</a>`).join('')||'<span class="muted">No screenshots.</span>';
}
function renderMap(g,last) {
  if (!g || !Array.isArray(g.grid) || !g.cols || !g.rows || !g.block) { $('map').innerHTML='<span class="muted">No geography yet.</span>'; return; }
  $('map').innerHTML='<canvas></canvas><div class="legend"></div><div class="muted" id="pois"></div>';
  const canvas=$('map').querySelector('canvas'), size=16; canvas.width=g.cols*size;canvas.height=g.rows*size;const ctx=canvas.getContext('2d');
  g.grid.forEach((row,z)=>[...row].forEach((tile,x)=>{ctx.fillStyle=tileColors[tile]||'#48565d';ctx.fillRect(x*size,z*size,size,size);}));
  function mark(p,color,label) {if(!Number.isFinite(p.x)||!Number.isFinite(p.z))return;const x=(Math.floor(p.x/g.block)+.5)*size,y=(g.rows-1-Math.floor(p.z/g.block)+.5)*size;if(x<0||y<0||x>=canvas.width||y>=canvas.height)return;ctx.fillStyle=color;ctx.beginPath();ctx.arc(x,y,5,0,7);ctx.fill();ctx.fillStyle='#101820';ctx.font='bold 9px sans-serif';ctx.fillText(label,x-3,y+3);}
  (g.pois||[]).forEach(p=>mark(p,'#f2bc59','◆'));(last?.colonists||[]).forEach(p=>mark(p,'#fff',String(p.name||'?')[0]));
  $('map').querySelector('.legend').innerHTML=Object.entries(g.legend||{}).map(([k,v])=>`<span><i class="swatch" style="background:${tileColors[k]||'#48565d'}"></i>${esc(k)} ${esc(v)}</span>`).join('')+'<span>● colonist</span><span>◆ POI</span>';
  $('pois').textContent=(g.pois||[]).map(p=>`${p.label || p.kind} (${p.x}, ${p.z})`).join(' · ');
}
async function init() {
  try {const response=await fetch('/api/runs');if(!response.ok)throw new Error('HTTP '+response.status);runs=await response.json();
    const option=r=>`<option value="${esc(r.id)}">${esc(r.id==='__live__'?'● Live session':r.id)} (${esc(r.kind)})</option>`;
    $('run').innerHTML=runs.map(option).join('');$('compare').innerHTML='<option value="">None</option>'+runs.map(option).join('');
    const control=runs.find(r=>r.is_control && r.id!==$('run').value);if(control)$('compare').value=control.id;
    $('run').addEventListener('change',selectRun);$('compare').addEventListener('change',selectCompare);$('source').addEventListener('change',queue);$('failures').addEventListener('change',queue);window.addEventListener('resize',queue);
    if(runs.length){selectRun();selectCompare();}else $('header').textContent='No runs found';
  } catch(e) {$('header').textContent='Could not load runs: '+e.message;}
}
setInterval(queue,5000);init();
