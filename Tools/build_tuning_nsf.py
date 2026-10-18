"""Rebuild embedded NSF kernels, failing on assembly/link errors. Requires Python 3 and bundled cc65."""
from pathlib import Path
import re, subprocess, sys

root=Path(__file__).resolve().parents[1]
nsf=root/'FamiStudio/Nsf'
build=root/'work/nsf-build'
build.mkdir(parents=True,exist_ok=True)
pattern=re.compile(r'CALL :CompileNsfPermutation nsf.cfg, "\{CODEBANKS\}=(\d+)", "([^"]+)", (\w+)')
variants=pattern.findall((nsf/'make.bat').read_text())
selected=sys.argv[1:]
for banks, options, name in variants:
 if selected and not any(s in name for s in selected):continue
 # The optional lookup code needs an extra code bank for several formerly full kernels.
 banks=int(banks)
 for trial in range(banks,5):
  cfg=build/(name+'.cfg');obj=build/(name+'.o');binary=build/(name+'.bin');dbg=build/(name+'.dbg')
  cfg.write_text((nsf/'nsf.cfg').read_text().replace('{CODEBANKS}',str(trial)))
  result=subprocess.run([str(root/'Tools/ca65.exe'),'nsf.s','-g','-o',str(obj),*options.split()],cwd=nsf,capture_output=True,text=True)
  if result.returncode:raise RuntimeError(name+'\n'+result.stderr)
  result=subprocess.run([str(root/'Tools/ld65.exe'),'-C',str(cfg),'-o',str(binary),str(obj),'--dbgfile',str(dbg)],cwd=nsf,capture_output=True,text=True)
  if result.returncode and ('overflow' in result.stderr and "'CODE'" in result.stderr):continue
  if result.returncode:raise RuntimeError(name+'\n'+result.stderr)
  break
 else:raise RuntimeError('Driver too large: '+name)
 labels=[]
 for line in dbg.read_text().splitlines():
  if ('_note_table_' in line or '_FT2NoteTable' in line) and '_slide_' not in line and '_exp_' not in line and 'addrsize=absolute' in line and 'type=lab' in line:
   label=re.search(r'name="([^"]+)"',line)[1]
   addr=int(re.search(r'val=(0x[0-9A-Fa-f]+)',line)[1],16)-(0x10000-trial*4096)
   if addr>=0:labels.append(f'{label}={addr}')
 (nsf/(name+'.bin')).write_bytes(binary.read_bytes())
 (nsf/(name+'.tbl')).write_text('\n'.join(labels)+'\n')
 print(f'{name}: {trial} banks',flush=True)
