# PCM integration / PCM 整合说明

R12 separates DPCM and PCM assets and adds 2A03 and VRC6 destinations to the
existing MMC5 PCM path. This document retains its previous filename so existing
links continue to work.

## Instruments and hardware ownership

| Instrument | Keyboard mappings | PCM hardware output | Voice ownership |
| --- | --- | --- | --- |
| 2A03 | DPCM or PCM on each key | `$4011`, 7 bits | PCM and DPCM alternate on the DPCM voice |
| MMC5 | PCM only | `$5011`, 8 bits | Independent PCM voice; both MMC5 pulses remain available |
| VRC6 | PCM on mapped keys; ordinary notes elsewhere | `$9000` or `$A000`, 4 bits | Occupies the selected pulse; sawtooth remains available |

Import PCM into the **PCM Samples** section, then assign it through the
instrument's sample-mapping icon. Import DPCM into **DPCM Samples**. Assets may
have the same name across these libraries without sharing source data or edits.
An MMC5 or VRC6 instrument cannot accept a DPCM asset.

The MMC5 PCM channel also accepts an ordinary 2A03 instrument's PCM mappings.
The same instrument and PCM asset can be used on both channels. Its DPCM-only
keys remain unchanged and are silent on MMC5 PCM. Opening the mapping keyboard
keeps the selected compatible sample channel for audition.

