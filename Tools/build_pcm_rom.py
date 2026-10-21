"""Build mapper-5/24 PCM ROM templates, measuring linked MMC5 blanking paths."""
from pathlib import Path
import json
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / "Tests/Mmc5PcmNes"))
from generic_service_cycles import patch_padding, read_labels
out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)
romdir = root / "FamiStudio/Rom"
results = []
configurations = [("mmc5", 1, 1), ("mmc5", 2, 1), ("mmc5", 3, 1), ("apu", 1, 0)]
configurations += [("vrc6", mask, 1) for mask in (1, 4, 5, 8, 9, 12, 13)]
for family, mask, expansion in configurations:
    mapper5 = family == "mmc5"
    source = "rom_mmc5_pcm" if mapper5 else "rom_vrc6_pcm"
    for pal in (False, True):
        for tracker in (False, True):
            name = f"rom_{family}_pcm_m{mask}_{'pal' if pal else 'ntsc'}{'_famitracker' if tracker else ''}"
            definitions = [f"FAMISTUDIO_CFG_{'PAL' if pal else 'NTSC'}_SUPPORT=1",
                           f"PCM_DESTINATIONS={mask}"]
            if not mapper5:
                definitions += [f"PCM_EXPANSION={expansion}"]
            if tracker:
                definitions.append("FAMISTUDIO_USE_FAMITRACKER_TEMPO=1")
            commands = [
                [str(root / "Tools/ca65.exe"), source + ".s", "-g", "-o", str(out / (name + ".o")),
                 *sum((["-D", definition] for definition in definitions), [])],
                [str(root / "Tools/ld65.exe"), "-C", source + ".cfg", "-o", str(out / (name + ".nes")),
                 str(out / (name + ".o")), "--dbgfile", str(out / (name + ".dbg")),
                 "-m", str(out / (name + ".map")), "-Ln", str(out / (name + ".lbl"))],
            ]
            for command in commands:
                run = subprocess.run(command, cwd=romdir, capture_output=True, text=True)
                if run.returncode:
                    raise RuntimeError(" ".join(command) + "\n" + run.stdout + run.stderr)
            symbols = read_labels(out / (name + ".lbl"))
            data = bytearray((out / (name + ".nes")).read_bytes())
            timing = patch_padding(data, symbols, pal, mask) if mapper5 else None
            tables = []
            for line in (out / (name + ".dbg")).read_text().splitlines():
                if ("_note_table_" in line and "_slide_" not in line and "_exp_" not in line
                        and "addrsize=absolute" in line and "type=lab" in line):
                    label = re.search(r'name="([^"]+)"', line)[1]
                    address = int(re.search(r'val=(0x[0-9A-Fa-f]+)', line)[1], 16) - 0xe000
                    if address >= 0:
                        tables.append(f"{label}={address}")
            (out / (name + ".nes")).write_bytes(data)
            (romdir / (name + ".nes")).write_bytes(data)
            (romdir / (name + ".tbl")).write_text("\n".join(tables) + "\n")
            results.append(dict(name=name, bytes=len(data), timing=timing))
            print("PASS", name, flush=True)
(out / "build-report.json").write_text(json.dumps(dict(passed=True, results=results), indent=2) + "\n")
