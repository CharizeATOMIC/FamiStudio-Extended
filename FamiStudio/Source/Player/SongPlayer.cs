using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FamiStudio
{
    public class SongPlayer : AudioPlayer
    {
        public SongPlayer(IAudioStream stream, bool pal, int sampleRate, bool stereo, int numFrames) : base(stream, NesApu.APU_SONG, pal, sampleRate, stereo, numFrames)
        {
            loopMode = LoopMode.LoopPoint;
        }

        public override void Shutdown()
        {
            Stop();
            base.Shutdown();
        }

        public void Start(Song song)
        {
            if (audioStream == null)
                return;

            // Resampling can be expensive. Prepare only the samples used by
            // this arrangement before starting the real-time audio thread.
            if (song.Project.UsesPcm)
            {
                var patterns = new HashSet<Pattern>();
                var mappings = new HashSet<DPCMSampleMapping>();
                foreach (var channel in song.Channels)
                {
                    if (!channel.IsMmc5PcmChannel && !channel.IsDpcmChannel &&
                        channel.Type != ChannelType.Vrc6Square1 && channel.Type != ChannelType.Vrc6Square2) continue;
                    for (int i = 0; i < song.Length; i++)
                    {
                        var pattern = channel.PatternInstances[i];
                        if (pattern == null) continue;
                        patterns.Add(pattern);
                        if (pattern.LoopVariant != null) patterns.Add(pattern.LoopVariant);
                    }
                }
                foreach (var pattern in patterns)
                foreach (var note in pattern.Notes.Values)
                    if (note.IsMusical && note.Instrument?.GetPcmMapping(note.Value) is DPCMSampleMapping mapping)
                        mappings.Add(mapping);
                foreach (var mapping in mappings)
                    mapping.Sample?.GetPcmData(palPlayback, mapping.PcmPitch);
            }

            var startNote = playPosition;

            BeginPlaySong(song);

            if (startNote > 0)
            {
                if (UseAccurateSeeking)
                {
                    abortSeek = false;
                    seekTask = Task.Factory.StartNew(() => { while (SeekTo(startNote) && !abortSeek); });
                    return;
                }
                else
                {
                    while (SeekTo(startNote));
                }
            }

            StartInternal();
        }

        public void StartIfSeekComplete()
        {
            if (IsSeeking && seekTask.IsCompleted)
            {
                seekTask = null;
                StartInternal();
            }
        }

        private void StartInternal()
        {
            if (UsesEmulationThread)
            {
                Debug.Assert(emulationThread == null);
                Debug.Assert(emulationQueue.Count == 0);

                ResetThreadingObjects();

                emulationThread = new Thread(EmulationThread);
                emulationThread.Start();
            }

            audioStream.Stop(); // Extra safety
            audioStream.Start(AudioBufferFillCallback, AudioStreamStartingCallback);
        }

        public void Stop()
        {
            if (IsSeeking)
            {
                abortSeek = true;
                seekTask.Wait();
                seekTask = null;
            }

            if (UsesEmulationThread)
            {
                // Keeping a local variable of the thread since the song may
                // end naturally and may set playerThread = null after we have set
                // the stop event.
                var thread = emulationThread;
                if (thread != null)
                {
                    stopEvent.Set();
                    thread.Join();
                    Debug.Assert(emulationThread == null);
                }
            }

            // This is needed to prevent a deadlock in case we were in the middle priming 
            //the emulation queue in "AudioStreamStartingCallback". 
            reachedEnd = true;

            audioStream?.Stop();

            if (UsesEmulationThread)
            {
                // When stopping, reset the play position to the first frame in the queue,
                // this prevent the cursor from jumping ahead when playing/stopping quickly
                playPosition = PlayPosition;
                emulationQueue.Clear();
            }
        }

#if false // Enable to debug oscilloscope triggers for a specific channel of a song.
        protected override FrameAudioData GetFrameAudioData()
        {
            var data = base.GetFrameAudioData();
            data.triggerSample = NesApu.GetChannelTrigger(apuIndex, ExpansionType.Vrc7, 0);
            return data;
        }
#endif

        protected override bool EmulateFrame()
        {
            var advanced = PlaySongFrame();

            // When reaching the end of a non looping song, push a null to signal to stop the stream.
            if (!advanced && UsesEmulationThread)
                emulationQueue.Enqueue(null);

            return advanced;
        }
        
        private void EmulationThread(object o)
        {
            var waitEvents = new WaitHandle[] { stopEvent, emulationSemaphore };

            while (true)
            {
                if (WaitHandle.WaitAny(waitEvents) == 0)
                    break;

                if (!EmulateFrame())
                    break;
            }

            emulationThread = null;
        }
    };
}