2A03 has one physical sample DAC, not an extra PCM channel. Starting a PCM key
stops DPCM DMA; starting a DPCM key relinquishes software ownership. VRC6
ordinary notes similarly relinquish PCM ownership and restore pulse registers.
Inactive PCM service must not keep writing a neutral value over ordinary
playback. VRC6's two pulses are independent 4-bit destinations, not an 8-bit
high/low pair. See [APU DMC](https://www.nesdev.org/wiki/APU_DMC) and
[VRC6 audio](https://www.nesdev.org/wiki/VRC6_audio).

MMC5 uses direct-write mode. A zero write to `$5011` holds the previous level,
so processed PCM uses values 1–255 and centers silence at 128.
See [MMC5 audio](https://www.nesdev.org/wiki/MMC5_audio).

## Clocks and CPU time

| Target | Sample scheduling | Requirements |
| --- | --- | --- |
| Editor/audio export | Native stream, approximately 4 kHz | Matching NesSndEmu library |
| NSF/NSFe | NSF2 timer IRQ, 448 NTSC or 416 PAL CPU cycles | IRQ-capable player, explicit project region, CPU-budget checks |
| NES player with MMC5 | Mapper 5 scanline IRQ and blanking feeder | Supplied mapper 5 player; rendering enabled |
| APU-only or VRC6 NES player | Mapper 24 counter IRQ | Supplied mapper 24 player |
| Game sound engine | Host periodically calls PCM service | Game-owned timer, banking, interrupt and CPU schedule |

Music updates at 1×/2×/4× and PCM samples are separate clocks. Changing the
music multiplier does not multiply sample pitch. NES export resamples to its
actual timer rate. In particular, the VRC6 counter's CPU divider has a different
relationship to PAL video scanlines; assuming the NTSC sample rate on PAL would
change pitch and duration.

The approximately 4 kHz rate is a limit of the supplied feeder implementation,
not a fixed sample-rate limit of the DAC. A program can change the interval
between DAC writes without changing the CPU clock. Faster service requires a
matching resampling rate and enough CPU time for the worst IRQ path, DMA and
music updates. Raising the WAV output rate alone does not restore frequencies
removed during PCM conversion. The current release does not expose a higher
PCM rate; the music update-rate control does not change PCM quality.

Multiple PCM voices consume additional CPU time on every sample interrupt.
The editor's host CPU can render combinations that an actual 6502 program
cannot sustain. NSF export executes the generated program and checks elapsed
PLAY time, including IRQ service, DPCM stalls and song looping. It rejects
sustained overload before replacing the destination file. Isolated loop/page
boundaries can still cause sample-write jitter; this check does not guarantee
an identical interval between every pair of DAC writes. It does not silently
reduce sample rate to fit more voices or reserve CPU time for game logic.

NES templates are static music players with 1× music updates. The 2A03 PCM
player uses mapper 24 for scheduling and banking; its sample output is
the APU DAC, but its cartridge is **not NROM**. NSF2's timer is player-provided,
not a real NES cartridge register. See [NSF2](https://www.nesdev.org/wiki/NSF2)
and [VRC IRQ](https://www.nesdev.org/wiki/VRC_IRQ).

The supplied NES templates support one active PCM voice at a time. Different
DACs can alternate, including stopping one and starting another on the same
music tick; overlapping PCM is rejected. This is a limit of these player
templates, not the editor or the general engine. They support base-APU, MMC5
or VRC6 projects, not arbitrary multi-expansion combinations. PRG capacity is
1 MiB for the mapper 5 player and 256 KiB for mapper 24.
The mapper 24 player requires 8 KiB of WRAM, with its volume table at
`$6000–$6FFF`. An emulator must implement mapper 24 WRAM as well as its IRQ
and audio registers; an older core that omits this RAM cannot play these PCM
ROMs correctly.

The music-player menus use bounded controller rereads and accept a new button
state after three music updates. This filters accidental song changes without
blocking the PCM interrupt. It is not a guarantee against every DPCM/controller
DMA collision. A game's controller routine must fit its own interrupt schedule;
see [controller reading](https://www.nesdev.org/wiki/Controller_reading_code).

## Integrating the engine

PCM is disabled by default. Generated music headers list the required options:
`FAMISTUDIO_USE_APU_PCM`, `FAMISTUDIO_USE_MMC5_PCM` and/or
`FAMISTUDIO_USE_VRC6_PCM`. Use the matching engine sources and generated sample
data. All four assembler variants and the multi-expansion CA65 engine must
implement the same control stream.

The engine has separate playback state for each destination and a shared
16 × 256 byte volume lookup table. The default requires 4 KiB writable memory;
a host can supply a precomputed ROM table. Follow the generated header and
engine comments for buffer addresses, bank callbacks and periodic service.
The control stream selects destinations explicitly. Do not feed it to an
older MMC5-only engine.
The enabled common PCM module also uses 167 bytes of zero-page state,
including double-buffered descriptors for four destinations. These costs must
be included in a game's memory layout.

Exported sample data remains immutable while its note plays. A one-byte loop
may cache its source byte; volume changes still apply to each DAC write.
Retrigger the note to replace its source data.

Do not run a full music update at each PCM sample interrupt. Ordinary hardware
oscillators continue generating sound during CPU work, but new notes and
envelopes wait for timely music updates. DPCM DMA and sprite DMA can delay
software DAC writes. Game logic, VRAM transfers, mapper effects and sample
banking all need time and compatible interrupt ownership.

2A03's DAC also participates in nonlinear TND mixing. Changing its level can
change triangle/noise gain even when their registers are untouched; this is
hardware behavior, not an unintended channel-mute operation. See
[APU mixer](https://www.nesdev.org/wiki/APU_Mixer).

## Persistence and compatibility

Format **1009** introduced separate PCM assets, mappings, folders and sort state.
New saves use **1010**, which also stores the independent note-effect clock
needed when converting the editing grid without changing existing effects.
R11 format 1008 remains readable: the loader clones required PCM source assets
and converts the old regular instruments used on MMC5 PCM into MMC5
instruments. Existing DPCM data is preserved. Saving uses the new format;
earlier releases cannot read it.

Internally `DPCMSample` remains the shared processing class, with `IsPcm`
identifying the asset kind. `Project.Samples` is DPCM-only and
`Project.PcmSamples` is PCM-only. These contain distinct objects with unique
IDs. They are not two views of shared sample storage.

Text files use separate `DPCMSample`/`PCMSample` and
`DPCMMapping`/`PCMMapping` records. PCM retains its source and processing
parameters, rather than converting a WAV through lossy DPCM. Clipboard and
merge operations resolve names within the correct library.

NSF2 IRQ cannot coexist with EPSM's overlapping `$401C/$401D` interface here.
Unsupported exporters reject PCM. NSF import does not reconstruct editable
software-PCM samples from DAC writes.

Native playback probes the generalized PCM ABI before accepting a PCM project.
Windows libraries are rebuilt with this change. Linux, macOS and Android also
require rebuilt target-native libraries; managed compilation alone does not
establish runtime support. Emulator execution is not a physical cartridge test.

## 中文使用说明

在项目列表中分别导入 **DPCM 采样**与 **PCM 采样**。两库分别保存原始数据、
加工参数、文件夹与排序，同名采样也不会相互覆盖。

- **2A03**：同一个键位映射入口，每键选择 DPCM 或 PCM。两者轮流使用原生
  DPCM 声部，PCM 的硬件输出精度为 7 位。
- **MMC5**：可用 MMC5 乐器，也可直接复用普通 2A03 乐器中的 PCM 键位。
  只能播放 PCM，普通乐器的 DPCM 键位保留但在 MMC5 PCM 通道不发声。
  独立通道的硬件精度为 8 位。
- **VRC6**：在 VRC6 乐器中映射 PCM，通过任一方波通道播放，硬件精度为
  4 位。采样占用该方波，后续未映射音符恢复普通方波；锯齿波不受占用。

监听与 WAV 导出使用相同处理数据。编辑器和 NSF2 约 4 kHz 的供样时钟独立于
歌曲的更新倍率。硬件导出受 CPU 预算约束，不能把编辑器能播放的多路组合
直接等同于 FC 能实时播放的组合。
约 4 kHz 是当前供样驱动的实现限制，不是 DAC 的硬件上限。固定 CPU 时钟下，
改变写入 DAC 的间隔即可改变 PCM 采样率，但须同步重新采样并核算最慢中断、
DMA 与音乐更新的周期预算。本版尚未提供更高 PCM 采样率；提高音乐更新倍率
或 WAV 输出采样率都不能恢复 PCM 转换时滤掉的高频内容。

含 PCM 的 NSF/NSFe 需要支持 NSF2 IRQ 的播放器。PCM NES 使用独立模板：含 MMC5 的工程为 mapper 5，
仅原生 APU 或使用 VRC6 的工程为 mapper 24，均为 1× 静态音乐播放器。NES 按自身时钟重新采样，
保持音高与时长。2A03 PCM 版本不能当作普通 NROM 游戏插入使用。
当前 NES 模板同时只支持一路 PCM，不同 DAC 可以交替使用，包括同一拍点
先停止旧路再开始新路。PCM 重叠会明确拒绝；这是当前模板的限制，不代表
编辑器或通用声音引擎只能播放一路。
mapper 24 模板还需要 8 KiB WRAM；模拟器必须支持这块内存，而不仅支持
VRC6 的发声寄存器与中断。
播放器通过有次数上限的手柄重读及连续三次音乐更新确认来过滤误切歌，
期间继续供样。游戏应另外协调自己的手柄读取与中断时序，这种菜单防抖
不等于消除所有 DPCM 与手柄 DMA 冲突。

声音引擎的 PCM 默认关闭。游戏应按导出头部配置供样、中断、换页和 4 KiB
音量表，并核算画面与音频的共同负载。原生 DAC 的非线性混音可能改变
三角波和噪波响度，这是 2A03 硬件本身的特性。

工程格式升级为 1010，保存转换网格时所需的独立音符效果时钟。R11 工程会自动迁移共享采样，新保存的工程需要
本版或更新版本打开。跨平台移植必须同时重建原生音频库。
