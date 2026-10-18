"""Check exported tuning data with all four supported assembler syntaxes."""
from pathlib import Path
import subprocess, sys, shutil

repo = Path(__file__).resolve().parents[1]
source = Path(sys.argv[1]).resolve()
build = repo / 'work/export-assembly-tests'
build.mkdir(parents=True, exist_ok=True)
for fmt in ['ca65', 'asm6', 'nesasm', 'sdas']:
    shutil.copyfile(source / ('demo-' + fmt + '.s'), build / ('data-' + fmt + '.inc'))

def run(exe, *args):
    result = subprocess.run([str(repo / 'Tools' / exe), *args], cwd=build, capture_output=True, text=True)
    if result.returncode or 'error' in result.stderr.lower() or 'error' in result.stdout.lower():
        raise RuntimeError(exe + '\n' + result.stdout + result.stderr)

(build / 'ca65.s').write_text('.segment "CODE"\n.include "data-ca65.inc"\n')
(build / 'ca65.cfg').write_text('MEMORY { ROM: start=$8000, size=$8000, file=%O; }\nSEGMENTS { CODE: load=ROM, type=ro; }\n')
run('ca65.exe', 'ca65.s', '-o', 'ca65.o')
run('ld65.exe', '-C', 'ca65.cfg', '-o', 'ca65.bin', 'ca65.o')
(build / 'asm6.asm').write_text('org $8000\ninclude "data-asm6.inc"\n')
run('asm6_fixed.exe', 'asm6.asm', 'asm6.bin')
(build / 'nesasm.asm').write_text('  .bank 0\n  .org $8000\n  .include "data-nesasm.inc"\n')
run('NESASM3.exe', 'nesasm.asm')
(build / 'sdas.s').write_text('.area _CODE\n.include "data-sdas.inc"\n')
run('sdas6500.exe', '-pogn', '-s', '-l', 'sdas.o', 'sdas.s')
run('sdld6808.exe', '-n', '-i', '-j', '-y', '-w', '-u', '-b', '_CODE=0x8000', 'sdas.ihx', 'sdas.o')
run('makebin.exe', '-s', '65536', '-o', '32768', 'sdas.ihx', 'sdas.bin')
reference = (build / 'ca65.bin').read_bytes()
assert (build / 'asm6.bin').read_bytes() == reference
assert (build / 'sdas.bin').read_bytes()[:len(reference)] == reference
nes = (build / 'nesasm.nes').read_bytes()
if nes.startswith(b'NES\x1a'): nes = nes[16:]
assert nes[:len(reference)] == reference
print('PASS exported custom-tuning music assembles to identical bytes in CA65, ASM6, NESASM and SDAS')
