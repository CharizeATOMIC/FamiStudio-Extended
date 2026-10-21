# 2A03、MMC5 与 VRC6 PCM：开发说明

本文记录 R12 PCM 功能的硬件依据、执行合同和测试入口。当前使用说明见 [PCM 整合说明](mmc5-pcm-integration.md)。它是开发笔记，不是已发布版本的功能清单。编辑器、导出器和模拟器各层须分别通过验证；文件头声明扩展音源不等于已经实现其采样播放。

## 硬件边界

MMC5 的 `$5011` 是 8 位直接音量输出寄存器。直接写入模式由 `$5010` bit 0 清零选择；非零字节更新输出，写入零保持原输出。因此 PCM 编码必须明确处理零值，不能把零当静音，也不能沿用 DMC 的 7 位 DAC 范围。读取模式通过 CPU 对 `$8000–$BFFF` 的读取送入数据，它不是自动采样 DMA。本次出口使用直接写入，不依赖读取模式或 MMC5 PCM IRQ。依据：[MMC5 audio](https://www.nesdev.org/wiki/MMC5_audio)、[NSFPlay MMC5 实现](https://github.com/bbbradsmith/nsfplay/blob/master/xgm/devices/Sound/nes_mmc5.cpp)。

VRC6 的两个方波声部也可以输出 CPU 提供的电平：`$9000/$A000` 的 bit 7 忽略占空比，低四位给出输出电平，并须启用对应声部。两个声部等权相加，仅产生总和 0–30 的 31 个静态电平，并不组成一个高低位拼接的 8 位 DAC；使用两个声部时，这两个声部不能同时保持普通方波演奏。依据：[VRC6 audio](https://www.nesdev.org/wiki/VRC6_audio)、[Mesen VRC6 混音源码](https://github.com/SourMesen/Mesen2/blob/master/Core/NES/Mappers/Audio/Vrc6Audio.h)。31 个电平是依据该加法关系作出的推导。

[Furnace 的 VRC6 源码](https://github.com/tildearrow/furnace/blob/master/src/engine/platform/vrc6.cpp) 也有软件采样路径。R12 已实现两个 VRC6 方波的独立 4 位 PCM 输出、监听和引擎导出。尚未对本工程输出作 VRC6 原硬件录音验证。VRC6 CPU 周期模式 IRQ 可以提供周期触发，但普通 NSF 的 VRC6 音频支持并不承诺该映射器 IRQ 存在。参见 [VRC IRQ](https://www.nesdev.org/wiki/VRC_IRQ)。

## 两种时钟必须分开

现有音乐逻辑和包络的 1/2/4 倍更新率不是 PCM 采样率。工程现在可选约 4/8/16/32 kHz，默认约 4 kHz；高三档用于编辑器播放与音频导出。更高档位增加转换后的数据量和每秒处理的样本数，实际 CPU 开销取决于工程和机器，不能按档位承诺总 CPU 占用倍数。源数据仍为 8 位，APU/VRC6 的输出位深保持 7/4 位。不能通过提高普通 NSF PLAY 调用频率、再在同一次调用中运行完整音乐更新来代替独立 PCM 时钟。

声音引擎、NSF/NSFe 和 NES 导出只提供“4 kHz 兼容导出”。高率工程默认未选择，必须由用户明确选择兼容档；API 默认拒绝，CLI 需传 `-pcm-rate:4000` 或 `-pcm-rate:4k`，其他值拒绝。导出器只改临时副本，从原始源波形低通重采样，保持音高、映射转调和音符时长，不修改工程采样率与源样本。降采样会缩减带宽；NES 的兼容档继续使用映射器实际频率。格式 1011 保存采样率，旧工程默认 4 kHz。

现有完整 IRQ 路径不支持通用 8 kHz 承诺。`Tests/check_pcm_irq_cycles.py` 使用实际链接内核枚举 14 种地区/目标组合，每组 168 种路径，包含 IRQ 入口、寄存器保存、应答、切 bank、PCM service、RTI；不包含 DMC DMA、被中断指令尾部和宿主任务。当前最坏周期为：

| 活动目标 | NTSC 最坏周期 | PAL 最坏周期 |
| --- | ---: | ---: |
| 2A03 | 248 | 248 |
| MMC5 | 246 | 246 |
| 一个 VRC6 方波 | 256 | 256 |
| 两个 VRC6 方波 | 455 | 454 |
| 2A03 + MMC5 | 436 | 436 |
| 四个 DAC | 834 | 834 |

单声部最坏值已经超过约 8 kHz 所能提供的 224/208 周期。短循环缓存路径更快，不能代表任意样本的切页、触发和回跳成本。该表是路径成本测量，不是所有多声部组合可导出的证明。现有 NSF 导出逐曲检查 PLAY 的真实耗时，能拒绝持续过载，但不提供每个采样 IRQ 的独立期限保证；将来开放高率硬件输出必须另外验证遗漏 IRQ、逐周期 DAC 间隔和 DMA 干扰，不能只沿用 PLAY 预算门槛。

当前 NSF2 出口合同采用返回式 INIT/PLAY 和 IRQ 功能 bit 4；PCM 中断可抢占音乐 PLAY，并在两次 PLAY 之间继续运行。`$401B/$401C` 为周期重载值，`$401D` 控制启动并在读取时应答。周期为重载值加一。NTSC 重载 447、PAL 重载 415，即 448/416 个 CPU 周期，对应各自 CPU 时钟下约 4 kHz。CPU 完成当前指令后才响应 IRQ，因此实际寄存器写入有指令边界抖动。导出器仍须逐一验证各音乐更新率和最坏采样路径的 CPU 预算。依据：[NSF2](https://www.nesdev.org/wiki/NSF2)、[6502 中断时序](https://www.nesdev.org/wiki/Interrupts)。

NSF2 定时器不是 NES 卡带的硬件寄存器。NES 导出使用独立的 mapper 5 扫描线方案或 mapper 24 计数器方案；出口根据实际 NTSC/PAL 时钟重新采样以保持音高和时长。仅原生 2A03 PCM 的工程使用 mapper 24 调度，不能视为 NROM。该路径必须单独验证扫描线中断、垂直消隐期及内存布局，不能把 NSF2 数据加上 iNES 文件头当作可用 NES。本笔记不声称已经完成原硬件录音或所有地区的实体卡带验证。

## NotSoFatso 当前实现范围

本分支增加了：

- NSF2 bit 4 及 NSFe `NSF2` chunk 的加载、保存。
- 可读写周期重载值、自动重载、持续 IRQ 标志及读取应答。
- `$FFFE/$FFFF` 的 RAM 向量覆盖，初始值来自当前 ROM 映射。
- 真实 6502 IRQ 栈入口与 RTI、CLI/SEI/PLP 的延迟处理；音乐更新及主机空闲期间均运行中断。
- 现有 APU frame/DMC IRQ 接入 CPU；DMC DMA 消耗的 CPU 周期计入定时器。
- NSF1 不启用上述执行路径，以保留旧音序的执行时间。
- NSF2 的逐帧返回值报告本时段实际完成的 PLAY，不沿用旧的持久标志；诊断 `NsfGetState(-1, 1002, 0)` 给出实际进入 PLAY 的累计次数。CPU 超预算时，一个 PLAY 可跨多个主机时段，必须用这些计数检查，不能把“曾经播放过”当作持续按时更新。
- 诊断 1003、1004、1005 分别给出已完成 PLAY 的最大耗时、当前尚未返回 PLAY 的耗时和上一次已完成 PLAY 的耗时，单位为 CPU 周期。它们包括 IRQ 与 DMC DMA，跨主机时段连续累计，并在切曲时重置。导出预算检查比较实际入口到返回的耗时与 PLAY 周期，避免把主机报告时段边界误判为超时。

不支持的 NSF2 非返回 INIT、抑制 PLAY、强制元数据特性仍明确拒绝，不能据此宣称“完整 NSF2 支持”。现有 6502 核心是指令级模拟器；上述测试验证定时器与程序行为，并不替代全部 6502 总线时序测试。

本工程 EPSM 的地址/数据端口使用 `$401C/$401D`，与 NSF2 定时器重叠。NSF2 IRQ 与 EPSM 的组合必须拒绝，不能通过忽略其中一个设备来“支持”。普通 NSF EPSM 路径不受影响。

## 可复现测试

`Tests/Nsf2Timer/verify.py` 使用 ca65/ld65 生成原创合成 NSF，并通过实际 NotSoFatso DLL 执行 CPU。它验证 NTSC/PAL 高频输出、超过 6,000 周期的 PLAY 被 IRQ 抢占、空闲采样、应答/禁用、向量覆盖、CLI 延迟、APU IRQ、DMC DMA、切曲重置、NSFe 以及不支持特性的拒绝。

```text
python Tests/Nsf2Timer/verify.py --dll <NotSoFatso.dll> --output <临时目录>
```

可选 `--reference-nsf` 运行 Brad Smith 的原始 [nsf2_irq.nsf](https://github.com/bbbradsmith/nes-audio-tests/blob/master/build/nsf2_irq.nsf)，其[源码](https://github.com/bbbradsmith/nes-audio-tests/blob/master/nsf2_irq.s)说明了错误向量音和长 PLAY 测试。该文件只下载到临时目录，不嵌入本仓库。可选 `--baseline-dll` 与多个 `--legacy` 比较旧 NSF 的 APU 写值和周期。

已经实际运行的专项包括作者测试 480 次 PLAY 间隔、5,031 次 DAC 写入，刻意超预算 PLAY 的准确完成计数，以及四个已有 1×/4×、PAL、延迟/循环变奏 NSF 对旧 DLL 的精确寄存器周期比较。合成定时器仍保留 224/208 周期压力测试；这些数值是模拟器测试的压力条件，**不是当前产品 PCM 采样率**。结果 JSON 记录所测 DLL 的 SHA256。最终 PCM 出口仍须绑定最终程序集和导出文件重新运行，不能用该合成测试替代全部产品验证。

## 数据模型、试听和导入边界

格式 1009 使用独立 DPCM/PCM 库。每个资源有独立源数据、加工参数、ID、文件夹和排序。`Project.Samples` 仅保存 DPCM，`Project.PcmSamples` 仅保存 PCM；`AllSamples` 供两库遍历。内部沿用 `DPCMSample` 加 `IsPcm`，不表示共用存储。相同名称只能在同一库内进行合并匹配。

2A03 的同一映射表允许每键选择 DPCM 或 PCM；MMC5/VRC6 仅允许 PCM。`GetSampleMapping` 返回两种映射之一，`GetDPCMMapping` 与 `GetPcmMapping` 严格按类型筛选。格式 1008 的旧 MMC5 PCM 工程读取时自动复制独立 PCM 资源，并将该通道使用的旧常规乐器转成 MMC5 乐器；原 DPCM 数据保留。

`GetPcmData` 从原始源建立 8 位 PCM，保留音量包络、裁剪、微调及转调，低通重采样后避免 MMC5 的零值保持。硬件输出时 APU 右移一位、VRC6 右移四位。独立 PCM 资源的处理、波形与试听都使用 PCM 数据，不经过有损 DPCM 编码。DPCM 编码速率、填充和 bank 等不适用于 PCM 的选项隐藏。旧 DMC 源迁移为 PCM 时仍保留影响原始解码的设置。

PCM 开始/停止/音量在四个原生目标上独立。APU PCM 占用原 DPCM DAC；普通 DPCM 音符恢复 DMA 时停止软件写入。VRC6 PCM 占用对应方波，普通音符恢复后不再被 PCM IRQ 中性写入覆盖。APU 的非线性 TND 混音仍会使 DAC 电平影响三角波/噪波响度，这属于硬件行为。

当前 NSF 导入仍不能从逐周期的 MMC5 PCM 输出重建可编辑采样轨道。导入器在实际播放中检测直接 DAC 的电平变化，排除 INIT、零值保持、恒定中性输出和被读取模式忽略的写入；检测到时给出该曲 PCM 轨道无法恢复的警告，仅导入其他受支持的声道。MMC5 扩展标志本身不会触发此警告。该检测不声称支持 MMC5 读取模式采样重建，也不等于采样导回功能。

托管程序集与原生音频库必须同时更新。`NesApu.SupportsPcm` 通过实际查询导出入口检查能力；旧库下工程设置禁用 PCM 并解释原因，打开 PCM 工程返回明确错误，直接调用播放入口也拒绝执行。底层文件加载器仍完整保留数据，普通工程不受该保护影响。`--mmc5-pcm-native-guard <输出目录> <true|false>` 用于在隔离的新旧原生库进程中分别验证这些行为；不能把 Windows DLL 的通过结果称为其他平台二进制已通过。

相关托管回归入口包括 `--pcm-libraries`、`--pcm-playback`、`--pcm-destination-export`、`--mmc5-pcm`、`--mmc5-pcm-preview`，以及 `Mmc5PcmImportRegression.Run` 的实际合成 NSF 导入测试。后续集成结果需分别列出二进制/文本/Undo、清理/合并、原生监听与 WAV、NSF2/NSFe 的周期和样本对照、NES CPU 执行、七语言 UI。原版工程、用户源 WAV 和用户配置必须保持不变。

## English hardware and support summary

- MMC5 direct PCM uses the full 8-bit `$5011` DAC. `$00` holds the preceding output; it is not silence. This implementation uses write mode, not MMC5 read-mode playback or its PCM IRQ.
- VRC6 can hold either pulse channel at a CPU-selected 4-bit level. Two equally mixed channels provide 31 static summed levels, not an 8-bit DAC. R12 implements each pulse as an independent 4-bit PCM destination; ordinary pulse notes relinquish software-sample ownership.
- Music logic at 1×/2×/4× and the PCM sample clock are independent. Editing and audio support project rates of approximately 4/8/16/32 kHz. Hardware exports support only the explicit 4 kHz compatibility tier (NSF: NTSC 448 cycles, PAL 416 cycles). High-rate projects require explicit resampling consent; unsupported hardware rates are not offered. Source PCM is 8-bit, with hardware output quantized to 7 bits on 2A03 and 4 bits per VRC6 pulse. NSF export requires a player supporting NSF2 timer IRQs and returning INIT/PLAY.
- NSF2 loading supports that IRQ subset, including interruptible PLAY/idle, RAM IRQ vectors, DMA-aware timer accounting and track reset. Non-returning INIT and other unsupported NSF2 feature flags are rejected. EPSM cannot share `$401C/$401D` with this timer.
- An instruction-level emulator and software CPU tests do not constitute physical-hardware certification. Separate mapper 5 and mapper 24 NES players resample for their actual clocks and require independent timing verification.
- Displayed PCM samples and processed sample audition come from the actual PCM conversion, not the lossy DPCM preview. Original WAV audition and DPCM behavior remain separate.
- NSF import does not reconstruct an editable MMC5 PCM sample track. Observed non-initialization DAC activity produces a localized warning; it must not be advertised as PCM round-trip import.
- PCM requires the updated native audio library for the target platform. The actual native export is probed before enabling PCM, opening a PCM project or starting playback. An older library leaves ordinary projects usable and reports a clear capability error for PCM instead of dropping its channel.

## R12 regression boundaries

`IndependentPcmModelRegression` covers separate same-name resources, editing,
serialization, clipboard, merge, cleanup and R11 binary/text migration.
`PcmPlaybackRegression` checks all four destinations, PAL/NTSC, music clocks,
ordinary-voice handoff, seeking, pan and expansion copies. Native tests in
`Tests/check_pcm_destinations_native.py` test the generalized ABI directly.
`PcmDestinationExportRegression` executes exported NSF2 programs and compares
actual DAC writes, including the handoff intervals. The assembler matrix in
`Tests/check_pcm_engine_assemblers.py` compares the four engine syntaxes.
`Tests/check_pcm_cache.py` checks one-byte-loop caching against linked 6502 code,
including retriggers, buffer reuse, volume changes and returning to normal samples.
`--pcm-nes <output>` generates the NES fixtures. `Tests/PcmNes/run_matrix.py`
executes them with an independent cycle model and an optional Mesen libretro
core, checking sample values, DMA stalls, loop boundaries, music deadlines,
visible video and held/released song-selection buttons. `summarize.py` only
accepts reports whose ROM hashes match the current exports.

CPU costs from the earlier single-voice R11 engine cannot be reused as R12
budget results. Extra destinations increase dispatch and sample work. Product
exports need their own IRQ and music-update checks with the final engine.
No software test establishes physical-cartridge or untested-platform behavior.

`--pcm-sample-rate-export <output>` verifies that unconfirmed high-rate exports and
unsupported rates preserve existing destination files. Explicit 4 kHz exports
are compared with the corresponding original 4 kHz project's NSF, NSFe, NES,
assembly and PCM bytes, including actual NSF CPU register sequences, PAL/NTSC,
all four DACs, mapping transposition, loops and the unchanged source project.
