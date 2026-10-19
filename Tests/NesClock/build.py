"""Build three original-music mapper-69 cartridges: build.py <music directory> <output>.
Input music-{1,2,4}.s must be ca65 exports with identical 1x-authored song timing.
"""
from pathlib import Path
import re,shutil,subprocess,sys,json
repo=Path(__file__).resolve().parents[2]
source=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
font={}
for line in (repo/'SoundEngine/DemoSource/charmap.inc').read_text().splitlines():
 m=re.search(r"\.charmap\s+'(.)', \$(\w+)",line)
 if m:font[m[1]]=int(m[2],16)
# IRQ acceptance (7), register saves (13), JSR (6), stop/reload/start (55),
# plus mean instruction-boundary latency. verify.py measures the actual result.
overhead=82
for rate in (1,2,4):
 folder=out/f'{rate}x';folder.mkdir(exist_ok=True)
 music=(source/f'music-{rate}.s').read_text(encoding='utf-8-sig')
 label=re.search(r'^(music_data_\w+):',music,re.M).group(1)
 (folder/'music.s').write_text(music)
 for name in ('demo.s','demo.cfg'):shutil.copyfile(Path(__file__).parent/name,folder/name)
 (folder/'demo.s').write_text((folder/'demo.s').read_text().replace('MUSIC_DATA',label))
 shutil.copyfile(repo/'SoundEngine/DemoSource/demo.chr',folder/'demo.chr')
 periods=[(n+1)*59561//(2*rate)-n*59561//(2*rate) for n in range(8)]
 reloads=[p-overhead-1 for p in periods]
 for name,shift in [('reload-low.inc',0),('reload-high.inc',8)]:
  (folder/name).write_text('.byte '+','.join(str((n>>shift)&255) for n in reloads)+'\n')
 screen=bytearray([255]*960+[0]*64)
 for row,text in [(3,'FAMISTUDIO CLOCK TEST'),(6,f'{rate}X OUTPUT / 1X SONG'),(9,'FME7 CPU CYCLE IRQ'),(12,'SAME NOTES SAME DURATION'),(15,'REAL SPACED MUSIC UPDATES'),(19,'NTSC / MAPPER 69 / APU'),(23,'ORIGINAL SYNTHETIC PHRASE'),(26,'RESET TO RESTART')]:
  start=row*32+(32-len(text))//2
  for n,c in enumerate(text):screen[start+n]=font[c]
 (folder/'screen.bin').write_bytes(screen)
 for exe,args in [('ca65.exe',['demo.s','-I',str(repo/'SoundEngine'),'-g','-o','demo.o']),('ld65.exe',['-C','demo.cfg','-o',f'Clock-{rate}x.nes','demo.o','--dbgfile','demo.dbg','-m','demo.map','-Ln','demo.lbl'])]:
  p=subprocess.run([str(repo/'Tools'/exe),*args],cwd=folder,text=True,capture_output=True)
  if p.returncode:raise RuntimeError(p.stdout+p.stderr)
 print('PASS',rate,'x cartridge', (folder/f'Clock-{rate}x.nes').stat().st_size,'bytes',flush=True)
(out/'build-settings.json').write_text(json.dumps({'overhead_cycles':overhead,'base_period':29780.5,'mapper':69,'region':'NTSC'},indent=2))
