"""Check every generated ROM layout and execute UI routines on a 6502.

Run Tools/build_tuning_rom.py first. Requires py65 (or --python-deps PATH).
No game ROM, BIOS, or extracted game data is used.
"""
from pathlib import Path
import argparse
import json
import re
import sys

parser = argparse.ArgumentParser()
parser.add_argument('--python-deps', type=Path)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
if args.python_deps:
    sys.path.insert(0, str(args.python_deps))
from py65.devices.mpu6502 import MPU

root = Path(__file__).resolve().parents[2]
rom = root / 'FamiStudio/Rom'
build = root / 'work/rom-build'
variants = re.findall(r'CALL :CompileNsfPermutation (\S+), "([^"]+)", (\w+), (\w+)', (rom / 'make.bat').read_text())
checks = 0


def check(value, description):
    global checks
    checks += 1
    if not value:
        raise AssertionError(description)


def debug_info(path):
    symbols, segments, scopes, raw_symbols = {}, {}, {}, []
    for line in path.read_text().splitlines():
        fields = dict(re.findall(r'(\w+)=("[^"]*"|[^,\s]+)', line))
        if line.startswith('scope\t'):
            scopes[fields['id']] = fields['name'].strip('"')
        elif line.startswith('seg\t'):
            segments[fields['name'].strip('"')] = {k: int(fields[k], 0) for k in ('start', 'size', 'ooffs') if k in fields}
        elif line.startswith('sym\t') and 'val' in fields:
            raw_symbols.append(fields)
    for fields in raw_symbols:
        name = fields['name'].strip('"')
        if fields.get('scope') != '0':
            name = scopes.get(fields.get('scope'), '?') + '::' + name
        symbols[name] = int(fields['val'], 0)
    return symbols, segments


class Memory(list):
    def __init__(self):
        super().__init__([0] * 65536)
        self.writes = []

    def __setitem__(self, address, value):
        super().__setitem__(address, value)
        if isinstance(address, int) and 0x2000 <= address < 0x6000:
            self.writes.append([address, value])


def call(memory, symbols, routine, interrupt=False, x=0):
    cpu = MPU(memory=memory)
    cpu.a, cpu.x, cpu.y, cpu.sp = 0xa5, x, 0x5a, 0xfd
    cpu.pc = symbols[routine]
    cpu.stPushWord(0x5000 if interrupt else 0x4fff)
    if interrupt:
        cpu.stPush(cpu.p)
    for _ in range(100000):
        if cpu.pc == 0x5000:
            return cpu
        cpu.step()
    raise AssertionError(f'{routine} did not return')


