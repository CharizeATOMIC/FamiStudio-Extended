"""Execute synthetic 6502 NSF code against the MMC5 DAC.

Usage: python Tests/check_mmc5_pcm_nsf.py NotSoFatso.dll output-directory
The test writes only its own short NSF fixtures. No game assets are involved.
"""
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct
import sys

library = Path(sys.argv[1]).resolve()
output = Path(sys.argv[2]).resolve()
output.mkdir(parents=True, exist_ok=True)
lib = c.CDLL(str(library))


def bind(name, result, *args):
    method = getattr(lib, name)
    method.restype, method.argtypes = result, args
    return method


open_nsf = bind("NsfOpen", c.c_void_p, c.c_char_p)
close_nsf = bind("NsfClose", None, c.c_void_p)
set_track = bind("NsfSetTrack", None, c.c_void_p, c.c_int)
run_frame = bind("NsfRunFrame", c.c_int, c.c_void_p)
get_state = bind("NsfGetState", c.c_int, c.c_void_p, c.c_int, c.c_int, c.c_int)
callback_type = c.CFUNCTYPE(None, c.c_int, c.c_int)
set_callback = bind("NsfSetApuWriteCallback", None, c.c_void_p, callback_type)
checks = []

# Each actual PLAY call executes one indexed pair of control/DAC writes. The
# zero-page counter starts at zero and clamps to its final row.
controls = [0, 0, 0, 0, 1, 1, 0x80, 0, 0, 0, 0]
values = [1, 127, 128, 255, 7, 0, 200, 0, 254, 129, 0]
expected = [1, 127, 128, 255, 255, 255, 200, 200, 254, 129, 129]
for pal in [False, True]:
    header = bytearray(128)
    header[:5] = b"NESM\x1a"
    header[5:8] = bytes([1, 1, 1])
    struct.pack_into("<HHH", header, 8, 0x8000, 0x8000, 0x8010)
    struct.pack_into("<H", header, 0x6e, 16639)
    struct.pack_into("<H", header, 0x78, 19997)
    header[0x7a] = int(pal)
    header[0x7b] = 8
    data = bytearray(0x400)
    data[:6] = bytes([0xa9, 0, 0x85, 0, 0x60, 0xea])  # init: lda #0/sta $00/rts
    program = [
        0xa6, 0x00,             # ldx $00
        0xbd, 0x00, 0x81,       # lda controls,x
        0x8d, 0x10, 0x50,       # sta $5010
        0xbd, 0x20, 0x81,       # lda values,x
        0x8d, 0x11, 0x50,       # sta $5011
        0xa9, 0, 0x8d, 0x15, 0x50,  # disabling pulses must not change DAC
        0xe0, len(values) - 1,  # cpx #last
        0xf0, 2,               # beq done
        0xe6, 0,               # inc $00
        0x60,                  # done:rts
    ]
    data[0x10:0x10+len(program)] = bytes(program)
    data[0x100:0x100+len(controls)] = bytes(controls)
    data[0x120:0x120+len(values)] = bytes(values)
    path = output / f"dac-{'pal' if pal else 'ntsc'}.nsf"
    path.write_bytes(header + data)
    handle = open_nsf(str(path).encode())
    assert handle, f"Cannot load synthetic NSF {path}"
    writes = []
    callback = callback_type(lambda address, value: writes.append((address, value)))
    try:
        set_track(handle, 0)
        set_callback(handle, callback)
        for index, wanted in enumerate(expected):
            writes.clear()
            # A fresh NSF first returns from INIT; no PLAY write is expected
            # in that startup frame. Thereafter each RunFrame runs one PLAY.
            for attempt in range(3 if index == 0 else 1):
                run_frame(handle)
                if any(address == 0x5011 for address, _ in writes):
                    break
            actual = get_state(handle, 17, 0, 0)
            assert actual == wanted, (pal, index, actual, wanted, writes)
            assert (0x5011, values[index]) in writes, (pal, index, "missing real CPU callback", writes)
            checks.append(dict(pal=pal, play=index, written=values[index], control=controls[index], dac=actual))
    finally:
        close_nsf(handle)

report = dict(passed=True, checks=len(checks), details=checks,
              dll_sha256=hashlib.sha256(library.read_bytes()).hexdigest(),
              scope="Real 6502 writes; all 8 DAC bits, zero hold, read-mode direct-write suppression, pulse-enable independence. No physical-console claim.")
(output / "dac-verification.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(f"PASS all {len(checks)} MMC5 PCM real-CPU DAC checks")
