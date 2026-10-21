## 中文

**FamiStudio Extended r12** 为 2A03、MMC5 和 VRC6 加入软件 PCM 工作流，涵盖采样管理、键位映射、编辑试听、音频导出及声音引擎、NSF2、NES 导出。基于 FamiStudio 4.5.3，并保留 r10 的调律、混音与乐器控制功能。

### 新增与改进

- **独立的 DPCM 与 PCM 采样库**：分别管理源数据、加工参数、文件夹和排序。同名采样也可以独立编辑，不会相互覆盖。
- **2A03 采样映射**：同一个键位映射界面，每键选择 DPCM 或 PCM。PCM 使用原生 7 位 DAC，与 DPCM 轮流占用同一硬件声部。
- **MMC5 PCM**：MMC5 乐器提供仅接受 PCM 的键位映射，也可直接复用普通 2A03 乐器的 PCM 键位。通过独立 8 位 DAC 播放，两个 MMC5 方波仍可同时使用。
- **VRC6 PCM**：两个方波通道各自支持 4 位 PCM。播放采样时占用对应方波，未映射音符恢复普通方波演奏，锯齿波保持可用。
- **完整的采样编辑流程**：支持波形编辑、试听、移调、循环、音量、实时声像及音频导出；新增界面和提示适配现有七种语言。
- **配套播放与导出**：声音引擎、NSF2 和专用 NES 模板支持 PCM 与普通音色交接，并改善短循环处理。保存、文本交换、复制粘贴、撤销和工程合并均识别两套采样库；旧共享采样工程会在打开时迁移。
- **更精细的编辑网格**：切换 1×/2×/4× 时同步转换现有歌曲的时间轴，保留音乐速度和包络时长；4× 提供 NTSC 240 Hz / PAL 200 Hz 的音符放置精度。无法精确转换时会说明原因并保留原工程。
- **另存为图标**：在保存按钮旁提供独立入口，沿用现有快捷键与多语言提示。
- 修正相同 BPM 的不同节奏排列选择，以及起始填充节奏在 NSF 导出中的首帧偏移。

### 下载

