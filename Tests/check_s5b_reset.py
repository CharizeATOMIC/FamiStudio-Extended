"""Check deterministic S5B restart PCM using original synthetic register programs.

Usage: python Tests/check_s5b_reset.py <NesSndEmu library> [report.json]
Uses only Python's standard library and the public native ABI; no game assets.
"""
import ctypes as c
import hashlib
import json
from pathlib import Path
import sys

lib = c.CDLL(str(Path(sys.argv[1]).resolve()))
integer = c.c_int
callback_type = c.CFUNCTYPE(integer, c.c_void_p, c.c_uint)
callback = callback_type(lambda user, address: 0)

def bind(name, result, *args):
    fn = getattr(lib, 'NesApu' + name)
    fn.restype, fn.argtypes = result, args
    return fn

create = bind('Create', integer)
destroy = bind('Destroy', None, integer)
configure = bind('ConfigureOutput', None, integer, integer, integer,
                 c.POINTER(integer), c.POINTER(integer), c.POINTER(integer))
init = bind('Init', integer, integer, integer, integer, integer, integer, integer, callback_type)
reset = bind('Reset', None, integer)
enable = bind('EnableChannel', None, integer, integer, integer, integer)
write = bind('WriteRegister', None, integer, c.c_uint, integer)
frame = bind('EndFrame', None, integer)
available = bind('SamplesAvailable', integer, integer)
read = bind('ReadSamples', integer, integer, c.POINTER(c.c_short), integer)

def samples(index, frames, stereo):
    chunks = []
    for _ in range(frames):
        frame(index)
        count = available(index)
        buffer = (c.c_short * (count * (2 if stereo else 1)))()
        assert read(index, buffer, count) == count
        chunks.append(bytes(buffer))
    return b''.join(chunks)

def restart(index, pal, mode, stereo):
    configure(index, int(stereo), 0, None, None, None)
    assert init(index, 44100, 16, int(pal), 0, 32, callback) == 0
    reset(index)
    for channel in range(5):
        enable(index, 0, channel, 0)
    for channel in range(3):
        enable(index, 6, channel, int(channel == 0))
    registers = {0: 173, 1: 0, 6: 5, 7: 0x36 if mode == 'noise' else 0x3e,
                 8: 16 if mode == 'envelope' else 13, 11: 23, 12: 0, 13: 10}
    for address, value in registers.items():
        write(index, 0xc000, address)
        write(index, 0xe000, value)
    return samples(index, 17, stereo)

results = []
for pal in [False, True]:
    for stereo in [False, True]:
        for mode in ['tone', 'noise', 'envelope']:
            index = create()
            try:
                reference = restart(index, pal, mode, stereo)
                assert any(reference), 'Synthetic S5B signal must be audible'
                for warm_frames in [1, 2, 7]:
                    samples(index, warm_frames, stereo)
                    actual = restart(index, pal, mode, stereo)
                    assert actual == reference, (pal, stereo, mode, warm_frames, 'reset changed PCM')
                results.append(dict(pal=pal, stereo=stereo, mode=mode,
                                    samples=len(reference) // 2,
                                    sha256=hashlib.sha256(reference).hexdigest()))
                print('PASS S5B reset', pal, stereo, mode, 'three repeat lengths', flush=True)
            finally:
                destroy(index)
report = dict(passed=True, scenarios=len(results), repeat_comparisons=len(results)*3,
              description='First and repeated S5B rendering have identical PCM after reset.', results=results)
if len(sys.argv) > 2:
    Path(sys.argv[2]).write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
print('PASS all 36 S5B reset PCM comparisons')
