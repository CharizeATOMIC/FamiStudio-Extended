"""Rebuild embedded NES/FDS drivers with the fork's tuning opcode."""
from pathlib import Path
import re,subprocess
root=Path(__file__).resolve().parents[1];rom=root/'FamiStudio/Rom';build=root/'work/rom-build'
build.mkdir(parents=True,exist_ok=True)
variants=re.findall(r'CALL :CompileNsfPermutation (\S+), "([^"]+)", (\w+), (\w+)',(rom/'make.bat').read_text())
errors=[]
for cfg,options,name,ext in variants:
 obj=build/(name+'.o');binary=build/(name+'.'+ext);dbg=build/(name+'.dbg')
 result=subprocess.run([str(root/'Tools/ca65.exe'),'rom.s','-g','-o',str(obj),*options.split()],cwd=rom,capture_output=True,text=True)
 if not result.returncode:result=subprocess.run([str(root/'Tools/ld65.exe'),'-C',cfg,'-o',str(binary),str(obj),'--dbgfile',str(dbg)],cwd=rom,capture_output=True,text=True)
 if result.returncode:
  errors.append(name+'\n'+result.stderr);continue
 labels=[];base=0x6000-(0x10+0x38+0x02+0x10+0x01) if ext=='fds' else 0xe000
 for line in dbg.read_text().splitlines():
  if ('_note_table_' in line or '_FT2NoteTable' in line) and '_slide_' not in line and '_exp_' not in line and 'addrsize=absolute' in line and 'type=lab' in line:
   label=re.search(r'name="([^"]+)"',line)[1];addr=int(re.search(r'val=(0x[0-9A-Fa-f]+)',line)[1],16)-base
   if addr>=0:labels.append(f'{label}={addr}')
 (rom/(name+'.'+ext)).write_bytes(binary.read_bytes());(rom/(name+'.tbl')).write_text('\n'.join(labels)+'\n')
 print('PASS',name,flush=True)
if errors:raise RuntimeError('\n'.join(errors))
