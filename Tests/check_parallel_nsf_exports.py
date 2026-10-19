"""Exercise concurrent desktop processes exporting and executing distinct NSFs.

Usage: python Tests/check_parallel_nsf_exports.py <dotnet> <harness.dll> <output>
The harness must already be built. Every child keeps its own output and log.
"""
from pathlib import Path
import subprocess
import sys

dotnet, harness, output = map(lambda p: Path(p).resolve(), sys.argv[1:4])
repo = Path(__file__).resolve().parents[1]
output.mkdir(parents=True, exist_ok=True)
children = []
for i, mode in enumerate(["apu-instrument-export", "chip-curves"] * 3):
    log = (output / f"{i}-{mode}.log").open("w", encoding="utf-8")
    process = subprocess.Popen(
        [str(dotnet), str(harness), "--" + mode, str(output / str(i))],
        cwd=repo, stdout=log, stderr=subprocess.STDOUT)
    children.append((i, mode, process, log))
errors = []
for i, mode, process, log in children:
    code = process.wait()
    log.close()
    if code:
        errors.append(f"process {i} ({mode}) exited {code}")
    else:
        print(f"PASS concurrent process {i}: {mode}", flush=True)
if errors:
    raise RuntimeError("; ".join(errors))
nsfs = [nsf for i in range(6) for nsf in (output / str(i)).rglob("*.nsf")]
assert len(nsfs) >= 100, f"Expected independent outputs, found {len(nsfs)}"
print(f"PASS six simultaneous processes, {len(nsfs)} NSF files with CPU validation")
