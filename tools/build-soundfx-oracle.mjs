import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'artifacts/soundfx'); fs.mkdirSync(output, {recursive:true});
// Preserve every helper statement. Replace only its DOS-era headers and external AIL calls.
const original = fs.readFileSync(path.join(root,'reference/A214_D3/SOUNDFX.C'),'latin1').replace(/^#include[^\r\n]*/gm,'').replace(/\x1a/g,'');
const shim = `
#include <stdint.h>
typedef int HDRIVER; typedef int HTIMER; typedef int HEFFECT;
#define EFFECT_FREE 0
#define printf(...) ((void)0)
typedef int (*sink_type)(int,int,int,int,int);
static sink_type sink;
void sfx_set_sink(sink_type value) { sink=value; }
unsigned AIL_lock_channel(HDRIVER d) { return sink(0,d,0,0,0); }
void AIL_send_channel_voice_message(HDRIVER d,unsigned s,unsigned a,unsigned b) { sink(1,d,s,a,b); }
void AIL_release_channel(HDRIVER d,unsigned c) { sink(2,d,c,0,0); }
HTIMER AIL_register_timer(void (*callback)(void)) { (void)callback; return sink(3,0,0,0,0); }
void AIL_set_timer_period(HTIMER t,long period) { sink(4,t,(int)period,0,0); }
void AIL_start_timer(HTIMER t) { sink(5,t,0,0,0); }
void AIL_release_timer_handle(HTIMER t) { sink(6,t,0,0,0); }
`;
const facade = `
int sfx_command(int op,int a,int b,int c,int d,int e) {
 switch(op) {
 case 0: init_sfx(); return 0;
 case 1: return play_sound_effect(0,a,b,c,d,e);
 case 2: timer_callback(); return 0;
 case 3: return get_sound_effect_status(a);
 case 4: stop_sound_effect(a); return 0;
 case 5: shutdown_sfx(); return 0;
 default: return -1;
 }
}
`;
const source=path.join(output,'soundfx.c'); fs.writeFileSync(source,shim+original+facade);
const library=path.join(output,process.platform==='win32'?'soundfx.dll':process.platform==='darwin'?'libsoundfx.dylib':'libsoundfx.so');
const windowsGcc=['C:/msys64/ucrt64/bin/gcc.exe','C:/msys64/mingw64/bin/gcc.exe'].find(fs.existsSync);
const compiler=process.env.CC??windowsGcc??'gcc';
const environment=path.isAbsolute(compiler)
  ? {...process.env,PATH:`${path.dirname(compiler)}${path.delimiter}${process.env.PATH??''}`}
  : process.env;
const build=spawnSync(compiler,['-std=c11','-O2','-fPIC',process.platform==='darwin'?'-dynamiclib':'-shared',source,'-o',library],{stdio:'inherit',env:environment});
if(build.error) throw build.error;
if(build.status!==0) process.exit(build.status ?? 1);
console.log(`Built original SOUNDFX helper: ${library}`);
