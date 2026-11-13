// Exercise the actual browser script with isolated browser state.
const {readFileSync} = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const script = readFileSync(__dirname + '/../assets/site.js', 'utf8');
function run({path='/', languages=['cs'], saved=null, blocked=false, click=null, platform='Win32', ua='Desktop', mobileTouch=0}={}) {
  let redirect, assigned, removed=false, handler;
  const download = {href:'', textContent:''};
  const os = {};
  const storage = {getItem(){if(blocked) throw Error(); return saved;}, setItem(k,v){if(blocked) throw Error(); saved=v;}, removeItem(){if(blocked) throw Error(); saved=null;}};
  const location = {href:'https://deskshare.zcloud.cz'+path, pathname:new URL('https://deskshare.zcloud.cz'+path).pathname, replace(url){redirect=String(url);}, assign(url){assigned=String(url);}};
  const link = {dataset:{lang:click}, addEventListener(type,fn){handler=fn;}};
  const document = {querySelectorAll(){return click ? [link] : [];}, querySelector(){return {textContent:'All downloads'};}, getElementById(id){return id==='hero-dl' ? download : id==='hero-os' ? os : null;}};
  vm.runInNewContext(script,{URL, location, navigator:{languages,userAgent:ua,platform,maxTouchPoints:mobileTouch},localStorage:storage,document});
  if(click) handler({preventDefault(){}});
  return {redirect,assigned,saved,download,os};
}
assert.match(run({languages:['sv-SE','de-AT']}).redirect,/\/de\/$/);
assert.match(run({languages:['pt-BR']}).redirect,/\/pt\/$/);
assert.match(run({languages:['zh-Hant']}).redirect,/\/zh\/$/);
assert.match(run({languages:['sv']}).redirect,/\/en\/$/);
assert.equal(run({languages:['cs']}).redirect,undefined);
assert.match(run({saved:'ar',languages:['en']}).redirect,/\/ar\/$/);
assert.match(run({blocked:true,languages:['de']}).redirect,/\/de\/$/);
assert.equal(run({path:'/fr/',saved:'de',languages:['de']}).redirect,undefined);
assert.equal(run({path:'/en/privacy/',languages:['de']}).redirect,undefined);
assert.match(run({path:'/?lang=sk',saved:'de',languages:['cs']}).redirect,/\/sk\/$/);
assert.match(run({path:'/?lang=invalid',saved:'de'}).redirect,/\/de\/\?lang=invalid$/);
assert.equal(run({path:'/de/?lang=de'}).redirect,'https://deskshare.zcloud.cz/de/');
const manual=run({path:'/de/?lang=de',click:'ja'});
assert.equal(manual.saved,'ja'); assert.equal(manual.assigned,'https://deskshare.zcloud.cz/ja/');
const auto=run({path:'/ar/',saved:'ar',languages:['cs'],click:'auto'});
assert.equal(auto.saved,null); assert.equal(auto.assigned,'https://deskshare.zcloud.cz/');
assert.match(run().download.href,/DeskShare-win-Setup.exe$/);
assert.match(run({platform:'MacIntel'}).download.href,/DeskShare-osx-Setup.pkg$/);
assert.match(run({platform:'Linux'}).download.href,/DeskShare.AppImage$/);
assert.equal(run({ua:'iPhone'}).download.href,'#download');
console.log('Language precedence, storage failures, explicit paths, switchers and OS downloads passed.');
