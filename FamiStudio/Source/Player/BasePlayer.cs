using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace FamiStudio
{
    public enum LoopMode
    {
        LoopPoint,
        Song,
        Pattern,
        None,
        Max
    };

    public interface IPlayerInterface
    {
        void NotifyInstrumentLoaded(Instrument instrument, long channelTypeMask, int nativeApu);
        void NotifyRegisterWrite(int apuIndex, int reg, int data, int metadata = 0);
        int GetN163AutoWavePosition(Instrument instrument, int nativeApu = -1);
        ChannelState GetChannelByType(int type, int nativeApu = -1); // Use sparingly, kind of hacky.
    }

    public class BasePlayer : IPlayerInterface
    {
        protected int apuIndex;
        readonly List<int> instanceApus = new List<int>();
        void ReleaseExtraApus()
        {
            foreach (var apu in instanceApus) if (apu != apuIndex) { NesApu.CurrentSample[apu] = null; NesApu.Destroy(apu); }
            instanceApus.Clear();
        }
        int GetInstanceApu(int instance)
        {
            if (instanceApus.Count == 0) instanceApus.Add(apuIndex);
            while (instanceApus.Count <= instance)
            {
                var apu = NesApu.Create();
                if (apu < 0) throw new InvalidOperationException("No free audio emulation contexts.");
                instanceApus.Add(apu);
            }
            return instanceApus[instance];
        }
        protected NesApu.DmcReadDelegate dmcCallback;
        protected int sampleRate;
        protected int loopCount = 0;
        private bool hasLoopedSong;
        protected int maxLoopCount = -1;
        protected int frameNumber = 0;
        protected int playbackRate = 1; // 1 = normal, 2 = 1/2, 4 = 1/4, etc.
        protected int playbackRateCounter = 1;
        protected int minSelectedPattern = -1;
        protected int maxSelectedPattern = -1;
        protected int numPlayedPatterns = 0;
        protected bool famitrackerTempo = true;
        protected bool palPlayback = false;
        protected bool seeking = false;
        protected bool beat = false;
        protected bool stereo = false;
        public bool ApplySongPan { get; set; } = true;
        protected bool accurateSeek = false;
        protected bool forceReadRegisterValues = false;
        protected volatile bool reachedEnd = false;
        protected int  tndMode = NesApu.TND_MODE_SINGLE;
        protected int  beatIndex = -1;
        readonly Dictionary<int, Dictionary<int, int>> n163AutoWavPosMaps = new();
        protected Song song;
        protected ChannelState[] channelStates;
        protected LoopMode loopMode = LoopMode.Song;
        protected long channelMask = -1;
        protected volatile int playPosition = 0;
        protected NoteLocation playLocation = new NoteLocation(0, 0);
        protected NesApu.NesRegisterValues registerValues = new NesApu.NesRegisterValues();

        // Only used by FamiTracker tempo.
        protected int famitrackerTempoCounter = 0;
        protected int famitrackerSpeed = 6;

        // Only used by FamiStudio tempo.
        protected GrooveIterator grooveIterator;

        // Only used by FamiStudio tempo when doing adapted playback (NTSC -> PAL or vice-versa).
        protected byte[] tempoEnvelope;
        protected int tempoEnvelopeIndex;
        protected int tempoEnvelopeCounter;

        protected BasePlayer(int apu, bool pal, bool inStereo, int rate = 44100)
        {
            apuIndex = apu;
            sampleRate = rate;
            dmcCallback = new NesApu.DmcReadDelegate(NesApu.DmcReadCallback);
            stereo = inStereo;
            palPlayback = pal;
            registerValues.SetPalMode(pal);
        }

        public virtual void Shutdown()
        {
            ReleaseExtraApus();
        }

        public long ChannelMask
        {
            get { return Thread.VolatileRead(ref channelMask); }
            set { Thread.VolatileWrite(ref channelMask, value); }
        }

        public LoopMode Loop
        {
            get { return loopMode; }
            set { loopMode = value; }
        }

        public bool AccurateSeek
        {
            get { return accurateSeek; }
            set { accurateSeek = value; }
        }

        public virtual int PlayPosition
        {
            get { return Math.Max(0, playPosition); }
            set { playPosition = value; }
        }

        public int PlayRate
        {
            get { return playbackRate; }
            set
            {
                Debug.Assert(value == 1 || value == 2 || value == 4);
                playbackRate = value;
            }
        }
        
        public void SetSelectionRange(int min, int max)
        {
            minSelectedPattern = min;
            maxSelectedPattern = max;
        }

        // Returns the number of frames to run (0, 1 or 2)
        protected int UpdateFamiStudioTempo()
        {
            if (!playLocation.IsValid || famitrackerTempo || song.Project.PalMode == palPlayback)
            {
                return 1;
            }
            else
            {
                if (--tempoEnvelopeCounter <= 0)
                {
                    tempoEnvelopeIndex++;

                    if (tempoEnvelope[tempoEnvelopeIndex] == 0x80)
                        tempoEnvelopeIndex = 1;

                    tempoEnvelopeCounter = tempoEnvelope[tempoEnvelopeIndex];

#if FALSE //DEBUG
                    if (song.Project.PalMode)
                        Debug.WriteLine("*** Will do nothing for 1 frame!");
                    else
                        Debug.WriteLine("*** Will run 2 frames!"); 
#endif

                    // A NTSC song playing on PAL will sometimes need to run 2 frames to keep up.
                    // A PAL song playing on NTSC will sometimes need to do nothing for 1 frame to avoid going too fast.
                    return palPlayback ? 2 : 0;
                }
                else
                {
                    return 1;
                }
            }
        }

        private void ResetFamiStudioTempo()
        {
            if (!famitrackerTempo)
            {
                var newGroove = song.GetPatternGroove(playLocation.PatternIndex);
                var newGroovePadMode = song.GetPatternGroovePaddingMode(playLocation.PatternIndex);

                FamiStudioTempoUtils.ValidateGroove(newGroove);

                grooveIterator = new GrooveIterator(newGroove, newGroovePadMode);

                tempoEnvelope = FamiStudioTempoUtils.GetTempoEnvelope(newGroove, newGroovePadMode, song.Project.PalMode);
                tempoEnvelopeCounter = tempoEnvelope[0];
                tempoEnvelopeIndex = 0;
            }
        }

        protected bool ShouldAdvanceSong()
        {
            if (!playLocation.IsValid) // Here we are really "before the first frame"
            {
                return true;
            }
            else
            {
                if (famitrackerTempo)
                {
                    return famitrackerTempoCounter <= 0;
                }
                else
                {
                    return !grooveIterator.IsPadFrame;
                }
            }
        }

        protected void UpdateTempo()
        {
            if (famitrackerTempo)
            {
                // Tempo/speed logic straight from Famitracker.
                var tempoDecrement = (song.FamitrackerTempo * 24) / famitrackerSpeed;
                var tempoRemainder = (song.FamitrackerTempo * 24) % famitrackerSpeed;

                if (famitrackerTempoCounter <= 0)
                {
                    int ticksPerSec = palPlayback ? 50 : 60;
                    famitrackerTempoCounter += (60 * ticksPerSec) - tempoRemainder;
                }

                famitrackerTempoCounter -= tempoDecrement;
            }
            else
            {
                grooveIterator.Advance();
            }
        }

        protected void AdvanceChannels()
        {
            foreach (var channel in channelStates)
            {
                channel.Advance(song, playLocation, ref famitrackerSpeed, hasLoopedSong && loopMode != LoopMode.Pattern);
            }
        }

        protected void UpdateChannels()
        {
            // Main channel update, advance song, update APU, etc.
            foreach (var channel in channelStates)
            {
                channel.Update();
            }

            // This will do the phase resets (if any) this attempts to mimic the sound engine a bit more closely.
            foreach (var channel in channelStates)
            {
                channel.PostUpdate();
            }
        }

        protected void EnableChannelType(int channelType, bool enable, int nativeApu = -1)
        {
            var exp = ChannelType.GetExpansionTypeForChannelType(channelType);
            var idx = ChannelType.GetExpansionChannelIndexForChannelType(channelType);

            NesApu.EnableChannel(nativeApu < 0 ? apuIndex : nativeApu, exp, idx, enable ? 1 : 0);
        }

        protected void InitAndResetApu(Project project)
        {
            var expMixerSettings = new ExpansionMixer[ExpansionType.Count];

            // Apply project overrides.
            for (int i = 0; i < expMixerSettings.Length; i++)
            {
                expMixerSettings[i] = project.AllowMixerOverride && project.ExpansionMixerSettings[i].Override ?
                    project.ExpansionMixerSettings[i] :
                    Settings.ExpansionMixerSettings[i];
            }

            if (instanceApus.Count == 0) instanceApus.Add(apuIndex);
            for (int instance = 0; instance < instanceApus.Count; instance++)
            {
                var nativeApu = instanceApus[instance];
                var channels = song == null ? Array.Empty<Channel>() : song.Channels.Where(c => c.ChipInstance == instance).ToArray();
                var panChannels = ApplySongPan && channels.Any(c => c.StereoEnabled) ? channels : Array.Empty<Channel>();
                NesApu.ConfigureOutput(nativeApu, stereo ? 1 : 0, panChannels.Length,
                    panChannels.Select(c => c.Expansion).ToArray(), panChannels.Select(c => c.ExpansionChannelIndex).ToArray(), panChannels.Select(c => c.EffectivePan).ToArray());
                NesApu.InitAndReset(
                    nativeApu,
                    sampleRate,
                    Settings.GlobalVolumeDb,
                    project.OverrideBassCutoffHz ? project.BassCutoffHz : Settings.BassCutoffHz,
                    expMixerSettings,
                    palPlayback,
                    Settings.N163Mix,
                    tndMode,
                    project.ExpansionAudioMask,
                    project.ExpansionNumN163Channels,
                    dmcCallback);
                for (int type = 0; type < ChannelType.Count; type++) if (project.IsChannelActive(type)) EnableChannelType(type, false, nativeApu);
            }
        }

        protected void UpdateChannelsMuting()
        {
            for (int i = 0; i < channelStates.Length; i++)
            {
                var state = channelStates[i];

                EnableChannelType(state.InnerChannelType, (channelMask & (1L << i)) != 0, state.NativeApuIndex);
            }
        }

        public void BeginPlaySong(Song s)
        {
            Debug.Assert(s.Project.OutputsStereoAudio == stereo);

            ReleaseExtraApus();
            song = s;
            n163AutoWavPosMaps.Clear();
            for (int instance = 0; instance < song.Project.GetExpansionInstances(ExpansionType.N163); instance++)
            {
                song.Project.AutoAssignN163WavePositions(out var positions, instance);
                n163AutoWavPosMaps[instance] = positions;
            }
            famitrackerTempo = song.UsesFamiTrackerTempo;
            famitrackerSpeed = song.FamitrackerSpeed;
            playLocation = NoteLocation.Invalid; // This means "before the first frame".
            frameNumber = 0;
            loopCount = 0;
            hasLoopedSong = false;
            famitrackerTempoCounter = 0;
            channelStates = CreateChannelStates(song.Project, apuIndex, song.GetTuningHz(), palPlayback, song.Project.ExpansionNumN163Channels);
            reachedEnd = false;
            playbackRateCounter = 1;
            tempoEnvelopeCounter = 0;

            InitAndResetApu(song.Project);
            UpdateChannelsMuting();
        }

        public bool SeekTo(int startNote)
        {
            Debug.Assert(startNote > 0);

            StartSeeking();

            var stillSeeking = true;

            while (true)
            {
                // We 1 frame before the target so that we "advance" to the correct frame on the first PlaySongFrame().
                var reachedStartNote = playLocation.IsValid && playLocation.ToAbsoluteNoteIndex(song) >= startNote - 1;
                if (reachedStartNote || !PlaySongFrameInternal(true))
                {
                    stillSeeking = false;
                    break;
                }

                // In accurate seek mode, this can be slow, so the caller must call in a loop until we return false.
                if (accurateSeek)
                {
                    EndSeekFrame();
                    break;
                }
            }

            StopSeeking();

            return stillSeeking;
        }

        protected bool PlaybackRateShouldSkipFrame(bool seeking)
        {
            if (seeking)
                return false;

            if (--playbackRateCounter > 0)
                return true;

            Debug.Assert(playbackRateCounter == 0);
            playbackRateCounter = playbackRate;
            return false;
        }

        protected bool PlaySongFrameInternal(bool seeking)
        {
            ClearBeat();

            //Debug.WriteLine($"PlaySongFrameInternal {playPosition}!");
            //Debug.WriteLine($"PlaySongFrameInternal {song.GetPatternStartNote(playPattern) + playNote}!");

            if (PlaybackRateShouldSkipFrame(seeking))
                return true;

            int numFramesToRun = UpdateFamiStudioTempo();

            for (int i = 0; i < numFramesToRun; i++)
            {
                if (ShouldAdvanceSong())
                {
                    //Debug.WriteLine($"  Seeking Frame {song.GetPatternStartNote(playPattern) + playNote}!");

                    if (!AdvanceSong(song.Length, seeking ? LoopMode.None : loopMode))
                    {
                        reachedEnd = true;
                        return false;
                    }

                    AdvanceChannels();
                }

                UpdateChannels();
                UpdateTempo();

#if DEBUG
                if (i > 0)
                {
                    var noteLength = song.GetPatternNoteLength(playLocation.PatternIndex);
                    if ((playLocation.NoteIndex % noteLength) == 0 && noteLength != 1)
                        Debug.WriteLine("*********** INVALID SKIPPED NOTE!");
                }
#endif
            }

            if (!seeking)
                playPosition = playLocation.ToAbsoluteNoteIndex(song);

            frameNumber++;

            return true;
        }

        public bool PlaySongFrame()
        {
            BeginFrame();

            if (!PlaySongFrameInternal(false))
                return false;

            UpdateChannelsMuting();
            EndFrame();

            return true;
        }

        protected void StartSeeking()
        {
            seeking = true;
            if (!accurateSeek)
                foreach (var apu in instanceApus) NesApu.StartSeeking(apu);
        }

        protected void StopSeeking()
        {
            if (!accurateSeek)
                foreach (var apu in instanceApus) NesApu.StopSeeking(apu);
            seeking = false;
        }

        protected bool AdvanceSong(int songLength, LoopMode loopMode)
        {
            bool advancedPattern = false;

            // Pretend we "advance" to the first note on the first frame.
            if (!playLocation.IsValid)
            {
                playLocation = new NoteLocation(0, 0);
                advancedPattern = true;
            }
            else
            {
                if (++playLocation.NoteIndex >= song.GetPatternLength(playLocation.PatternIndex))
                {
                    playLocation.NoteIndex = 0;

                    if (loopMode != LoopMode.Pattern)
                    {
                        playLocation.PatternIndex++;
                        advancedPattern = true;
                    }
                    else
                    {
                        // Make sure the selection is valid, updated on another thread, so could be sketchy.
                        var minPatternIdx = minSelectedPattern;
                        var maxPatternIdx = maxSelectedPattern;

                        if (minPatternIdx >= 0 &&
                            maxPatternIdx >= 0 &&
                            maxPatternIdx >= minPatternIdx &&
                            minPatternIdx < song.Length)
                        {
                            if (playLocation.PatternIndex + 1 > maxPatternIdx)
                            {
                                playLocation.PatternIndex = minPatternIdx;
                            }
                            else
                            {
                                playLocation.PatternIndex++;
                                advancedPattern = true;
                            }
                        }
                    }
                }

                if (playLocation.PatternIndex >= songLength)
                {
                    if (maxLoopCount > 0 && ((loopMode == LoopMode.LoopPoint && song.LoopPoint >= 0) || loopMode == LoopMode.Song) && ++loopCount >= maxLoopCount)
                    {
                        return false;
                    }

                    if (loopMode == LoopMode.LoopPoint) // This loop mode is actually unused.
                    {
                        if (song.LoopPoint >= 0)
                        {
                            playLocation.PatternIndex = song.LoopPoint;
                            hasLoopedSong = true;
                            playLocation.NoteIndex = 0;
                            advancedPattern = true;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    else if (loopMode == LoopMode.Song)
                    {
                        playLocation.PatternIndex = Math.Max(0, song.LoopPoint);
                        hasLoopedSong = true;
                        playLocation.NoteIndex = 0;
                        advancedPattern = true;
                    }
                    else if (loopMode == LoopMode.None)
                    {
                        return false;
                    }
                }
            }

            if (advancedPattern)
            {
                numPlayedPatterns++;
                ResetFamiStudioTempo();
            }

            UpdateBeat();

            return true;
        }

        private void ClearBeat()
        {
            beat = false;
        }

        private void UpdateBeat()
        {
            if (!seeking)
            {
                var beatLength = song.GetPatternBeatLength(playLocation.PatternIndex);

                beat = playLocation.NoteIndex % beatLength == 0;
                beatIndex = playLocation.NoteIndex / beatLength;
            }
            else
            {
                beat = false;
                beatIndex = -1;
            }
        }

        private ChannelState CreateChannelState(int apuIdx, int channelType, int expNumChannels, int tuning, bool pal)
        {
            switch (channelType)
            {
                case ChannelType.Square1:
                case ChannelType.Square2:
                    return new ChannelStateSquare(this, apuIdx, channelType, tuning, pal);
                case ChannelType.Triangle:
                    return new ChannelStateTriangle(this, apuIdx, channelType, tuning, pal);
                case ChannelType.Noise:
                    return new ChannelStateNoise(this, apuIdx, channelType, tuning, pal);
                case ChannelType.Dpcm:
                    return new ChannelStateDpcm(this, apuIdx, channelType, tuning, pal);
                case ChannelType.Vrc6Square1:
                case ChannelType.Vrc6Square2:
                    return new ChannelStateVrc6Square(this, apuIdx, channelType, tuning, pal);
                case ChannelType.Vrc6Saw:
                    return new ChannelStateVrc6Saw(this, apuIdx, channelType, tuning, pal);
                case ChannelType.Vrc7Fm1:
                case ChannelType.Vrc7Fm2:
                case ChannelType.Vrc7Fm3:
                case ChannelType.Vrc7Fm4:
                case ChannelType.Vrc7Fm5:
                case ChannelType.Vrc7Fm6:
                    return new ChannelStateVrc7(this, apuIdx, channelType, tuning);
                case ChannelType.FdsWave:
                    return new ChannelStateFds(this, apuIdx, channelType, tuning, pal);
                case ChannelType.Mmc5Square1:
                case ChannelType.Mmc5Square2:
                    return new ChannelStateMmc5Square(this, apuIdx, channelType, tuning, pal);
                case ChannelType.N163Wave1:
                case ChannelType.N163Wave2:
                case ChannelType.N163Wave3:
                case ChannelType.N163Wave4:
                case ChannelType.N163Wave5:
                case ChannelType.N163Wave6:
                case ChannelType.N163Wave7:
                case ChannelType.N163Wave8:
                    return new ChannelStateN163(this, apuIdx, channelType, tuning, pal, expNumChannels);
                case ChannelType.S5BSquare1:
                case ChannelType.S5BSquare2:
                case ChannelType.S5BSquare3:
                    return new ChannelStateS5B(this, apuIdx, channelType, tuning, pal);
                case ChannelType.EPSMSquare1:
                case ChannelType.EPSMSquare2:
                case ChannelType.EPSMSquare3:
                    return new ChannelStateEPSMSquare(this, apuIdx, channelType, tuning, pal);
                case ChannelType.EPSMFm1:
                case ChannelType.EPSMFm2:
                case ChannelType.EPSMFm3:
                case ChannelType.EPSMFm4:
                case ChannelType.EPSMFm5:
                case ChannelType.EPSMFm6:
                    return new ChannelStateEPSMFm(this, apuIdx, channelType, tuning, pal);
                case ChannelType.EPSMrythm1:
                case ChannelType.EPSMrythm2:
                case ChannelType.EPSMrythm3:
                case ChannelType.EPSMrythm4:
                case ChannelType.EPSMrythm5:
                case ChannelType.EPSMrythm6:
                    return new ChannelStateEPSMRythm(this, apuIdx, channelType, tuning, pal);
            }

            Debug.Assert(false);
            return null;
        }

        protected ChannelState[] CreateChannelStates(Project project, int apuIdx, int tuning, bool pal, int expNumChannels)
        {
            var layout = project.GetChannelLayout();
            var states = new ChannelState[layout.Length];
            for (int i = 0; i < layout.Length; i++)
            {
                var native = apuIdx == NesApu.APU_INSTRUMENT ? apuIdx : GetInstanceApu(layout[i].Instance);
                states[i] = CreateChannelState(native, layout[i].Type, expNumChannels, tuning, pal);
                states[i].SongChannelIndex = i;
            }
            return states;
        }

        protected virtual void BeginFrame()
        {
            foreach (var apu in instanceApus) NesApu.ResetTriggers(apu);
        }

        private unsafe void EndSeekFrame()
        {
            Debug.Assert(accurateSeek);

            foreach (var apu in instanceApus) { NesApu.EndFrame(apu); NesApu.ReadSamples(apu, IntPtr.Zero, NesApu.SamplesAvailable(apu)); }
        }

        protected virtual unsafe short[] EndFrame()
        {
            short[] samples = null;
            int[] mixed = null;
            for (int instance = 0; instance < instanceApus.Count; instance++)
            {
                var apu = instanceApus[instance];
                if (song != null)
                {
                    int stem = 0;
                    foreach (var c in song.Channels) if (c.ChipInstance == instance) NesApu.SetPan(apu, stem++, c.EffectivePan);
                }
                NesApu.EndFrame(apu);
                var count = NesApu.SamplesAvailable(apu);
                var part = new short[count * (stereo ? 2 : 1)];
                fixed (short* ptr = part) NesApu.ReadSamples(apu, new IntPtr(ptr), count);
                if (samples == null) { samples = part; if (instanceApus.Count > 1) mixed = part.Select(v => (int)v).ToArray(); }
                else
                {
                    if (part.Length != samples.Length) throw new InvalidOperationException("Chip audio clocks lost synchronization.");
                    for (int i = 0; i < part.Length; i++) mixed[i] += part[i];
                }
            }
            if (mixed != null) for (int i = 0; i < samples.Length; i++) samples[i] = (short)Utils.Clamp(mixed[i], short.MinValue, short.MaxValue);

            ReadBackRegisterValues();

            return samples;
        }

        public void ForceInstrumentsReload()
        {
            if (channelStates != null)
            {
                foreach (var channelState in channelStates)
                    channelState.ForceInstrumentReload();
            }
        }

        public void NotifyInstrumentLoaded(Instrument instrument, long channelTypeMask, int nativeApu)
        {
            foreach (var channelState in channelStates)
            {
                if (channelState.NativeApuIndex == nativeApu && ((1L << channelState.InnerChannelType) & channelTypeMask) != 0)
                {
                    channelState.IntrumentLoadedNotify(instrument);
                }
            }
        }

        public virtual void NotifyRegisterWrite(int apuIndex, int reg, int data, int metadata = 0)
        {
        }

        public virtual ChannelState GetChannelByType(int type, int nativeApu = -1)
        {
            return Array.Find(channelStates, c => c.InnerChannelType == type && (nativeApu < 0 || c.NativeApuIndex == nativeApu));
        }

        public int GetN163AutoWavePosition(Instrument instrument, int nativeApu = -1)
        {
            Debug.Assert(instrument != null && instrument.IsN163 && instrument.N163WaveAutoPos);

            // Instrument preview plays one voice, so all its waves may start at zero.
            var pos = 0;
            var instance = nativeApu < 0 ? 0 : instanceApus.IndexOf(nativeApu);
            if (n163AutoWavPosMaps.TryGetValue(instance, out var positions))
                positions?.TryGetValue(instrument.Id, out pos);

            return pos;
        }

        protected void ReadBackRegisterValues()
        {
            if (Settings.ShowRegisterViewer || forceReadRegisterValues)
            {
                lock (registerValues)
                {
                    registerValues.ReadRegisterValues(apuIndex);

                    // Read some additionnal information that we may need for the
                    // register viewer, such as instrument colors, etc.
                    foreach (var state in channelStates)
                    {
                        if (state.NativeApuIndex == apuIndex) state.AddRegisterValuesExtraData(registerValues);
                    }
                }
            }
        }

        public void GetRegisterValues(NesApu.NesRegisterValues values)
        {
            lock (registerValues)
            {
                registerValues.CopyTo(values);
            }
        }

        protected int GetOscilloscopeTrigger(int channelType)
        {
            var expType = ChannelType.GetExpansionTypeForChannelType(channelType);
            var chanIdx = ChannelType.GetExpansionChannelIndexForChannelType(channelType);

            return NesApu.GetChannelTrigger(apuIndex, expType, chanIdx);
        }
    };
}
