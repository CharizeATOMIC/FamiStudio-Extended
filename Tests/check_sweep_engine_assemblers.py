"""Check hardware sweep across four assemblers, with optional legacy byte comparison."""
from pathlib import Path
import argparse, itertools, os, subprocess

parser=argparse.ArgumentParser()
parser.add_argument('--baseline',type=Path)
args=parser.parse_args()
root=Path(__file__).resolve().parents[1]
build=root/'work/sweep-engine-tests';build.mkdir(parents=True,exist_ok=True)
engine=root/'SoundEngine';tools=root/'Tools'
for kind,ext in [('ca65','s'),('asm6','asm'),('nesasm','asm'),('sdas','s')]:
    text=(engine/f'UnitTests/test_{kind}.{ext}').read_text(encoding='utf-8-sig')
    text=text.replace('"UnitTests\\test_defs.inc"','"../work/sweep-engine-tests/test_defs.inc"').replace('"test_defs.inc"','"../work/sweep-engine-tests/test_defs.inc"')
    text=text.replace(f'"..\\famistudio_{kind}.{ext}"',f'"famistudio_{kind}.{ext}"')
    (build/f'test_{kind}.{ext}').write_text(text,encoding='utf8')

def run(exe,params):
    result=subprocess.run([str(tools/exe),*map(str,params)],cwd=engine,capture_output=True,text=True,env={**os.environ,'NES_INCLUDE':str(engine)})
    if result.returncode or 'error' in result.stdout.lower() or 'error:' in result.stderr:
        raise RuntimeError(exe+'\n'+result.stdout+result.stderr)

count=0
for sweep,expansion,features,sfx in itertools.product([0,1],['NONE','VRC6','VRC7','MMC5','FDS','N163','S5B','EPSM'],[0,1],[0,1]):
    definitions=[f'FAMISTUDIO_USE_PULSE_SWEEP={sweep}','FAMISTUDIO_CFG_NTSC_SUPPORT=1','FAMISTUDIO_CFG_PAL_SUPPORT=1',
                 f'FAMISTUDIO_CFG_SFX_SUPPORT={sfx}',f'FAMISTUDIO_CFG_SMOOTH_VIBRATO={features}']
    if expansion!='NONE':definitions.append(f'FAMISTUDIO_EXP_{expansion}=1')
    if expansion=='N163':definitions.append('FAMISTUDIO_EXP_N163_CHN_CNT=8')
    if features:
        definitions.extend(f'FAMISTUDIO_USE_{f}=1' for f in ['CUSTOM_TUNING','NOISE_ENVELOPES','ENVELOPE_DECAY','APU_INSTRUMENT_FLAGS',
            'PITCH_TRACK','SLIDE_NOTES','VOLUME_TRACK','VOLUME_SLIDES','NOISE_SLIDE_NOTES','VIBRATO','ARPEGGIO','RELEASE_NOTES','PHASE_RESET'])
    (build/'test_defs.inc').write_text('\n'.join(definitions)+'\n')
    run('ca65.exe',[build/'test_ca65.s','-I',engine,'-g','-o',build/'test_ca65.o'])
    run('ld65.exe',['-C',engine/'UnitTests/test_ca65.cfg','-o',build/'test_ca65.nes',build/'test_ca65.o'])
    run('asm6_fixed.exe',[build/'test_asm6.asm',build/'test_asm6.nes'])
    run('NESASM3.exe',[build/'test_nesasm.asm'])
    run('sdas6500.exe',['-pogn','-I'+str(engine),'-y','-s','-l',build/'test_sdas.o',build/'test_sdas.s'])
    run('sdld6808.exe',['-n','-i','-j','-y','-w','-u','-w','-b','_ZP=0x0000','-b','_BSS=0x300','-b','_CODE=0x8000',build/'test_sdas.ihx',build/'test_sdas.o'])
    run('makebin.exe',['-s','73728','-o','32752',build/'test_sdas.ihx',build/'test_sdas.nes'])
    reference=(build/'test_ca65.nes').read_bytes()
    for other in ['asm6','nesasm','sdas']:
        data=(build/f'test_{other}.nes').read_bytes()
        assert data==reference,(sweep,expansion,features,sfx,other,next((i for i,(a,b) in enumerate(zip(data,reference)) if a!=b),None))
    if not sweep and args.baseline:
        text=(build/'test_ca65.s').read_text().replace('"famistudio_ca65.s"','"../work/sweep-engine-tests/baseline-engine.s"')
        (build/'baseline.s').write_text(text)
        (build/'baseline-engine.s').write_bytes((args.baseline/'famistudio_ca65.s').read_bytes())
        run('ca65.exe',[build/'baseline.s','-I',engine,'-g','-o',build/'baseline.o'])
        run('ld65.exe',['-C',engine/'UnitTests/test_ca65.cfg','-o',build/'baseline.nes',build/'baseline.o'])
        assert (build/'baseline.nes').read_bytes()==reference,('disabled sweep changed legacy bytes',expansion,features,sfx)
    count+=1
    print('PASS',sweep,expansion,features,sfx,'four assemblers agree',flush=True)
print('PASS',count,'sweep configurations',flush=True)
