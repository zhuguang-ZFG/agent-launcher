'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const {PrototypeState:{Model}} = require('../docs/prototype-state.js');
test('F15: Agent switch uses its own executable and restores original',()=>{
 const m=new Model();m.applyEdit(m.beginEdit(1),{path:'C:/Tools/pi.cmd',arg:'--pi-specific'});
 m.switchAgent(1,'codex');assert.deepEqual(m.profile(m.draft.slots[0]),{path:'',arg:''});
 m.switchAgent(1,'pi');assert.equal(m.profile(m.draft.slots[0]).path,'C:/Tools/pi.cmd');
});
test('F16: template restores isolation and nested profiles without aliasing',()=>{
 const m=new Model();m.applyEdit(m.beginEdit(1),{path:'pi.cmd',arg:'first'});m.save('saved');
 m.edit(d=>{d.directoryMode='共用原目录';d.slots[0].profiles.pi.arg='changed';});m.restore('saved');
 assert.equal(m.draft.directoryMode,'各自独立');assert.equal(m.profile(m.draft.slots[0]).arg,'first');
 m.edit(d=>d.slots[0].profiles.pi.arg='again');m.restore('saved');assert.equal(m.profile(m.draft.slots[0]).arg,'first');
});
test('F17: browsing several templates retains manually edited slot 4 snapshot',()=>{
 const m=new Model();m.edit(d=>{d.slots.forEach(s=>s.enabled=s.id===4);d.layout='horizontal';d.directoryMode='使用已准备目录';});
 const expected=structuredClone(m.draft);m.restore('pair');m.restore('single');m.restore('last');
 const {revision,...actual}=m.draft;delete expected.revision;assert.deepEqual(actual,expected);
});
test('F16: cancelled edit and stale edit cannot change Draft',()=>{
 const m=new Model();const edit=m.beginEdit(1);edit.profile.path='cancelled';assert.equal(m.profile(m.draft.slots[0]).path,'');
 m.switchAgent(1,'codex');assert.throws(()=>m.applyEdit(edit,{path:'wrong',arg:''}));
});
test('F16/F17: incomplete template and missing last snapshot refuse without mutation',()=>{
 const m=new Model();assert.throws(()=>m.restore('last'));m.templates.set('bad',{slots:[],layout:'vertical'});
 const before=structuredClone(m.draft);assert.throws(()=>m.restore('bad'));assert.deepEqual(m.draft,before);
});
