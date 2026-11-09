'use strict';
(function(root) {
 const clone = value => structuredClone(value);
 const names = {pi:'Pi',codex:'Codex',claude:'Claude Code',gemini:'Gemini CLI',opencode:'OpenCode',hermes:'Hermes',omp:'Omp',kimi:'Kimi',qoder:'Qoder',custom:'自定义 Agent'};
 function initial() {return {schemaVersion:1,revision:0,projectPath:'D:\\Projects\\demo',layout:'vertical',directoryMode:'各自独立',slots:['pi','codex','pi','hermes'].map((agentId,i)=>({id:i+1,agentId,enabled:i===0,profiles:{}}))};}
 class Model {
  constructor(){this.draft=initial();this.lastEdited=null;this.manual=true;this.templates=new Map();}
  profile(slot){return slot.profiles[slot.agentId] || {path:'',arg:''};}
  edit(change){const next=clone(this.draft);change(next);next.revision++;this.draft=next;this.manual=true;}
  switchAgent(id,agentId){if(!names[agentId])throw Error('未知 Agent');this.edit(d=>{d.slots.find(s=>s.id===id).agentId=agentId;});}
  beginEdit(id){const slot=this.draft.slots.find(s=>s.id===id);return {id,revision:this.draft.revision,agentId:slot.agentId,profile:clone(this.profile(slot))};}
  applyEdit(session,profile){if(session.revision!==this.draft.revision)throw Error('配置已变化，请重新打开设置');this.edit(d=>{d.slots.find(s=>s.id===session.id).profiles[session.agentId]=clone(profile);});}
  save(key){this.templates.set(key,clone(this.draft));}
  restore(key){
   let next;
   if(key==='last'){if(!this.lastEdited)throw Error('没有上次组合');next=clone(this.lastEdited);}
   else if(this.templates.has(key)) next=clone(this.templates.get(key));
   else {next=initial();next.projectPath=this.draft.projectPath;if(key==='pair')next.slots[1].enabled=true;else if(key!=='single')throw Error('未知组合');}
   if(!['各自独立','使用已准备目录','共用原目录'].includes(next.directoryMode)||!next.slots||!next.layout)throw Error('组合字段不完整');
   if(this.manual)this.lastEdited=clone(this.draft);
   next.revision=this.draft.revision+1;this.draft=next;this.manual=false;
  }
 }
 root.PrototypeState={Model,names};
})(typeof module==='object'?module.exports:globalThis);
