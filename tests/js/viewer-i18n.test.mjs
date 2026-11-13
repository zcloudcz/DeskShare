// Run: node tests/js/viewer-i18n.test.mjs
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
const source = fs.readFileSync('src/WebClient/i18n.js', 'utf8');
function load({query='', saved=null, languages=['en'], blocked=false} = {}) {
    const events = {};
    const select = {replaceChildren(){}, addEventListener(name, handler){events[name]=handler;}};
    const document = {documentElement:{}, querySelectorAll:()=>[], getElementById:()=>select};
    const storage = {getItem(){if(blocked) throw Error(); return saved;}, setItem(k,v){saved=v;}, removeItem(){saved=null;}};
    const context = vm.createContext({document, localStorage:storage, location:{href:'https://viewer.test/'+query}, navigator:{languages}, URL, Option:function(){}, history:{replaceState(){}}, window:{addEventListener(){},dispatchEvent(){}}, Event:function(){}});
    const api = vm.runInContext(source+';DeskShareI18n',context);
    api.init();
    return {api, document, switchTo(value){events.change({target:{value}});}, saved:()=>saved};
}
assert.equal(load({query:'?lang=sk', saved:'de', languages:['cs']}).api.language,'sk');
assert.equal(load({saved:'ar'}).document.documentElement.dir,'rtl');
assert.equal(load({languages:['sv-SE','de-AT']}).api.language,'de');
assert.equal(load({languages:['pt-BR']}).api.language,'pt');
assert.equal(load({languages:['zh-Hant']}).api.language,'zh');
assert.equal(load({languages:['sv']}).api.language,'en');
assert.equal(load({query:'?lang=bogus',saved:'uk'}).api.language,'uk');
assert.equal(load({blocked:true,languages:['cs']}).api.language,'cs');
const app=load({query:'?lang=cs',languages:['de-AT']});
app.switchTo('ar'); assert.equal(app.api.language,'ar'); assert.equal(app.saved(),'ar');
app.switchTo(''); assert.equal(app.api.language,'de'); assert.equal(app.saved(),null);
assert.equal(app.document.documentElement.dir,'ltr');
const {dictionaries}=app.api;
assert.equal(Object.keys(dictionaries).length,17);
const keys=Object.keys(dictionaries.en).sort();
const params=text=>[...text.matchAll(/\{(\w+)\}/g)].map(m=>m[1]).sort();
for(const [language,dict] of Object.entries(dictionaries)) {
    assert.deepEqual(Object.keys(dict).sort(),keys,language);
    for(const key of keys) {
        assert.ok(dict[key].trim(),`${language}:${key}`);
        assert.deepEqual(params(dict[key]),params(dictionaries.en[key]),`${language}:${key}`);
        assert.ok(!/[—–]/.test(dict[key]),`${language}:${key}`);
    }
}
const html=fs.readFileSync('src/WebClient/index.html','utf8');
const client=fs.readFileSync('src/WebClient/client.js','utf8');
for(const [,key] of (html+client).matchAll(/(?:data-i18n="|uiText\(')([\w.]+)/g)) if (!key.endsWith('.')) assert.ok(dictionaries.en[key],key);
for (const state of ['new','connecting','connected','disconnected','failed','closed']) assert.ok(dictionaries.en['rtc.'+state]);
assert.ok(Buffer.byteLength(source)<150*1024);
console.log('viewer i18n OK: 17 complete dictionaries, detection, switching, RTL, placeholders');
