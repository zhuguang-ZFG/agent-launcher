import {spawn} from 'node:child_process';
import {mkdir,mkdtemp,readFile,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import assert from 'node:assert/strict';
const root=path.resolve(import.meta.dirname,'..');
const report=path.join(root,'artifacts');
await mkdir(report,{recursive:true});
const profile=await mkdtemp(path.join(tmpdir(),'AgentLauncher-browser-'));
const executable='C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const browser=spawn(executable,['--headless=new','--disable-gpu','--no-first-run','--enable-logging=stderr','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{stdio:['ignore','ignore','pipe']});
let ws;let nextId=0;const pending=new Map();let failure;let rejectOpen;let closing=false;let stderr='';let version;let lastMethod;let results=[];
function fail(error){failure??=error;rejectOpen?.(failure);for(const waiter of pending.values())waiter.reject(failure);pending.clear();}
browser.stderr.on('data',data=>{stderr=(stderr+data.toString()).slice(-65536);});
browser.once('error',error=>fail(Error(`Browser startup failed: ${error.message}`)));
browser.once('exit',(code,signal)=>{if(!closing)fail(Error(`Browser exited before checks completed: code=${code}, signal=${signal}`));});
async function send(method,params={}){
 if(failure)throw failure;lastMethod=method;const id=++nextId;
 return await new Promise((resolve,reject)=>{
  const timeout=setTimeout(()=>{pending.delete(id);reject(Error(`CDP timeout: ${method}`));},10000);
  const waiter={resolve:v=>{clearTimeout(timeout);resolve(v);},reject:e=>{clearTimeout(timeout);reject(e);}};pending.set(id,waiter);
  try{ws.send(JSON.stringify({id,method,params}));}catch(error){pending.delete(id);waiter.reject(error);}
 });
}
async function evaluate(expression){const result=await send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(result.exceptionDetails)throw Error(JSON.stringify(result.exceptionDetails));return result.result.value;}
try{
 await writeFile(path.join(report,'prototype-browser-results.json'),JSON.stringify({status:'running',passed:[],profile},null,2));
 let port;for(let i=0;i<100;i++){if(failure)throw failure;try{port=(await readFile(path.join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0];break;}catch{await new Promise(r=>setTimeout(r,100));}}
 if(!port)throw Error('Browser startup timeout');
 const tabs=await(await fetch(`http://127.0.0.1:${port}/json/list`,{signal:AbortSignal.timeout(10000)})).json();
 const page=tabs.find(t=>t.type==='page');if(!page)throw Error('Browser did not create a page target');
 ws=new WebSocket(page.webSocketDebuggerUrl);
 ws.addEventListener('message',event=>{try{const message=JSON.parse(event.data);if(message.method==='Inspector.targetCrashed'){fail(Error('Browser page target crashed'));return;}const waiter=pending.get(message.id);if(waiter){pending.delete(message.id);if(message.error)waiter.reject(Error(message.error.message));else waiter.resolve(message.result);}}catch(error){fail(error);}});
 ws.addEventListener('close',event=>{if(!closing)fail(Error(`Browser CDP connection closed: ${event.code} ${event.reason}`));});
 ws.addEventListener('error',()=>fail(Error('Browser CDP connection failed')));
 await new Promise((resolve,reject)=>{const timeout=setTimeout(()=>reject(Error('Browser CDP connection timeout')),10000);rejectOpen=reject;ws.addEventListener('open',()=>{clearTimeout(timeout);rejectOpen=undefined;resolve();},{once:true});if(failure){clearTimeout(timeout);reject(failure);}});
 version=await send('Browser.getVersion');
 await send('Page.enable');await send('Emulation.setDeviceMetricsOverride',{width:1040,height:900,deviceScaleFactor:1,mobile:false});
 await send('Page.navigate',{url:pathToFileURL(path.join(root,'docs/prototype.html')).href});
 for(let i=0;i<50;i++){if(await evaluate('typeof model !== "undefined" && document.readyState === "complete"'))break;await new Promise(r=>setTimeout(r,100));}
 results=await evaluate(`(()=>{
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
 await writeFile(path.join(report,'prototype-browser-results.json'),JSON.stringify({status:'passed',passed:results,version},null,2));console.log(results.map(x=>'PASS '+x).join('\n'));
}catch(error){
 await writeFile(path.join(report,'prototype-browser-results.json'),JSON.stringify({status:'failed',passed:results,failed:error.message,executable,profile,version,lastMethod,exitCode:browser.exitCode,signal:browser.signalCode,stderr},null,2));
 console.error(`${error.message}\nBrowser diagnostics: ${path.join(report,'prototype-browser-results.json')}\n${stderr}`);process.exitCode=1;
}finally{closing=true;ws?.close();if(browser.exitCode===null)browser.kill();await writeFile(path.join(report,'prototype-browser-stderr.log'),stderr);}
