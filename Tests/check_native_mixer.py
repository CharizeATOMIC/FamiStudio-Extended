"""Exercise the rebuilt native ABI on Windows, Linux or macOS (Python stdlib only)."""
import ctypes as c
from pathlib import Path
import sys

lib = c.CDLL(str(Path(sys.argv[1]).resolve()))
integer = c.c_int
pointer = c.POINTER(integer)
dmc_type = c.CFUNCTYPE(integer, c.c_void_p, c.c_uint)
dmc = dmc_type(lambda user, address: 0)

def bind(name, result, *args):
    fn = getattr(lib, 'NesApu' + name)
    fn.restype, fn.argtypes = result, args
    return fn

create = bind('Create', integer)
destroy = bind('Destroy', None, integer)
configure = bind('ConfigureOutput', None, integer, integer, integer, pointer, pointer, pointer)
init = bind('Init', integer, integer, integer, integer, integer, integer, integer, dmc_type)
enable = bind('EnableChannel', None, integer, integer, integer, integer)
write = bind('WriteRegister', None, integer, c.c_uint, integer)
frame = bind('EndFrame', None, integer)
available = bind('SamplesAvailable', integer, integer)
read = bind('ReadSamples', integer, integer, c.POINTER(c.c_short), integer)
pan = bind('SetPan', None, integer, integer, integer)

def render(index, frames=15):
    audio = []
    for _ in range(frames):
        frame(index)
        count = available(index)
        assert 0 < count < 2000
        buffer = (c.c_short * (count * 2))()
        assert read(index, buffer, count) == count
        audio.extend(buffer)
    return audio[12000:]

contexts = [create(), create()]
assert min(contexts) >= 10 and contexts[0] != contexts[1]
try:
    for index, balance, timer in zip(contexts, [0, 100], [253, 200]):
        configure(index, 1, 1, (integer * 1)(1), (integer * 1)(0), (integer * 1)(balance))
        assert init(index, 44100, 16, 0, 1, 1, dmc) == 0
        for channel in range(5): enable(index, 0, channel, 0)
        for channel in range(3): enable(index, 1, channel, int(channel == 0))
        write(index, 0x9000, 0x4f)
        write(index, 0x9001, timer & 255)
        write(index, 0x9002, 0x80 | (timer >> 8))
    left, right = [render(index) for index in contexts]
    assert any(left[::2]) and not any(left[1::2])
    assert any(right[1::2]) and not any(right[::2])
    assert left[::2] != right[1::2]
    print('PASS native ABI: independent VRC6 contexts with distinct pitches and opposite pan')
    pan(contexts[0], 0, 100)
    switched = render(contexts[0])
    assert any(switched[1::2]) and not any(switched[::2])
    print('PASS native ABI: live pan changes without rebuilding the context')
finally:
    for index in contexts: destroy(index)
reused = create()
assert reused == contexts[0]
destroy(reused)
print('PASS native ABI: contexts released and reused')
