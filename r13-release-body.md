Based on FamiStudio 4.5.3 / 基于 FamiStudio 4.5.3
Project format / 工程格式：1011

## 中文

- 工程属性 → 信息新增“PCM 采样率”，可选择约 4、8、16、32 kHz。
  适用于 2A03、MMC5 和 VRC6 的 PCM 编辑、试听及音频导出。
- 切换采样率会从保留的源数据重新采样，保持音高和时长；不改变音乐更新率、
  钢琴窗网格或 DPCM 采样设置。支持保存、撤销、重做和文本导入导出。
- 高采样率保留更多高频细节，同时增加 CPU、内存和采样数据量。
  工程属性会显示当前制式的实际采样率和性能提示。
- NSF、NSFe、NES 和声音引擎仅提供约 4 kHz 的兼容 PCM 导出。
  高采样率工程须明确选择兼容重采样，导出副本保留音高和时长，原工程不变。
  当前通用 6502 驱动无法保证更高采样率的中断时限，因此不提供相应导出档位。
- 修正 PCM 声音引擎导出的制式选择。既有芯片、并发声部和 CPU 预算限制继续适用。
- 新控件和提示适配全部七种语言。工程格式升级至 1011，旧工程仍可打开；
  新保存的工程需要 R13 或更新版本。

## English

- Add PCM sample rate to Project Properties → Information, with approximately
  4, 8, 16 and 32 kHz options for 2A03, MMC5 and VRC6 editing, audition and audio export.
- Resample retained source data when changing rates while preserving pitch and
  duration. Music updates, piano-roll resolution and DPCM settings remain independent.
  Project saves, undo/redo and text interchange retain the selected rate.
- Higher rates preserve more high-frequency detail and increase CPU work, memory
  and PCM data size. The dialog shows the actual regional clock and performance notes.
- NSF, NSFe, NES and sound-engine export offer only the approximately 4 kHz compatible
  PCM clock. Higher-rate projects require an explicit compatible resampling choice.
  The exported copy retains pitch and duration; the original project is unchanged.
  Higher hardware-export rates are unavailable because the current general-purpose
  6502 driver cannot meet their per-sample interrupt deadlines reliably.
- Correct region selection for PCM sound-engine export. Existing chip, concurrency
  and CPU-budget restrictions still apply.
- Localize all new controls and messages in all seven supported languages.
  Project format is now 1011. Existing projects load normally; new saves require R13
  or newer.
