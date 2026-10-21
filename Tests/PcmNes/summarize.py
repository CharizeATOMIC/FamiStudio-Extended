"""Collect only successful PCM cartridge proofs matching the current ROM bytes."""
from pathlib import Path
import hashlib
import json
import sys


def main():
    root, destination = map(Path, sys.argv[1:3])
    results = []
    failures = []
    for path in sorted(root.glob("*/fixture.json")):
        fixture = json.loads(path.read_text())
        folder = path.parent
        rom = folder / fixture["rom_filename"]
        digest = hashlib.sha256(rom.read_bytes()).hexdigest()
        cpu_path = folder / "cycle-verification.json"
        console_path = folder / "console/console-verification.json"
        try:
            cpu = json.loads(cpu_path.read_text())
            console = json.loads(console_path.read_text())
            assert cpu["passed"] and console["passed"], "A verification failed."
            assert len(cpu["results"]) == 2, "Both DMA timing modes are required."
            assert {r["dpcm_dma_stalls"] for r in cpu["results"]} == {False, True}
            assert all(r["rom_sha256"] == digest for r in cpu["results"])
            assert console["rom_sha256"] == digest, "Console report tests an older ROM."
            assert all(r["music_updates"] >= 3200 for r in cpu["results"])
            results.append(dict(fixture=folder.name, rom_sha256=digest,
                cpu=cpu["results"], console=console,
                cpu_report=str(cpu_path), console_report=str(console_path)))
        except (OSError, KeyError, AssertionError) as error:
            failures.append(dict(fixture=folder.name, error=str(error)))
    cpu = [run for fixture in results for run in fixture["cpu"]]
    groups = {}
    for run in cpu:
        key = f"mapper{run['mapper']}_{'pal' if run['pal'] else 'ntsc'}"
        groups.setdefault(key, []).append(run)
    grouped_timing = {}
    for key, runs in groups.items():
        destinations = {}
        for voice in ("apu", "mmc5", "vrc6_1", "vrc6_2"):
            values = [run["dac_write_intervals"][voice] for run in runs
                      if voice in run["dac_write_intervals"]]
            if values:
                destinations[voice] = dict(min=min(v["min"] for v in values),
                                           max=max(v["max"] for v in values))
        grouped_timing[key] = dict(runs=len(runs),
            cpu_fraction_mean=sum(run["cpu_fraction"] for run in runs) / len(runs),
            cpu_fraction_min=min(run["cpu_fraction"] for run in runs),
            cpu_fraction_max=max(run["cpu_fraction"] for run in runs),
            worst_frame_cpu_fraction_max=max(run["worst_complete_frame_cpu_fraction"] for run in runs),
            music_wall_cycles_max=max(run["music_wall_cycles_max"] for run in runs),
            sample_callback_interval=dict(
                min=min(run["sample_entry_interval"]["min"] for run in runs),
                max=max(run["sample_entry_interval"]["max"] for run in runs)),
            cross_frame_callback_interval=dict(
                min=min(run["cross_frame_entry_interval"]["min"] for run in runs),
                max=max(run["cross_frame_entry_interval"]["max"] for run in runs)),
            dac_write_intervals_including_control_writes=destinations)
    report = dict(passed=bool(results) and not failures, physical_hardware=False,
        fixture_count=len(results), failed_fixtures=failures, cpu_runs=len(cpu),
        music_updates=sum(r["music_updates"] for r in cpu),
        exact_pcm_values=sum(r["pcm_values_checked"] for r in cpu),
        independent_sample_cursor_checks=sum(r["independent_sample_cursor_checks"] for r in cpu),
        editor_score_checks=sum(r["independent_editor_score_checks"] for r in cpu),
        independent_pcm_trigger_checks=sum(r["independent_pcm_trigger_checks"] for r in cpu),
        consecutive_nmi_clock_checks=sum(r["consecutive_nmi_music_clock_checks"] for r in cpu),
        complete_console_video_frames=sum(r["console"]["video_frames"] for r in results),
        timing_by_mapper_and_region=grouped_timing,
        timing_notes="CPU fractions cover music/IRQ/NMI instruction work, excluding idle polling. Callback intervals exclude deliberate song-selection initialization pauses. DAC intervals include immediate stop/handoff control writes, not only regular sample writes.",
        tests_directory=str(Path(__file__).resolve().parent),
        scope="Synthetic exported cartridges executed by an independent 6502/mapper timing model and by the Mesen libretro console emulator. Physical hardware and exact DPCM bus alignment are not claimed.",
        fixtures=results)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({k: v for k, v in report.items() if k != "fixtures"}, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