下载 [Windows x64 程序包](https://github.com/CharizeATOMIC/FamiStudio-Extended/releases/download/r12/FamiStudio-Extended-4.5.3-r12-win-x64.zip)，完整解压后运行 `FamiStudio-Extended.exe`，无需另装 .NET。

- [PCM 示例包](https://github.com/CharizeATOMIC/FamiStudio-Extended/releases/download/r12/PCM-r12.zip)
- [r12 源码](https://github.com/CharizeATOMIC/FamiStudio-Extended/tree/r12) · [源码 ZIP](https://github.com/CharizeATOMIC/FamiStudio-Extended/releases/download/r12/FamiStudio-Extended-r12-source.zip)
- [Git 历史增量包（需已有 r10）](https://github.com/CharizeATOMIC/FamiStudio-Extended/releases/download/r12/FamiStudio-Extended-r12.bundle)
- [PCM 游戏整合说明](https://github.com/CharizeATOMIC/FamiStudio-Extended/blob/r12/Docs/docs/development/mmc5-pcm-integration.md)

### 兼容与使用范围

- **工程格式为 1010**。早期版本和官方 FamiStudio 无法读取本版新保存的工程；可用“另存为”保留原文件。
- 编辑器和 NSF2 的 PCM 供样时钟约为 **4 kHz**，独立于音乐的 1×/2×/4× 更新率。这是当前驱动的实现选择，并非硬件采样率上限。多路 PCM 与音乐共享 CPU 预算，无法持续运行的 NSF 组合会拒绝导出。
- 含 PCM 的 **NSF/NSFe 需要支持 NSF2 IRQ 的播放器**，并使用工程对应的 PAL/NTSC 制式。NSF2 PCM 暂不能与 EPSM 同时导出；NSF 导入不会将软件 PCM 自动还原为可编辑采样轨道。
- 含 PCM 的 **NES 导出为 1× 静态音乐播放器，同时支持一路 PCM**，不同 DAC 可在同一音乐时刻交接。APU-only 与 VRC6 工程使用 **mapper 24，并需要 8 KiB WRAM**；MMC5 工程使用 **mapper 5**。这些模板不支持任意多扩展组合。导出按实际 NES 时钟重新采样，保持音高与时长。
- 游戏集成需要为 PCM 供样预留 CPU、IRQ、内存及换页资源，并协调音乐更新和画面工作；导出的音乐播放器不能直接代替游戏中的时序安排。
- **本版仅提供 Windows x64 预编译程序**。其他平台需要同时重建对应原生音频库，不能只替换托管程序集。

感谢 BleuBleu 和 FamiStudio 的所有贡献者。FamiStudio Extended 是独立分支，保留上游版权与许可证。

---

## English

**FamiStudio Extended r12** brings software PCM to 2A03, MMC5 and VRC6, from sample management and keyboard mapping to audition, audio rendering, sound-engine, NSF2 and NES export. It is based on FamiStudio 4.5.3 and retains r10’s tuning, mixing and instrument controls.

### Added and improved

- **Separate DPCM and PCM libraries** with independent source data, processing settings, folders and ordering. Samples with matching names remain separate assets.
- **2A03 sample mapping** through one keyboard map, with DPCM or PCM assigned to each key. PCM uses the native 7-bit DAC and alternates with DPCM on the same hardware voice.
- **MMC5 PCM-only mappings** on MMC5 instruments, with direct reuse of regular 2A03 instruments' PCM keys. Playback uses the independent 8-bit DAC; both MMC5 pulse voices remain available.
- **VRC6 PCM** on either pulse, with 4-bit output. A sample occupies its selected pulse; unmapped notes restore ordinary pulse playback. The sawtooth remains available.
- **Sample editing and playback** with waveform editing, audition, transpose, looping, volume, live pan and audio export. New controls and messages support all seven existing interface languages.
- **Matching export paths** for the sound engine, NSF2 and dedicated NES players, including PCM/ordinary-voice handoff and improved short-loop handling. Saving, text interchange, clipboard, undo and project merging recognize both libraries. Earlier shared-sample projects migrate when opened.
- **Finer editing grids**: switching between 1×/2×/4× converts existing song timelines while retaining musical speed and envelope duration. The 4× grid supports NTSC 240 Hz / PAL 200 Hz note placement. Inexact conversions leave the project intact with an explanation.
- **Dedicated Save As icon** beside Save, retaining keyboard shortcuts and localized help.
- Correct selection of different grooves with the same BPM and first-frame timing for beginning-padded grooves in NSF exports.

### Downloads

Download the [Windows x64 build](https://github.com/CharizeATOMIC/FamiStudio-Extended/releases/download/r12/FamiStudio-Extended-4.5.3-r12-win-x64.zip), extract the entire archive and run `FamiStudio-Extended.exe`. No separate .NET installation is required.

- [PCM examples](https://github.com/CharizeATOMIC/FamiStudio-Extended/releases/download/r12/PCM-r12.zip)
- [Source at r12](https://github.com/CharizeATOMIC/FamiStudio-Extended/tree/r12) · [Source ZIP](https://github.com/CharizeATOMIC/FamiStudio-Extended/releases/download/r12/FamiStudio-Extended-r12-source.zip)
- [Incremental Git history (requires r10)](https://github.com/CharizeATOMIC/FamiStudio-Extended/releases/download/r12/FamiStudio-Extended-r12.bundle)
- [PCM game-integration guide](https://github.com/CharizeATOMIC/FamiStudio-Extended/blob/r12/Docs/docs/development/mmc5-pcm-integration.md)

### Compatibility and scope

- **New saves use project format 1010.** Earlier releases and official FamiStudio cannot open them. Use Save As to retain an original copy.
- Editor and NSF2 PCM run at approximately **4 kHz**, independently of 1×/2×/4× music updates. This is an implementation choice, not a fixed hardware sample-rate ceiling. Concurrent PCM and music share a CPU budget; NSF combinations that cannot sustain playback are rejected.
- PCM **NSF/NSFe exports require an NSF2 IRQ-capable player** and the project’s PAL/NTSC region. NSF2 PCM currently cannot coexist with EPSM. NSF import does not reconstruct editable software-PCM sample tracks.
- PCM **NES exports are static music players with 1× updates and one active PCM voice**. Different DACs may hand off on the same music tick. APU-only and VRC6 projects use **mapper 24 with 8 KiB WRAM**; MMC5 projects use **mapper 5**. These templates do not support arbitrary multi-expansion layouts. Samples are resampled for the NES clock to preserve pitch and duration.
- Game integration must reserve CPU time, IRQ service, memory and banking resources, and coordinate music updates with graphics. The exported music player does not replace a game’s interrupt and timing design.
- **Prebuilt binaries are provided only for Windows x64.** Other platforms also require rebuilt target-native audio libraries, not just updated managed assemblies.

Thanks to BleuBleu and all FamiStudio contributors. FamiStudio Extended is an independent fork and retains upstream copyright and license notices.
