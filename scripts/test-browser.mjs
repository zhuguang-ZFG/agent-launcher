import {spawn} from 'node:child_process';
import {mkdtemp,readFile,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import assert from 'node:assert/strict';
const root=path.resolve(import.meta.dirname,'..');
const profile=await mkdtemp(path.join(tmpdir(),'AgentLauncher-browser-'));
const browser=spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',['--headless=new','--disable-gpu','--no-first-run','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{stdio:'ignore'});
let ws;let nextId=0;const pending=new Map();
async function send(method,params={}){const id=++nextId;return await new Promise((resolve,reject)=>{const timeout=setTimeout(()=>{pending.delete(id);reject(Error(`CDP timeout: ${method}`));},10000);pending.set(id,{resolve:v=>{clearTimeout(timeout);resolve(v);},reject:e=>{clearTimeout(timeout);reject(e);}});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const result=await send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(result.exceptionDetails)throw Error(JSON.stringify(result.exceptionDetails));return result.result.value;}
try{
 let port;for(let i=0;i<100;i++){try{port=(await readFile(path.join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0];break;}catch{await new Promise(r=>setTimeout(r,100));}}
 if(!port)throw Error('Browser startup timeout');
 const tabs=await(await fetch(`http://127.0.0.1:${port}/json/list`)).json();ws=new WebSocket(tabs.find(t=>t.type==='page').webSocketDebuggerUrl);
 ws.addEventListener('message',event=>{const message=JSON.parse(event.data);const waiter=pending.get(message.id);if(waiter){pending.delete(message.id);if(message.error)waiter.reject(Error(message.error.message));else waiter.resolve(message.result);}});
 await new Promise((resolve,reject)=>{ws.addEventListener('open',resolve,{once:true});ws.addEventListener('error',reject,{once:true});});
 await send('Page.enable');await send('Emulation.setDeviceMetricsOverride',{width:1040,height:900,deviceScaleFactor:1,mobile:false});
 await send('Page.navigate',{url:pathToFileURL(path.join(root,'docs/prototype.html')).href});
 for(let i=0;i<50;i++){if(await evaluate('typeof model !== "undefined" && document.readyState === "complete"'))break;await new Promise(r=>setTimeout(r,100));}
 const results=await evaluate(`(()=>{
 const results=[];const click=id=>document.getElementById(id).click();
 const value=(id,v)=>{const e=document.getElementById(id);e.value=v;e.dispatchEvent(new Event('change',{bubbles:true}));};
 const check=(name,ok)=>{if(!ok)throw Error(name);results.push(name);};
 document.querySelector('.slot .more').click();document.getElementById('agent-path').value='C:/Tools/pi.cmd';document.getElementById('agent-arg').value='--pi-specific';click('apply-agent');
 let select=document.querySelector('.slot select');select.value='codex';select.dispatchEvent(new Event('change'));document.querySelector('.slot .more').click();check('F15 switch executable',document.getElementById('agent-path').value==='');document.querySelector('#agent-dialog [data-close]').click();
 select=document.querySelector('.slot select');select.value='pi';select.dispatchEvent(new Event('change'));document.querySelector('.slot .more').click();check('F15 restore executable',document.getElementById('agent-path').value==='C:/Tools/pi.cmd');document.querySelector('#agent-dialog [data-close]').click();
 click('save-button');document.getElementById('template-name').value='独立组合';click('apply-save');const saved=document.getElementById('template').value;
 click('directory-button');document.querySelector('input[value="共用原目录"]').checked=true;click('apply-directory');value('template',saved);check('F16 restore mode',document.getElementById('directory-label').textContent==='各自独立');
 click('directory-button');document.querySelector('input[value="共用原目录"]').checked=true;document.querySelector('#directory-dialog [data-close]').click();click('directory-button');check('F16 cancel radio',document.querySelector('input[name=directory-mode]:checked').value==='各自独立');document.querySelector('#directory-dialog [data-close]').click();
 const boxes=[...document.querySelectorAll('.slot-check input')];boxes.forEach((box,i)=>{box.checked=i===3;box.dispatchEvent(new Event('change'));});value('template','pair');value('template','single');value('template','last');check('F17 last slot 4',JSON.stringify(model.draft.slots.filter(s=>s.enabled).map(s=>s.id))==='[4]');
 return results;
})()`);
 assert.equal(results.length,5);
 const shot=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});await writeFile(path.join(root,'artifacts/prototype-browser.png'),Buffer.from(shot.data,'base64'));
 await writeFile(path.join(root,'artifacts/prototype-browser-results.json'),JSON.stringify({passed:results},null,2));console.log(results.map(x=>'PASS '+x).join('\n'));
 await send('Browser.close');
}finally{ws?.close();if(browser.exitCode===null)browser.kill();}
