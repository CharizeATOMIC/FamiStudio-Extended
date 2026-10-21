"""Execute exported MMC5 cartridges, including mapper IRQs and DPCM byte DMA.

py65 executes the actual 6502 instructions. A cycle-counted PPU timing model
provides vblank/NMI and visible scanline IRQs; it does not render pixels.
Writes are timestamped on instruction completion. DPCM uses conservative
four-cycle byte-fetch stalls; this is not a transistor/bus-phase emulator.
"""
from pathlib import Path
import argparse
import hashlib
import json
import math
import statistics
import sys

parser = argparse.ArgumentParser()
parser.add_argument("output", type=Path)
parser.add_argument("templates", type=Path)
parser.add_argument("--py65", type=Path)
parser.add_argument("--frames", type=int, default=400)
parser.add_argument("--variant", choices=["ntsc-studio", "ntsc-tracker", "pal-studio", "pal-tracker"])
args = parser.parse_args()
if args.py65:
    sys.path.insert(0, str(args.py65))
from py65.devices.mpu6502 import MPU
from service_cycles import read_labels


class Machine:
    def __init__(self, rom, pal, dmc_stalls=True):
        self.prg = rom[16:16 + rom[4] * 16384]
        self.ram = bytearray(0x800)
        self.work = bytearray(0x2000)
        self.banks = [0, 1, 2, len(self.prg) // 8192 - 1]
        self.pal = pal
        self.dmc_stalls = dmc_stalls
        self.clock = 0
        self.pending = []
        self.apu = {}
        self.writes = []
        self.irq_enabled = False
        self.irq_pending = False
        self.irq_line = 0
        self.irq_edges = []
        self.nmi_pending = False
        self.nmi_enabled = False
        self.rendering = False
        self.vblank = False
        self.frame = 0
        self.line = 0
        self.frame_start = 0.0
        self.line_cycles = 341 * (5 / 16 if pal else 1 / 3)
        self.next_line = self.line_cycles
        self.next_nmi = None
        self.next_irq = None
        self.dma_next = None
        self.dma_remaining = 0
        self.dma_address = 0xc000
        self.dma_reads = []
        self.dma_cycles = 0
        self.controller = 0
        self.controller_bits = 0
        self.controller_strobe = False
        self.frames_started = []

    def __getitem__(self, address):
        if address < 0x2000:
            return self.ram[address & 2047]
        if address == 0x2002:
            result = 128 if self.vblank else 0
            self.vblank = False
            return result
        if address == 0x5204:
            result = (128 if self.irq_pending else 0) | (64 if self.line < 240 else 0)
            self.irq_pending = False
            return result
        if address == 0x4016:
            if self.controller_strobe:
                return (self.controller >> 7) & 1
            result = (self.controller_bits >> 7) & 1
            self.controller_bits = ((self.controller_bits << 1) | 1) & 255
            return result
        if 0x6000 <= address < 0x8000:
            return self.work[address - 0x6000]
        if address >= 0x8000:
            if address in (0xfffa, 0xfffb):
                self.irq_pending = False  # MMC5 recognizes NMI vector reads.
            bank = self.banks[(address - 0x8000) // 8192]
            return self.prg[(bank % (len(self.prg) // 8192)) * 8192 + (address & 8191)]
        return 0

    def __setitem__(self, address, value):
        if address < 0x2000:
            self.ram[address & 2047] = value
        elif 0x6000 <= address < 0x8000:
            self.work[address - 0x6000] = value
        else:
            self.pending.append((address, value))

    def dmc_period(self):
        rates = ([398, 354, 316, 298, 276, 236, 210, 198, 176, 148, 132, 118, 98, 78, 66, 50]
                 if self.pal else
                 [428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 85, 72, 54])
        return rates[self.apu.get(0x4010, 0) & 15] * 8

    def advance(self, cycles):
        end = self.clock + cycles
        stalls = 0
        while self.dma_next is not None and self.dma_next <= end:
            self.dma_reads.append((self.dma_next, self.dma_address, self[self.dma_address]))
            self.dma_address = (self.dma_address + 1) & 65535
            if self.dma_address == 0:
                self.dma_address = 0x8000
            self.dma_remaining -= 1
            if self.dmc_stalls:
                end += 4
                stalls += 4
                self.dma_cycles += 4
            self.dma_next += self.dmc_period()
            if self.dma_remaining == 0:
                if self.apu.get(0x4010, 0) & 64:
                    self.dma_remaining = self.apu.get(0x4013, 0) * 16 + 1
                    self.dma_address = 0xc000 + self.apu.get(0x4012, 0) * 64
                else:
                    self.dma_next = None
        while True:
            moment = min(self.next_line,
                         self.next_nmi if self.next_nmi is not None else float("inf"),
                         self.next_irq if self.next_irq is not None else float("inf"))
            if moment > end:
                break
            if moment == self.next_nmi:
                self.next_nmi = None
                self.vblank = True
                if self.nmi_enabled:
                    self.nmi_pending = True
                continue
            if moment == self.next_irq:
                self.next_irq = None
                if self.irq_enabled:
                    self.irq_pending = True
                    self.irq_edges.append(moment)
                continue
            self.line += 1
            if self.line == (312 if self.pal else 262):
                self.frame += 1
                self.line = 0
                self.frame_start = moment
                self.frames_started.append(moment)
                self.irq_pending = False
            if self.line == (311 if self.pal else 261):
                self.vblank = False
            if self.line == 241:
                self.next_nmi = moment + (5 / 16 if self.pal else 1 / 3)
            if self.line == self.irq_line and 0 < self.line < 240 and self.rendering:
                if self.irq_enabled:
                    self.next_irq = moment + (1.25 if self.pal else 4 / 3)
            # One PPU-dot odd-frame shortening on NTSC rendering frames.
            shortened = (not self.pal and self.rendering and self.line == 261 and self.frame & 1)
            self.next_line += self.line_cycles - (1 / 3 if shortened else 0)
        self.clock = end
        for address, value in self.pending:
            if address == 0x2000:
                if value & 128 and not self.nmi_enabled and self.vblank:
                    self.nmi_pending = True
                self.nmi_enabled = bool(value & 128)
            elif address == 0x2001:
                self.rendering = bool(value & 24)
            elif address == 0x5203:
                self.irq_line = value
            elif address == 0x5204:
                self.irq_enabled = bool(value & 128)
                if not self.irq_enabled:
                    self.irq_pending = False
            elif 0x5114 <= address <= 0x5117:
                self.banks[address - 0x5114] = value & 127
            elif address == 0x4016:
                self.controller_strobe = bool(value & 1)
                if self.controller_strobe:
                    self.controller_bits = self.controller
            elif 0x4000 <= address <= 0x5015:
                self.apu[address] = value
                self.writes.append((self.clock, address, value))
                if address == 0x4015:
                    if not value & 16:
                        self.dma_remaining = 0
                        self.dma_next = None
                    elif self.dma_remaining == 0:
                        self.dma_address = 0xc000 + self.apu.get(0x4012, 0) * 64
                        self.dma_remaining = self.apu.get(0x4013, 0) * 16 + 1
                        self.dma_next = self.clock + 4
        self.pending.clear()
        return stalls


def execute(folder, labels, stalls):
    fixture = json.loads((folder / "fixture.json").read_text())
    rom = (folder / "MMC5-PCM.nes").read_bytes()
    memory = Machine(rom, fixture["pal"], stalls)
    pcm_payload = (folder / "pcm-data.bin").read_bytes()
    first_pcm = memory[labels["first_pcm_bank"]]
    assert memory.prg[first_pcm * 8192:first_pcm * 8192 + len(pcm_payload)] == pcm_payload
    cpu = MPU(memory=memory, pc=None)
    updates = []
    service_times = []
    service_durations = []
    irq_durations = []
    nmi_durations = []
    update_durations = []
    frames = {}
    expected_writes = []
    mismatches = []
    active_service = None
    update_start = None
    interrupt_stack = []
    update_registers = []
    count_at_update = []
    startup = None
    instructions = 0
    selection_test = False
    selection_done = False
    first_song = None
    end_clock = 0
    occupied = dict(irq=0, nmi=0, music=0)
    song_ticks = {}
    score_checks = 0
    score_errors = []
    trigger_checks = 0
    selections = []
    frame_occupied = {}
    while len(updates) < args.frames:
        if memory.nmi_pending:
            memory.nmi_pending = False
            before = cpu.processorCycles
            cpu.nmi()
            cpu.processorCycles += memory.advance(cpu.processorCycles - before)
            if startup is not None:
                occupied["nmi"] += cpu.processorCycles - before
                frame_occupied[memory.frame] = frame_occupied.get(memory.frame, 0) + cpu.processorCycles - before
            assert not any(kind == "nmi" for kind, _ in interrupt_stack), "nested NMI"
            interrupt_stack.append(("nmi", memory.clock - 7))
        elif memory.irq_pending and not cpu.p & cpu.INTERRUPT:
            before = cpu.processorCycles
            cpu.irq()
            cpu.processorCycles += memory.advance(cpu.processorCycles - before)
            if startup is not None:
                occupied["irq"] += cpu.processorCycles - before
                frame_occupied[memory.frame] = frame_occupied.get(memory.frame, 0) + cpu.processorCycles - before
            assert not any(kind == "irq" for kind, _ in interrupt_stack), "nested MMC5 IRQ"
            interrupt_stack.append(("irq", memory.clock - 7))
        pc = cpu.pc
        if pc == labels["select_song"]:
            selections.append(memory.frame)
        if pc == labels["famistudio_pcm_service"]:
            assert active_service is None
            active_service = memory.clock
            path = "visible" if memory.line < 241 else "blank"
            if memory.rendering:
                service_times.append((memory.clock, path, memory.frame, memory.line))
                frames.setdefault(memory.frame, []).append(memory.line)
            pending = memory[labels["famistudio_pcm_pending"]]
            enabled = memory[labels["famistudio_pcm_active"]]
            offset = memory[labels["famistudio_pcm_active_offset"]]
            if pending == 128:
                enabled = False
            elif pending != 255:
                enabled, offset = True, pending
                song = memory[labels["song_index"]]
                tick = song_ticks.get(song, 0) + int(update_start is not None)
                track = fixture["tracks"][song]
                source_tick = tick
                if tick >= track["end"] and track["loop"] >= 0:
                    source_tick = track["loop"] + (tick - track["end"]) % (track["end"] - track["loop"])
                events = [event for event in track["events"]
                          if event["tick"] == source_tick and event["command"] == 1]
                assert len(events) == 1, ("PCM attack at wrong score tick", song, tick, source_tick, events)
                sample = fixture["samples"][events[0]["sample"]]
                state = labels["famistudio_pcm_sample_ptr"] + offset
                address = memory[state] | (memory[state + 1] << 8)
                bank = memory[state + 4]
                count = memory[state + 2] | (memory[state + 3] << 8)
                assert bank * 8192 + address - 0x8000 == sample["offset"]
                assert count == sample["length"] and bool(memory[state + 5]) == sample["loop"]
                trigger_checks += 1
            pointer = labels["famistudio_pcm_sample_ptr"] + offset
            remain = memory[pointer + 2] | (memory[pointer + 3] << 8)
            index = 0
            if enabled and remain == 0:
                if memory[pointer + 5]:
                    index = 6
                else:
                    enabled = False
            value = 128
            if enabled and not memory[labels["famistudio_song_speed"]] & 128:
                address = memory[pointer + index] | (memory[pointer + index + 1] << 8)
                bank = memory[pointer + (10 if index else 4)]
                physical = memory[labels["first_pcm_bank"]] + bank
                raw = memory.prg[physical * 8192 + address - 0x8000]
                volume = memory[labels["famistudio_pcm_table_ptr"] + 1] - 0x60
                value = max(1, 128 + math.trunc((raw - 128) * volume / 15))
            expected_writes.append(value)
        if pc in (labels.get("sample_return"), labels.get("nmi_sample_return")) and active_service is not None:
            service_durations.append(memory.clock - active_service)
            actual = memory.apu.get(0x5011)
            expected = expected_writes[-1]
            if actual != expected:
                mismatches.append((memory.clock, actual, expected))
            active_service = None
        if pc == labels["music_call"]:
            update_start = memory.clock
        if pc == labels["music_return"] and update_start is not None:
            updates.append(memory.clock)
            update_durations.append(memory.clock - update_start)
            update_start = None
            update_registers.append(dict(memory.apu))
            count_at_update.append(len(service_times))
            song = memory[labels["song_index"]]
            song_ticks[song] = song_ticks.get(song, 0) + 1
            editor_frames = fixture["editor"][song]["registers"]
            reference = {int(k): v for k, v in editor_frames[song_ticks[song] % len(editor_frames)].items()}
            for control, low, high in ((0x4000, 0x4002, 0x4003), (0x5000, 0x5002, 0x5003)):
                volume = reference.get(control, 0) & 15
                actual_volume = memory.apu.get(control, 0) & 15
                score_checks += 1
                if volume != actual_volume:
                    score_errors.append((song, song_ticks[song], hex(control), actual_volume, volume))
                if volume:
                    expected = reference[low] | ((reference[high] & 7) << 8)
                    actual = memory.apu.get(low, 0) | ((memory.apu.get(high, 0) & 7) << 8)
                    score_checks += 1
                    if actual != expected:
                        score_errors.append((song, song_ticks[song], hex(low), actual, expected))
            if startup is None:
                startup = memory.clock
                first_song = memory[labels["song_index"]]
            if len(updates) == args.frames // 2:
                memory.controller = 1
                selection_test = True
            if selection_test and memory[labels["song_index"]] != first_song:
                selection_done = True
                memory.controller = 0
        opcode = memory[cpu.pc]
        before = cpu.processorCycles
        category = interrupt_stack[-1][0] if interrupt_stack else ("music" if update_start is not None else None)
        cpu.step()
        cpu.processorCycles += memory.advance(cpu.processorCycles - before)
        if category is not None and startup is not None:
            amount = cpu.processorCycles - before
            occupied[category] += amount
            frame_occupied[memory.frame] = frame_occupied.get(memory.frame, 0) + amount
        if opcode == 0x40:
            kind, start = interrupt_stack.pop()
            if kind == "irq":
                irq_durations.append(memory.clock - start)
            else:
                nmi_durations.append(memory.clock - start)
        instructions += 1
        if instructions > 30000000:
            raise AssertionError(f"cartridge stalled at {cpu.pc:04x}, {len(updates)} updates")
        end_clock = memory.clock
    assert not mismatches, mismatches[:20]
    assert not score_errors, score_errors[:20]
    assert selection_done, "controller did not select the second song"
    assert memory.dma_reads, "DPCM was not exercised"
    assert service_times and service_durations
    # Only complete ordinary frames; exclude the song-switch frame, which
    # deliberately stops sound while drawing the static screen.
    full = {frame: lines for frame, lines in frames.items()
            if len(lines) in ((78,) if fixture["pal"] else (65, 66))}
    partial = {frame: len(lines) for frame, lines in frames.items() if frame not in full}
    permitted = {max(frames), min(frames)}
    for selection in selections:
        permitted.update((selection - 1, selection, selection + 1))
    assert all(frame in permitted for frame in partial), ("lost or duplicate PCM samples", partial)
    assert abs(len(full) - args.frames) <= 3, ("music missed a video frame",
        len(full), args.frames, {f: len(x) for f, x in frames.items() if f not in full})
    intervals = [b[0] - a[0] for a, b in zip(service_times, service_times[1:])
                 if b[0] - a[0] < 2000]
    crossing = [b[0] - a[0] for a, b in zip(service_times, service_times[1:])
                if b[2] != a[2] and b[0] - a[0] < 2000]
    writes = [t for t, reg, value in memory.writes if reg == 0x5011 and t >= startup]
    dac_intervals = [b - a for a, b in zip(writes, writes[1:]) if b - a < 2000]
    result = dict(
        pal=fixture["pal"], tracker=fixture["tracker"], dpcm_dma_stalls=stalls,
        instructions=instructions, music_updates=len(updates),
        full_frames=len(full), samples_per_frame=sorted(set(map(len, full.values()))),
        partial_frames=partial,
        pcm_samples_checked=len(service_durations), pcm_value_mismatches=len(mismatches),
        independent_editor_score_checks=score_checks, editor_score_mismatches=len(score_errors),
        independent_pcm_trigger_and_descriptor_checks=trigger_checks,
        sample_entry_interval_min=min(intervals), sample_entry_interval_max=max(intervals),
        cross_frame_entry_interval_min=min(crossing), cross_frame_entry_interval_max=max(crossing),
        dac_write_interval_min=min(dac_intervals), dac_write_interval_max=max(dac_intervals),
        visible_irq_cpu_cycles_max=max(v for v in irq_durations if v < 1000),
        irq_with_deferred_blanking_cycles_max=max(irq_durations),
        deferred_blank_interrupts=sum(v >= 1000 for v in irq_durations),
        nmi_cpu_cycles_max=max(nmi_durations),
        music_update_wall_cycles_max=max(update_durations),
        occupied_cycles=occupied,
        music_and_pcm_cpu_fraction=sum(occupied.values()) / (end_clock - startup),
        worst_complete_frame_music_and_pcm_cpu_fraction=max(frame_occupied.get(frame, 0) for frame in full) /
            (33247.5 if fixture["pal"] else 29780.5),
        dma_bytes=len(memory.dma_reads), dma_stolen_cycles=memory.dma_cycles,
        two_song_selection_passed=selection_done, sha256=hashlib.sha256(rom).hexdigest())
    print(json.dumps(result), flush=True)
    return result, update_registers, count_at_update


results = []
for pal in (False, True):
    for tracker in (False, True):
        stem = ("pal" if pal else "ntsc") + ("-tracker" if tracker else "-studio")
        if args.variant and args.variant != stem:
            continue
        template = f"rom_mmc5_pcm_{'pal' if pal else 'ntsc'}{'_famitracker' if tracker else ''}"
        labels = read_labels(args.templates / (template + ".lbl"))
        baseline = None
        for stalls in (False, True):
            result, registers, counts = execute(args.output / stem, labels, stalls)
            if baseline is None:
                baseline = registers
            else:
                audible = list(range(0x4000, 0x4010)) + [0x5000, 0x5002, 0x5003, 0x5004, 0x5006, 0x5007]
                errors = [(i, hex(address), frame.get(address), reference.get(address))
                          for i, (frame, reference) in enumerate(zip(registers, baseline))
                          for address in audible if frame.get(address) != reference.get(address)]
                assert not errors, errors[:20]
                result["dpcm_preserves_other_channel_score_states"] = True
            results.append(result)
        (args.output / "cycle-verification.json").write_text(json.dumps(dict(
            passed=True, physical_hardware=False,
            scope="Actual 6502 code, mapper-5 PRG/scanline IRQ, vblank NMI and conservative DPCM byte DMA. No pixel renderer or transistor-level bus-phase emulation.",
            results=results), indent=2) + "\n")
print("PASS all MMC5 PCM NES timing, sample, DPCM and song-selection checks")
