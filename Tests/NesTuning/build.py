"""Build a hardware-compatible NROM demo from music exported by --nes-demo."""
from pathlib import Path
import subprocess, sys, shutil, re
repo = Path(__file__).resolve().parents[2]
out = Path(sys.argv[1]).resolve()
out.mkdir(parents=True, exist_ok=True)
font = {}
for line in (repo / 'SoundEngine/DemoSource/charmap.inc').read_text().splitlines():
    m = re.search(r"\.charmap\s+'(.)', \$(\w+)", line)
    if m: font[m[1]] = int(m[2], 16)
screen = bytearray([255] * 960 + [0] * 64)
for row, text in [(3, 'FAMISTUDIO TUNING'), (5, 'CARTRIDGE ENGINE TEST'), (8, 'NROM / NTSC / MONO'),
                  (12, 'STEP 1 OF 4'), (15, '1. A4 440 HZ PATTERN'), (17, '2. A4 432 HZ SONG'),
                  (19, '3. A4 450 HZ PATTERN'), (21, '4. C4 / A432 / UP 50 CENTS'), (25, 'AUTO LOOP / RESET TO RESTART')]:
    start = row * 32 + (32 - len(text)) // 2
    for i, char in enumerate(text): screen[start+i] = font[char]
(out / 'screen.bin').write_bytes(screen)
shutil.copyfile(repo / 'SoundEngine/DemoSource/demo.chr', out / 'demo.chr')
for name in ['demo.s', 'demo.cfg']: shutil.copyfile(Path(__file__).parent / name, out / name)
for exe, args in [
    ('ca65.exe', ['demo.s', '-I', str(repo / 'SoundEngine'), '-g', '-o', 'demo.o']),
    ('ld65.exe', ['-C', 'demo.cfg', '-o', 'TuningVerification.nes', 'demo.o', '--dbgfile', 'demo.dbg', '-m', 'demo.map'])]:
    p = subprocess.run([str(repo / 'Tools' / exe), *args], cwd=out, capture_output=True, text=True)
    if p.returncode: raise RuntimeError(p.stdout + p.stderr)
print('PASS NROM demo built', (out / 'TuningVerification.nes').stat().st_size, 'bytes')
