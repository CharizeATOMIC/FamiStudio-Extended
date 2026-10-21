"""Independent 6502 execution of mapper-5/24 PCM cartridges.

The CPU executes the actual exported ROM. The timing model extends the earlier
MMC5 regression with a VRC IRQ counter clocked continuously by its 341/3 CPU
prescaler (also on PAL). Writes are timestamped at instruction completion and
DPCM byte reads conservatively steal four CPU cycles. Pixel rendering, exact
DMA bus alignment, and physical hardware are outside this test's scope.

VRC IRQ register/phase cross-check:
https://www.nesdev.org/wiki/VRC_IRQ
https://github.com/libretro/libretro-fceumm/blob/master/src/boards/vrc6.c
No source from an external emulator is incorporated here.
"""
from pathlib import Path
import argparse
import bisect
import hashlib
import json
import math
import re
import statistics
import subprocess
import sys


def read_labels(path):
    return {m[2]: int(m[1], 16) for line in path.read_text().splitlines()
            if (m := re.match(r"al (\w+) \.(.+)", line))}


class Machine:
    def __init__(self, rom, pal, dmc_stalls=True):
        self.mapper = (rom[6] >> 4) | (rom[7] & 0xf0)
        assert self.mapper in (5, 24), self.mapper
        self.prg = rom[16:16 + rom[4] * 16384]
        assert self.mapper != 24 or len(self.prg) <= 256 * 1024, "VRC6 PRG bank registers address at most 256 KiB"
        self.ram = bytearray(0x800)
        self.work = bytearray(0x2000)
        self.prg_ram_enabled = self.mapper == 5
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
        self.vrc_latch = 0
        self.vrc_count = 0
        self.vrc_ack_enable = False
        self.vrc_cycle_mode = False
        self.next_vrc_tick = None
        self.irq_overflows_while_asserted = 0
        self.mapper_writes = []

    def __getitem__(self, address):
        if address < 0x2000:
            return self.ram[address & 2047]
        if address == 0x2002:
            result = 128 if self.vblank else 0
            self.vblank = False
            return result
        if self.mapper == 5 and address == 0x5204:
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
            assert self.prg_ram_enabled, "VRC6 work RAM was read before B003 bit 7 enabled it"
            return self.work[address - 0x6000]
        if address >= 0x8000:
            if self.mapper == 5 and address in (0xfffa, 0xfffb):
                self.irq_pending = False  # MMC5 recognizes NMI vector reads.
            bank = self.banks[(address - 0x8000) // 8192]
            return self.prg[(bank % (len(self.prg) // 8192)) * 8192 + (address & 8191)]
        return 0

    def __setitem__(self, address, value):
        if address < 0x2000:
            self.ram[address & 2047] = value
        elif 0x6000 <= address < 0x8000:
            assert self.prg_ram_enabled, "VRC6 work RAM was written before B003 bit 7 enabled it"
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
                         self.next_irq if self.next_irq is not None else float("inf"),
                         math.ceil(self.next_vrc_tick) if self.next_vrc_tick is not None else float("inf"))
            if moment > end:
                break
            if self.next_vrc_tick is not None and moment == math.ceil(self.next_vrc_tick):
                self.next_vrc_tick += 1 if self.vrc_cycle_mode else 341 / 3
                if self.vrc_count == 255:
                    self.vrc_count = self.vrc_latch
                    self.irq_overflows_while_asserted += int(self.irq_pending)
                    self.irq_pending = True
                    self.irq_edges.append(moment)
                else:
                    self.vrc_count += 1
                continue
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
                if self.mapper == 5:
                    self.irq_pending = False
            if self.line == (311 if self.pal else 261):
                self.vblank = False
            if self.line == 241:
                self.next_nmi = moment + (5 / 16 if self.pal else 1 / 3)
            if self.mapper == 5 and self.line == self.irq_line and 0 < self.line < 240 and self.rendering:
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
            elif self.mapper == 5 and address == 0x5203:
                self.irq_line = value
                self.mapper_writes.append((self.clock, address, value))
            elif self.mapper == 5 and address == 0x5204:
                self.irq_enabled = bool(value & 128)
                self.mapper_writes.append((self.clock, address, value))
                if not self.irq_enabled:
                    self.irq_pending = False
            elif self.mapper == 5 and 0x5114 <= address <= 0x5117:
                self.banks[address - 0x5114] = value & 127
            elif self.mapper == 24 and address == 0x8000:
                self.banks[0:2] = [(value & 15) * 2, (value & 15) * 2 + 1]
            elif self.mapper == 24 and address == 0xc000:
                self.banks[2] = value & 31
            elif self.mapper == 24 and address == 0xb003:
                self.prg_ram_enabled = bool(value & 128)
            elif self.mapper == 24 and address == 0xf000:
                self.vrc_latch = value
                self.irq_pending = False
                self.mapper_writes.append((self.clock, address, value))
            elif self.mapper == 24 and address == 0xf001:
                self.irq_pending = False
                self.irq_enabled = bool(value & 2)
                self.vrc_ack_enable = bool(value & 1)
                self.vrc_cycle_mode = bool(value & 4)
                if self.irq_enabled:
                    self.vrc_count = self.vrc_latch
                    self.next_vrc_tick = self.clock + (1 if self.vrc_cycle_mode else 341 / 3)
                else:
                    self.next_vrc_tick = None
                self.mapper_writes.append((self.clock, address, value))
            elif self.mapper == 24 and address == 0xf002:
                self.irq_pending = False
                self.irq_enabled = self.vrc_ack_enable
                if not self.irq_enabled:
                    self.next_vrc_tick = None
                self.mapper_writes.append((self.clock, address, value))
            elif address == 0x4016:
                self.controller_strobe = bool(value & 1)
                if self.controller_strobe:
                    self.controller_bits = self.controller
            elif 0x4000 <= address <= 0x5015 or (self.mapper == 24 and address in (0x9000, 0x9001, 0x9002, 0x9003, 0xa000, 0xa001, 0xa002, 0xb000, 0xb001, 0xb002)):
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


DAC_REGISTERS = (0x4011, 0x5011, 0x9000, 0xa000)
VOICE_NAMES = ("apu", "mmc5", "vrc6_1", "vrc6_2")


def source_tick(track, tick):
    if tick >= track["end"] and track["loop"] >= 0:
        return track["loop"] + (tick - track["end"]) % (track["end"] - track["loop"])
    return tick


def scaled_dac(raw, volume, destination):
    # The unsigned 8-bit source first receives signed gain around its midpoint;
    # only then does each physical DAC quantize to its own bit depth.
    scaled = 128 + math.trunc((raw - 128) * volume / 15)
    return scaled >> 1 if destination == 0 else max(1, scaled) if destination == 1 else (scaled >> 4) | 128


def execute(folder, labels, stalls, frames_requested, MPU, profile=False):
    fixture = json.loads((folder / "fixture.json").read_text())
    rom_path = folder / fixture.get("rom_filename", "")
    if not rom_path.is_file():
        candidates = [p for p in folder.glob("*.nes") if "unsupported" not in p.name]
        assert len(candidates) == 1, candidates
        rom_path = candidates[0]
    rom = rom_path.read_bytes()
    memory = Machine(rom, fixture["pal"], stalls)
    bank_size = fixture.get("bank_size", 16384 if memory.mapper == 24 else 8192)
    payload = (folder / "pcm-data.bin").read_bytes()
    # The TOC stores physical 8 KiB PRG banks on both mapper types.
    first_pcm = memory[labels["first_pcm_bank"]]
    assert memory.prg[first_pcm * 8192:first_pcm * 8192 + len(payload)] == payload
    cpu = MPU(memory=memory, pc=None)
    updates, service_times, service_durations = [], [], []
    nmi_entries = []
    irq_durations, nmi_durations, update_durations = [], [], []
    update_details = []
    music_profile = {}
    profile_labels = sorted((value, name) for name, value in labels.items() if name.startswith("famistudio_") and value >= 0x8000)
    profile_addresses = [value for value, name in profile_labels]
    interrupt_stack, selections, snapshots = [], [], []
    selection_frames = set()
    selection_start = None
    previous_music_clock = None
    music_clock_checks = 0
    song_ticks, frame_occupied = {}, {}
    occupied = dict(irq=0, nmi=0, music=0)
    voices = [None] * 4
    updates_by_frame = {}
    expected = None
    service_start = service_return = update_start = startup = None
    first_song = None
    selection_requested = selection_done = False
    score_checks = trigger_checks = sample_checks = sequence_checks = 0
    per_voice_checks = [0] * 4
    errors = []
    instructions = 0

    def expected_samples():
        nonlocal trigger_checks, sequence_checks
        result = {}
        for destination, name in enumerate(VOICE_NAMES):
            if "famistudio_pcm_service_" + name not in labels:
                continue
            if not memory[labels["famistudio_pcm_owns"] + destination]:
                voices[destination] = None
                continue
            pending = memory[labels["famistudio_pcm_pending"] + destination]
            offset = memory[labels["famistudio_pcm_active_offset"] + destination]
            if pending not in (128, 255):
                song = memory[labels["song_index"]]
                tick = song_ticks.get(song, 0) + int(update_start is not None)
                track = fixture["tracks"][song]
                current_tick = source_tick(track, tick)
                events = [event for event in track["events"]
                          if event["tick"] == current_tick and event["command"] == 1 and
                          event.get("destination", 1) == destination]
                assert len(events) == 1, ("attack at wrong score tick", song, tick, current_tick, destination, events)
                sample = fixture["samples"][events[0]["sample"]]
                voices[destination] = dict(sample=sample, cursor=0, active=True)
                offset = pending
                pointer = labels["famistudio_pcm_sample_ptr"] + offset
                address = memory[pointer] | memory[pointer + 1] << 8
                bank = memory[pointer + 4]
                count = memory[pointer + 2] | memory[pointer + 3] << 8
                assert bank * bank_size + address - 0x8000 == sample["offset"], (
                    "descriptor offset", destination, bank, address, sample)
                assert count == sample["length"] and bool(memory[pointer + 5]) == sample["loop"]
                trigger_checks += 1
            state = voices[destination]
            paused = bool(memory[labels["famistudio_song_speed"]] & 128)
            if state and state["active"] and pending == 255 and not memory[labels["famistudio_pcm_active"] + destination]:
                # Cartridge exports stop synchronously in the music command;
                # the subsequent sample IRQ must not replay the previous byte.
                song = memory[labels["song_index"]]
                tick = song_ticks.get(song, 0) + int(update_start is not None)
                events = [e for e in fixture["tracks"][song]["events"]
                          if e["tick"] == source_tick(fixture["tracks"][song], tick)
                          and e["command"] == 2 and e.get("destination", 1) == destination]
                assert events, ("PCM stopped without a source stop", song, tick, destination)
                assert memory.apu.get(DAC_REGISTERS[destination]) == scaled_dac(128, 15, destination)
                state["active"] = False
                trigger_checks += 1
            # A completed or stopped voice leaves the DAC at its midpoint.
            # Later idle callbacks deliberately do not keep rewriting the DAC.
            if not paused and pending != 128 and (state is None or not state["active"]):
                continue
            if pending == 128 and state:
                state["active"] = False
            raw = 128
            if state and state["active"] and not paused:
                sample = state["sample"]
                if state["cursor"] == sample["length"]:
                    if sample["loop"]:
                        state["cursor"] = 0
                    else:
                        state["active"] = False
                if state["active"]:
                    pointer = labels["famistudio_pcm_sample_ptr"] + offset
                    remaining = memory[pointer + 2] | memory[pointer + 3] << 8
                    # The engine reloads an exhausted loop at the start of this
                    # call. Validate either the live pointer or loop descriptor.
                    index = 6 if remaining == 0 else 0
                    address = memory[pointer + index] | memory[pointer + index + 1] << 8
                    bank = memory[pointer + (10 if index else 4)]
                    position = bank * bank_size + address - 0x8000
                    assert position == sample["offset"] + state["cursor"], (
                        "sample cursor skipped/repeated a byte", destination, position, state)
                    raw = payload[sample["offset"] + state["cursor"]]
                    state["cursor"] += 1
                    sequence_checks += 1
            volume = memory[labels["famistudio_pcm_table_ptr"] + destination * 2 + 1] - 0x60
            assert 0 <= volume <= 15, ("invalid PCM gain page", destination, volume)
            result[DAC_REGISTERS[destination]] = (scaled_dac(raw, volume, destination), destination)
        return result

    while len(updates) < frames_requested:
        if memory.nmi_pending:
            nmi_entries.append(dict(clock=memory.clock, frame=memory.frame, pc=hex(cpu.pc),
                irq_busy=memory[labels["irq_busy"]] if "irq_busy" in labels else 0,
                blank_deferred=memory[labels["blank_deferred"]] if "blank_deferred" in labels else 0))
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
            assert not any(kind == "irq" for kind, _ in interrupt_stack), "nested mapper IRQ"
            interrupt_stack.append(("irq", memory.clock - 7))
        pc = cpu.pc
        if pc == labels["select_song"]:
            selections.append(memory.frame)
            selection_start = memory.frame
            previous_music_clock = None
            voices = [None] * 4
            song_ticks[memory[labels["song_index"]]] = 0
        if pc == labels["famistudio_pcm_service"]:
            assert service_start is None, "sample feeder reentered"
            service_start = memory.clock
            service_return = (memory[0x100 + ((cpu.sp + 1) & 255)] |
                              memory[0x100 + ((cpu.sp + 2) & 255)] << 8) + 1
            expected = expected_samples()
            write_start = len(memory.writes)
            if memory.rendering:
                service_times.append((memory.clock, memory.frame, memory.line))
        if service_start is not None and pc == service_return:
            writes = [(register, value) for _, register, value in memory.writes[write_start:]
                      if register in DAC_REGISTERS]
            assert len(writes) == len(expected), ("wrong DAC write count", writes, expected,
                song_ticks, memory.frame, voices,
                [memory[labels["famistudio_pcm_active"] + d] for d in range(4)],
                [memory[labels["famistudio_pcm_pending"] + d] for d in range(4)])
            for register, value in writes:
                wanted, destination = expected[register]
                sample_checks += 1
                per_voice_checks[destination] += 1
                if value != wanted:
                    errors.append(("DAC", memory.clock, hex(register), value, wanted))
            service_durations.append(memory.clock - service_start)
            service_start = service_return = None
        if pc == labels["music_call"]:
            if selection_start is not None:
                selection_frames.update(range(selection_start, memory.frame + 1))
                selection_start = None
            current_clock = memory[labels["frame_seen"]]
            if previous_music_clock is not None:
                assert (current_clock - previous_music_clock) & 255 == 1, (
                    "music skipped an NMI update", memory.frame, previous_music_clock, current_clock)
                music_clock_checks += 1
            previous_music_clock = current_clock
            update_start = memory.clock
            music_profile = {}
        if pc == labels["music_return"] and update_start is not None:
            updates.append(memory.clock)
            update_durations.append(memory.clock - update_start)
            update_details.append(dict(start=update_start, end=memory.clock, elapsed=memory.clock-update_start,
                song=memory[labels["song_index"]], next_tick=song_ticks.get(memory[labels["song_index"]], 0)+1,
                ppu_frame=memory.frame))
            if profile:
                grouped = {}
                for address, cycles in music_profile.items():
                    index = bisect.bisect_right(profile_addresses, address) - 1
                    name = profile_labels[index][1] if index >= 0 else hex(address)
                    grouped[name] = grouped.get(name, 0) + cycles
                update_details[-1]["music_cpu_work"] = sum(music_profile.values())
                update_details[-1]["music_hotspots"] = sorted(grouped.items(), key=lambda pair: -pair[1])[:25]
            update_start = None
            snapshots.append(dict(memory.apu))
            updates_by_frame[memory.frame] = updates_by_frame.get(memory.frame, 0) + 1
            song = memory[labels["song_index"]]
            song_ticks[song] = song_ticks.get(song, 0) + 1
            editor = fixture["editor"][song]["registers"]
            current_tick = source_tick(fixture["tracks"][song], song_ticks[song])
            reference = {int(k): v for k, v in editor[current_tick % len(editor)].items()}
            # Independent score snapshots include the ordinary APU and any
            # physical expansion voices that PCM does not currently own.
            for control, low, high, destination, high_mask in (
                (0x4000, 0x4002, 0x4003, None, 7), (0x4004, 0x4006, 0x4007, None, 7),
                (0x5000, 0x5002, 0x5003, None, 7), (0x5004, 0x5006, 0x5007, None, 7),
                (0x9000, 0x9001, 0x9002, 2, 15), (0xa000, 0xa001, 0xa002, 3, 15)):
                if control not in reference or (destination is not None and
                        memory[labels["famistudio_pcm_owns"] + destination]):
                    continue
                volume = reference[control] & 15
                actual_volume = memory.apu.get(control, 0) & 15
                score_checks += 1
                if volume != actual_volume:
                    errors.append(("score volume", song, current_tick, hex(control), actual_volume, volume))
                if volume:
                    wanted = reference.get(low, 0) | (reference.get(high, 0) & high_mask) << 8
                    actual = memory.apu.get(low, 0) | (memory.apu.get(high, 0) & high_mask) << 8
                    score_checks += 1
                    if wanted != actual:
                        errors.append(("score period", song, current_tick, hex(low), actual, wanted))
            if startup is None:
                startup = memory.clock
                first_song = song
            if len(fixture["tracks"]) > 1 and len(updates) == frames_requested // 2:
                memory.controller = 1
                selection_requested = True
            if selection_requested and song != first_song:
                selection_done = True
                memory.controller = 0
        opcode = memory[cpu.pc]
        before = cpu.processorCycles
        category = interrupt_stack[-1][0] if interrupt_stack else ("music" if update_start is not None else None)
        cpu.step()
        cpu.processorCycles += memory.advance(cpu.processorCycles - before)
        if category is not None and startup is not None:
            used = cpu.processorCycles - before
            if profile and category == "music":
                music_profile[pc] = music_profile.get(pc, 0) + used
            occupied[category] += used
            frame_occupied[memory.frame] = frame_occupied.get(memory.frame, 0) + used
        if opcode == 0x40:
            kind, start = interrupt_stack.pop()
            (irq_durations if kind == "irq" else nmi_durations).append(memory.clock - start)
        instructions += 1
        assert instructions < max(30000000, frames_requested * 150000), (
            "cartridge stalled", hex(cpu.pc), len(updates))
    assert not errors, errors[:20]
    assert service_durations and sequence_checks and trigger_checks
    assert not memory.irq_overflows_while_asserted, "IRQ overflowed again before acknowledgement"
    if len(fixture["tracks"]) > 1:
        assert selection_done, "controller did not select second song"
    has_dpcm = any(registers.get(str(0x4015), 0) & 16
                   for song in fixture["editor"] for registers in song["registers"])
    assert not has_dpcm or memory.dma_reads, "DPCM was never exercised"
    services_by_frame = {}
    for _, frame, _ in service_times:
        services_by_frame[frame] = services_by_frame.get(frame, 0) + 1
    partial = {min(services_by_frame), max(services_by_frame)}
    partial.update(selection_frames)
    for selection in selections:
        partial.update((selection - 1, selection, selection + 1))
    complete = {frame: count for frame, count in services_by_frame.items() if frame not in partial}
    wanted_counts = (73, 74) if memory.mapper == 24 and fixture["pal"] else (
        (65, 66) if not fixture["pal"] else (78,))
    bad_frames = {f: n for f, n in complete.items() if n not in wanted_counts}
    if bad_frames:
        first = min(bad_frames)
        relevant = [entry for entry in service_times if first - 1 <= entry[1] <= first + 2]
        begin, end = relevant[0][0], relevant[-1][0]
        (folder / f"timing-failure-{stalls}.json").write_text(json.dumps(dict(
            bad_frames=bad_frames, service_entries=relevant,
            irq_edges=[t for t in memory.irq_edges if begin <= t <= end],
            mapper_writes=[w for w in memory.mapper_writes if begin <= w[0] <= end],
            nmi_entries=[e for e in nmi_entries if first - 1 <= e["frame"] <= first + 2],
            selections=selections), indent=2))
    assert all(count in wanted_counts for count in complete.values()), (
        "lost/duplicate samples", wanted_counts, bad_frames)
    period = 33247.5 if fixture["pal"] else 29780.5
    overruns = [row for row in update_details if row["elapsed"] >= period]
    if overruns:
        (folder / f"update-overruns-{stalls}.json").write_text(json.dumps(dict(
            period=period, overruns=overruns, rom_sha256=hashlib.sha256(rom).hexdigest()), indent=2))
    assert max(update_durations) < period, (
        "music update exceeded the next NMI deadline", max(update_durations))
    intervals = [b[0] - a[0] for a, b in zip(service_times, service_times[1:]) if b[0] - a[0] < 2000]
    crossings = [b[0] - a[0] for a, b in zip(service_times, service_times[1:])
                 if b[1] != a[1] and b[0] - a[0] < 2000]
    edge_intervals = [b - a for a, b in zip(memory.irq_edges, memory.irq_edges[1:]) if b - a < 2000]
    if memory.mapper == 24:
        assert set(edge_intervals) <= {454, 455}, ("VRC6 prescaler phase changed", sorted(set(edge_intervals)))
    dac_intervals = {}
    for destination, register in enumerate(DAC_REGISTERS):
        timestamps = [t for t, r, _ in memory.writes if r == register and t >= startup]
        values = [b - a for a, b in zip(timestamps, timestamps[1:]) if b - a < 2000]
        if per_voice_checks[destination] and values:
            dac_intervals[VOICE_NAMES[destination]] = dict(min=min(values), max=max(values))
    result = dict(passed=True, fixture=str(folder), mapper=memory.mapper, pal=fixture["pal"],
                  tracker=fixture["tracker"], dpcm_dma_stalls=stalls, instructions=instructions,
                  music_updates=len(updates), independent_editor_score_checks=score_checks,
                  song_logical_updates=song_ticks,
                  source_track_lengths=[dict(end=t["end"], loop=t["loop"]) for t in fixture["tracks"]],
                  consecutive_nmi_music_clock_checks=music_clock_checks,
                  independent_pcm_trigger_checks=trigger_checks,
                  independent_sample_cursor_checks=sequence_checks, pcm_values_checked=sample_checks,
                  per_destination_values_checked=dict(zip(VOICE_NAMES, per_voice_checks)),
                  sample_entry_interval=dict(min=min(intervals), max=max(intervals)),
                  cross_frame_entry_interval=dict(min=min(crossings), max=max(crossings)),
                  irq_edge_intervals=sorted(set(edge_intervals)),
                  dac_write_intervals=dac_intervals, samples_per_complete_frame=sorted(set(complete.values())),
                  service_wall_cycles_max=max(service_durations), irq_wall_cycles_max=max(irq_durations),
                  nmi_wall_cycles_max=max(nmi_durations), music_wall_cycles_max=max(update_durations),
                  occupied_cycles=occupied, cpu_fraction=sum(occupied.values()) / (memory.clock - startup),
                  worst_complete_frame_cpu_fraction=max(frame_occupied.get(f, 0) for f in complete) /
                    (33247.5 if fixture["pal"] else 29780.5),
                  dpcm_bytes=len(memory.dma_reads), dpcm_stolen_cycles=memory.dma_cycles,
                  song_selection_passed=selection_done if len(fixture["tracks"]) > 1 else None,
                  rom_sha256=hashlib.sha256(rom).hexdigest())
    return result, snapshots, rom_path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("templates", type=Path)
    parser.add_argument("--py65", type=Path)
    parser.add_argument("--frames", type=int, default=3200)
    parser.add_argument("--variant", help="Fixture subdirectory name; by default run all fixture.json files.")
    parser.add_argument("--profile", action="store_true", help="Include music CPU hotspots when an update exceeds its deadline.")
    parser.add_argument("--console-core", type=Path, help="Also run the existing independent libretro console test.")
    args = parser.parse_args()
    if args.py65:
        sys.path.insert(0, str(args.py65))
    from py65.devices.mpu6502 import MPU
    fixtures = [args.output / args.variant / "fixture.json"] if args.variant else sorted(args.output.glob("*/fixture.json"))
    assert fixtures, "No generated fixtures found."
    results = []
    for fixture_path in fixtures:
        fixture = json.loads(fixture_path.read_text())
        folder = fixture_path.parent
        if "template" in fixture:
            label_path = args.templates / (fixture["template"] + ".lbl")
        else:
            destination = fixture.get("destination", {"apu": 0, "mmc5": 1, "vrc6-1": 2, "vrc6-2": 3}.get(folder.name.rsplit("-", 2)[0], 1))
            mask = fixture.get("destination_mask", 1 << destination)
            mapper_name = "mmc5" if mask & 2 else "vrc6"
            suffix = "_famitracker" if fixture["tracker"] else ""
            label_path = args.templates / f"rom_{mapper_name}_pcm_m{mask}_{'pal' if fixture['pal'] else 'ntsc'}{suffix}.lbl"
        labels = read_labels(label_path)
        baseline = None
        for stalls in (False, True):
            result, snapshots, rom_path = execute(folder, labels, stalls, args.frames, MPU, args.profile)
            if baseline is None:
                baseline = snapshots
            else:
                audible = tuple(range(0x4000, 0x4010)) + (0x5000, 0x5002, 0x5003, 0x5004, 0x5006, 0x5007)
                differences = [(i, hex(reg), a.get(reg), b.get(reg))
                               for i, (a, b) in enumerate(zip(snapshots, baseline))
                               for reg in audible if a.get(reg) != b.get(reg)]
                assert not differences, ("DPCM stalls changed score", differences[:20])
                result["dpcm_preserves_other_channel_score"] = True
            results.append(result)
            print(json.dumps(result), flush=True)
        if args.console_core:
            console = Path(__file__).resolve().with_name("verify_console.py")
            subprocess.run([sys.executable, str(console), str(args.console_core), str(rom_path),
                            str(folder / "console"), str(label_path)], check=True)
        report_folder = folder if args.variant else args.output
        (report_folder / "cycle-verification.json").write_text(json.dumps(dict(
            passed=True, physical_hardware=False,
            scope="Actual 6502 instructions, mapper 5/24 PRG and IRQ, PPU timing, independent PCM byte cursor/gain/quantization, score clock and conservative DPCM DMA.",
            results=results), indent=2) + "\n")
    print("PASS independent PCM NES CPU, values, timing, DPCM and song selection")


if __name__ == "__main__":
    main()
