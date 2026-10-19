"""Assemble the same engine with four assemblers and compare executable bytes."""
from pathlib import Path
import subprocess,os,sys,argparse
p=argparse.ArgumentParser();p.add_argument("--baseline",type=Path);args=p.parse_args()
r=Path(__file__).resolve().parents[1]
b=r/'work/apu-flags-engine-tests';b.mkdir(parents=True,exist_ok=True)
engine=r/'SoundEngine';tools=r/'Tools'
for kind,ext in [('ca65','s'),('asm6','asm'),('nesasm','asm'),('sdas','s')]:
 s=(engine/f'UnitTests/test_{kind}.{ext}').read_text(encoding='utf-8-sig')
 s=s.replace('"UnitTests\\test_defs.inc"','"../work/apu-flags-engine-tests/test_defs.inc"').replace('"test_defs.inc"','"../work/apu-flags-engine-tests/test_defs.inc"')
 s=s.replace(f'"..\\famistudio_{kind}.{ext}"',f'"famistudio_{kind}.{ext}"')
 (b/f'test_{kind}.{ext}').write_text(s,encoding='utf-8')
def run(exe,args):
 result=subprocess.run([str(tools/exe),*map(str,args)],cwd=engine,capture_output=True,text=True,env={**os.environ,'NES_INCLUDE':str(engine)})
 if result.returncode or 'error' in result.stdout.lower() or 'error:' in result.stderr:
  raise RuntimeError(exe+'\n'+result.stdout+result.stderr)
 return result
for flags in [0,1]:
 for tuning in [0,1]:
  for expansion in ['NONE','VRC6','VRC7','MMC5','FDS','N163','S5B','EPSM']:
   for features in [0,1]:
    definitions=[f'FAMISTUDIO_USE_APU_INSTRUMENT_FLAGS={flags}',f'FAMISTUDIO_USE_NOISE_ENVELOPES={features}',f'FAMISTUDIO_USE_ENVELOPE_DECAY={features}','FAMISTUDIO_CFG_NTSC_SUPPORT=1','FAMISTUDIO_CFG_PAL_SUPPORT=1',f'FAMISTUDIO_USE_CUSTOM_TUNING={tuning}']
    if expansion!='NONE':definitions.append(f'FAMISTUDIO_EXP_{expansion}=1')
    if expansion=='N163':definitions.append('FAMISTUDIO_EXP_N163_CHN_CNT=8')
    if features:definitions += [f'FAMISTUDIO_USE_{f}=1' for f in ['PITCH_TRACK','SLIDE_NOTES','VOLUME_TRACK','VOLUME_SLIDES','NOISE_SLIDE_NOTES','VIBRATO','ARPEGGIO','RELEASE_NOTES','PHASE_RESET']]
    (b/'test_defs.inc').write_text('\n'.join(definitions)+'\n')
    run('ca65.exe',[b/'test_ca65.s','-I',engine,'-g','-o',b/'test_ca65.o'])
    run('ld65.exe',['-C',engine/'UnitTests/test_ca65.cfg','-o',b/'test_ca65.nes',b/'test_ca65.o'])
    run('asm6_fixed.exe',[b/'test_asm6.asm',b/'test_asm6.nes'])
    run('NESASM3.exe',[b/'test_nesasm.asm'])
    run('sdas6500.exe',['-pogn','-I'+str(engine),'-y','-s','-l',b/'test_sdas.o',b/'test_sdas.s'])
    run('sdld6808.exe',['-n','-i','-j','-y','-w','-u','-w','-b','_ZP=0x0000','-b','_BSS=0x300','-b','_CODE=0x8000',b/'test_sdas.ihx',b/'test_sdas.o'])
    run('makebin.exe',['-s','73728','-o','32752',b/'test_sdas.ihx',b/'test_sdas.nes'])
    reference=(b/'test_ca65.nes').read_bytes()
    if flags==0 and args.baseline:
     baseline_source=(b/'test_ca65.s').read_text().replace('\"famistudio_ca65.s\"','\"'+'../work/apu-flags-engine-tests/baseline-engine.s'+'\"')
     (b/'baseline.s').write_text(baseline_source)
     (b/'baseline-engine.s').write_bytes((args.baseline/'famistudio_ca65.s').read_bytes())
     run('ca65.exe',[b/'baseline.s','-I',engine,'-g','-o',b/'baseline.o'])
     run('ld65.exe',['-C',engine/'UnitTests/test_ca65.cfg','-o',b/'baseline.nes',b/'baseline.o'])
     assert (b/'baseline.nes').read_bytes()==reference,('disabled feature changed legacy bytes',tuning,expansion,features)
    for other in ['asm6','nesasm','sdas']:
     data=(b/f'test_{other}.nes').read_bytes()
     assert data==reference,(tuning,expansion,features,other,next((i for i,(a,c) in enumerate(zip(data,reference)) if a!=c),None))
    print('PASS',flags,tuning,expansion,features,'all four assemblers agree',flush=True)
