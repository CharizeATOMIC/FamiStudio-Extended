"""Run independent PCM cartridge fixtures in isolated worker processes."""
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
import argparse
import json
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fixtures", type=Path)
    parser.add_argument("templates", type=Path)
    parser.add_argument("--py65", type=Path)
    parser.add_argument("--frames", type=int, default=3200)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--mapper", type=int, choices=(5, 24))
    parser.add_argument("--console-core", type=Path)
    parser.add_argument("--console-only", action="store_true")
    args = parser.parse_args()
    script = Path(__file__).with_name("verify.py")
    fixtures = sorted(args.fixtures.glob("*/fixture.json"))
    if args.mapper:
        def mapper(path):
            header = (path.parent / json.loads(path.read_text())["rom_filename"]).read_bytes()[:16]
            return (header[6] >> 4) | (header[7] & 240)
        fixtures = [p for p in fixtures if mapper(p) == args.mapper]

    def run(path):
        fixture = json.loads(path.read_text())
        folder = path.parent
        if args.console_only:
            assert args.console_core
            command = [sys.executable, str(script.with_name("verify_console.py")),
                       str(args.console_core), str(folder / fixture["rom_filename"]),
                       str(folder / "console"), str(args.templates / (fixture["template"] + ".lbl"))]
            log = folder / "console.log"
        else:
            command = [sys.executable, str(script), str(args.fixtures), str(args.templates),
                       "--variant", folder.name, "--frames", str(args.frames)]
            if args.py65:
                command += ["--py65", str(args.py65)]
            if args.console_core:
                command += ["--console-core", str(args.console_core)]
            log = folder / "cpu.log"
        with log.open("w") as stream:
            result = subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT)
        if result.returncode != 0:
            report = folder / ("console/console-verification.json" if args.console_only else "cycle-verification.json")
            report.parent.mkdir(parents=True, exist_ok=True)
            report.write_text(json.dumps(dict(passed=False, log=str(log), exit_code=result.returncode), indent=2))
        return dict(fixture=folder.name, passed=result.returncode == 0, exit_code=result.returncode, log=str(log))

    results = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        for future in as_completed([pool.submit(run, path) for path in fixtures]):
            result = future.result()
            results.append(result)
            print(json.dumps(result), flush=True)
    report = dict(passed=all(r["passed"] for r in results), fixtures=len(results),
                  physical_hardware=False, results=sorted(results, key=lambda r: r["fixture"]))
    suffix = "-console" if args.console_only else "-cpu"
    if args.mapper:
        suffix += f"-mapper{args.mapper}"
    (args.fixtures / ("matrix" + suffix + ".json")).write_text(json.dumps(report, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
