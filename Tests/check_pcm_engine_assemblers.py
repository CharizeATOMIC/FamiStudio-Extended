"""Compare optional PCM destination feeders across four assemblers and preserve the off path."""
from pathlib import Path
import subprocess,os,sys
r=Path(__file__).resolve().parents[1];b=r/'work/pcm-engine-tests';b.mkdir(parents=True,exist_ok=True)
engine=r/'SoundEngine';tools=r/'Tools'
for kind,ext in [('ca65','s'),('asm6','asm'),('nesasm','asm'),('sdas','s')]:
 s=(engine/f'UnitTests/test_{kind}.{ext}').read_text(encoding='utf-8-sig')
 s=s.replace('"UnitTests\\test_defs.inc"','"../work/pcm-engine-tests/test_defs.inc"').replace('"test_defs.inc"','"../work/pcm-engine-tests/test_defs.inc"')
 s=s.replace(f'"..\\famistudio_{kind}.{ext}"',f'"famistudio_{kind}.{ext}"')
 # Unconditional callback stubs keep reference layouts identical for flag 0/1.
 marker='famistudio_dpcm_bank_callback:'
 s=s.replace(marker,'famistudio_pcm_bank_callback:\nfamistudio_pcm_restore_callback:\n'+marker)
 (b/f'test_{kind}.{ext}').write_text(s,encoding='utf-8')
def run(exe,args):
 result=subprocess.run([str(tools/exe),*map(str,args)],cwd=engine,capture_output=True,text=True,env={**os.environ,'NES_INCLUDE':str(engine)})
 if result.returncode or 'Error' in result.stdout or 'error(s)' in result.stdout or 'error:' in result.stderr:raise RuntimeError(exe+'\n'+result.stdout+result.stderr)
 return result
for expansion, pcm_flag in [('APU','APU'),('MMC5','MMC5'),('VRC6','VRC6'),('MMC5','APU,MMC5'),('VRC6','APU,VRC6')]:
 for pcm in [0,1]:
  for tempo in [0,1]:
   for transient in [0,1]:
    definitions=['FAMISTUDIO_CFG_NTSC_SUPPORT=1','FAMISTUDIO_CFG_PAL_SUPPORT=1',f'FAMISTUDIO_EXP_{expansion}=1',f'FAMISTUDIO_USE_FAMITRACKER_TEMPO={tempo}',f'FAMISTUDIO_PCM_TRANSIENT_WINDOW={transient}',f'FAMISTUDIO_PCM_TRACK_PATH={transient}', f'FAMISTUDIO_PCM_STOP_IMMEDIATE={transient}', 'FAMISTUDIO_USE_UPDATE_RATE=1','FAMISTUDIO_USE_VOLUME_TRACK=1']
    definitions += [f'FAMISTUDIO_USE_{flag}_PCM={pcm}' for flag in pcm_flag.split(',')]
    (b/'test_defs.inc').write_text('\n'.join(definitions)+'\n')
    run('ca65.exe',[b/'test_ca65.s','-I',engine,'-g','-o',b/'test_ca65.o'])
    run('ld65.exe',['-C',engine/'UnitTests/test_ca65.cfg','-o',b/'test_ca65.nes',b/'test_ca65.o'])
    run('asm6_fixed.exe',[b/'test_asm6.asm',b/'test_asm6.nes'])
    run('NESASM3.exe',[b/'test_nesasm.asm'])
    run('sdas6500.exe',['-pogn','-I'+str(engine),'-y','-s','-l',b/'test_sdas.o',b/'test_sdas.s'])
    run('sdld6808.exe',['-n','-i','-j','-y','-w','-u','-w','-b','_ZP=0x0000','-b','_BSS=0x300','-b','_CODE=0x8000',b/'test_sdas.ihx',b/'test_sdas.o'])
    run('makebin.exe',['-s','73728','-o','32752',b/'test_sdas.ihx',b/'test_sdas.nes'])
    reference=(b/'test_ca65.nes').read_bytes()
    for other in ['asm6','nesasm','sdas']:
     data=(b/f'test_{other}.nes').read_bytes();assert data==reference,(pcm,tempo,transient,other,next((i for i,(a,c)in enumerate(zip(data,reference))if a!=c),None))
    if pcm==0:
     old=subprocess.run(['git','show','HEAD:SoundEngine/famistudio_ca65.s'],cwd=r,capture_output=True,check=True).stdout
     (b/'baseline.s').write_bytes(old)
     (b/'baseline-test.s').write_text((b/'test_ca65.s').read_text().replace('"famistudio_ca65.s"','"../work/pcm-engine-tests/baseline.s"'))
     run('ca65.exe',[b/'baseline-test.s','-I',engine,'-o',b/'baseline.o'])
     run('ld65.exe',['-C',engine/'UnitTests/test_ca65.cfg','-o',b/'baseline.nes',b/'baseline.o'])
     assert (b/'baseline.nes').read_bytes()==reference,'Flag-off legacy bytes changed'
    print('PASS',expansion,pcm_flag,'PCM',pcm,'tempo',tempo,'transient',transient,'all four assemblers agree',flush=True)
print('PASS all 40 PCM configurations, including unchanged disabled engine bytes')