results = []
for cfg, options, name, extension in variants:
    symbols, segments = debug_info(build / (name + '.dbg'))
    binary = (build / (name + '.' + extension)).read_bytes()
    check(binary == (rom / (name + '.' + extension)).read_bytes(), name + ': embedded artifact is current')
    code, toc, vectors = (segments[k] for k in ('CODE', 'TOC', 'VECTORS'))
    check(code['start'] + code['size'] <= toc['start'], name + ': code does not overlap TOC')
    expected_songs = 12 if extension == 'fds' else 32 if 'epsm' in name else 48
    check(toc['size'] == 64 + 32 * expected_songs, name + ': full song capacity retained')
    if extension == 'fds':
        check((toc['start'], segments['SONG']['start']) == (0x7700, 0x7900), name + ': FDS exporter addresses unchanged')
        check(toc['start'] + toc['size'] <= segments['SONG']['start'], name + ': TOC does not overlap song')
        song_memory = re.search(r'SONG:\s*start\s*=\s*\$([0-9a-f]+),\s*size\s*=\s*\$([0-9a-f]+)', (rom / cfg).read_text(), re.I)
        check(int(song_memory[1], 16) + int(song_memory[2], 16) == 0xc000, name + ': full FDS song capacity retained')
        exporter = (root / 'FamiStudio/Source/IO/FdsFile.cs').read_text(encoding='utf8')
        check(re.search(r'FdsSongDataAddr\s*=\s*0x7900', exporter) is not None, name + ': exporter and linker agree')
    else:
        check(toc['start'] + toc['size'] <= vectors['start'], name + ': TOC does not overlap vectors')
        check(vectors['start'] == 0xfffa, name + ': hardware vectors unchanged')

    base_memory = Memory()
    for segment in segments.values():
        if segment.get('start', 0) >= 0x6000 and 'ooffs' in segment:
            start, size, offset = segment['start'], segment['size'], segment['ooffs']
            base_memory[start:start + size] = binary[offset:offset + size]

    def memory_copy():
        memory = Memory()
        memory[:] = base_memory[:]
        return memory

    # Right retains precedence over left, including either end of the list.
    for song_index in range(expected_songs):
        for pressed in (0, 0x40, 0x80, 0xc0):
            memory = memory_copy()
            memory[symbols['gamepad_pressed']] = pressed
            memory[symbols['song_index']] = song_index
            memory[symbols['max_song']] = expected_songs - 1
            memory[symbols['play_song']] = 0x60  # Stub only the audio/load callee.
            cpu = MPU(memory=memory)
            cpu.pc = symbols['main::check_right']
            for _ in range(100):
                if cpu.pc in (symbols['main::draw'], symbols['main::draw_done']):
                    break
                cpu.step()
            expected = min(song_index + 1, expected_songs - 1) if pressed & 0x80 else max(song_index - 1, 0) if pressed & 0x40 else song_index
            check(memory[symbols['song_index']] == expected, name + ': song navigation boundaries')
            check(cpu.pc == symbols['main::draw' if expected == song_index else 'main::draw_done'], name + ': song changes keep equalizers out of text NMI')

    if extension == 'nes' and not any(chip in name for chip in ('vrc6', 'vrc7', 's5b', 'n163')):
        memory = memory_copy()
        cpu = MPU(memory=memory)
        cpu.pc = symbols['reset']
        for _ in range(100):
            if memory[cpu.pc] == 0x78:  # First SEI follows mapper initialization.
                break
            cpu.step()
        check([w for w in memory.writes if 0x5120 <= w[0] <= 0x5127] == [[0x5120 + i, i] for i in range(8)], name + ': CHR banks retain order and values')

    # Native OAM layout remains identical after compact sprite initialization.
    memory = memory_copy()
    call(memory, symbols, 'setup_background')
    check(memory[symbols['oam']:symbols['oam'] + 12] == [15, 0x51, 1, 80, 23, 0x60, 1, 72, 23, 0x62, 1, 88], name + ': logo sprites')

    channel_count = symbols['NUM_EQUALIZERS']
    colors = [0, 1, 2, 0, 1, 1, 2, 0, 1, 2, 0, 1, 1, 2, 0]
    tiles = [[0xe3] * 4, [0xe3, 0xe3, 0xe3, 0xe0], [0xe3, 0xe3, 0xe3, 0xf0],
             [0xe3, 0xe3, 0xe0, 0xf0], [0xe3, 0xe3, 0xf0, 0xf0],
             [0xe3, 0xe0, 0xf0, 0xf0], [0xe3, 0xf0, 0xf0, 0xf0],
             [0xe0, 0xf0, 0xf0, 0xf0], [0xf0] * 4]
    split_rows = any(chip in name for chip in ('vrc6', 'vrc7', 's5b', 'n163', 'epsm'))
    for channel in range(channel_count):
        for volume in range(9):
            memory = memory_copy()
            memory[symbols['nmt_update_len']] = 7
            memory[symbols['famistudio_chn_note_counter'] + channel] = volume
            cpu = call(memory, symbols, 'update_equalizer', x=channel)
            hi = 0x21 if split_rows and channel < 5 else 0x22
            lo = memory[symbols['equalizer_ppu_addr_lo_lookup'] + channel]
            expected = [hi, lo, 4] + [tile + colors[channel] for tile in tiles[volume]]
            offset = symbols['nmt_update_data'] + 7
            check(memory[offset:offset + 7] == expected, name + ': equalizer tiles')
            check(cpu.x == channel and memory[symbols['nmt_update_len']] == 14, name + ': equalizer preserves channel and advances buffer')

    # The player never submits title text and equalizers in the same NMI.
    # Exercise both maximum legal workloads, not an unreachable combined load.
    timings = {}
    for kind in ('text_and_palette', 'equalizers'):
        memory = memory_copy()
        memory[symbols['nmi_ready']] = 1
        memory[symbols['nmt_update_mode']] = 2 if kind == 'text_and_palette' else 1
        palette = list(range(32))
        memory[symbols['palette']:symbols['palette'] + 32] = palette
        data = [0x21, 0x42, 28] + list(range(28)) if kind == 'text_and_palette' else sum(([0x22, i * 4, 4, 0xe3, 0xe0, 0xf0, 0xf0] for i in range(channel_count)), [])
        memory[symbols['nmt_update_data']:symbols['nmt_update_data'] + len(data)] = data
        memory[symbols['nmt_update_len']] = len(data)
        cpu = call(memory, symbols, 'nmi', interrupt=True, x=0x3c)
        expected_writes = []
        offset = 0
        while offset < len(data):
            hi, lo, count = data[offset:offset + 3]
            expected_writes += [[0x2006, hi], [0x2006, lo]] + [[0x2007, v] for v in data[offset + 3:offset + 3 + count]]
            offset += count + 3
        if kind == 'text_and_palette':
            expected_writes += [[0x2006, 0x3f], [0x2006, 0]] + [[0x2007, v] for v in palette]
        check([w for w in memory.writes if w[0] in (0x2006, 0x2007)] == expected_writes, name + ': ordered PPU writes')
        check((cpu.a, cpu.x, cpu.y) == (0xa5, 0x3c, 0x5a), name + ': interrupt restores registers')
        check(all(memory[symbols[key]] == 0 for key in ('nmi_lock', 'nmi_ready', 'nmt_update_mode', 'nmt_update_len')), name + ': interrupt clears completed update')
        # 7 interrupt-entry cycles, 7 for a possible preceding instruction,
        # and the slower of the two OAM DMA phases. Use the faster PAL DMC
        # period (50 cycles) even for NTSC (54), for a conservative common bound.
        base_cycles = cpu.processorCycles + 7 + 7 + 514
        dmc_stalls = ((base_cycles + 49) // 50 + 1) * 4
        timings[kind] = {'cpu_cycles': cpu.processorCycles, 'with_oam_entry_and_dmc': base_cycles + dmc_stalls}
        check(base_cycles + dmc_stalls < 2273, name + ': NTSC vblank budget')

    results.append({'name': name, 'songs': expected_songs, 'code_bytes': code['size'], 'free_code_bytes': toc['start'] - code['start'] - code['size'], 'nmi': timings})

args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps({'passed': True, 'checks': checks, 'variants': results}, indent=2) + '\n')
print(f'PASS {checks} checks across {len(results)} ROM/FDS configurations')
print('Minimum code headroom:', min(item['free_code_bytes'] for item in results))
print('Worst NMI including DMA:', max(t['with_oam_entry_and_dmc'] for item in results for t in item['nmi'].values()))
