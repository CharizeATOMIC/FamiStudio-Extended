using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FamiStudio
{
    // The ordinary score remains in the normal sound engine. Only the PCM voice's
    // trigger/stop/volume commands are compiled to a sparse output-clock stream.
    public sealed class Mmc5PcmExportData
    {
        public sealed class Sample
        {
            public int Offset;
            public byte[] Data;
            public bool Loop;
        }
        public sealed class Event
        {
            public int Tick;
            public int Command; // 1 trigger, 2 stop, 0x10..0x1f volume
            public int SampleIndex;
            public int Destination; // 0 APU DAC, 1 MMC5 DAC, 2/3 VRC6 pulse DACs.
        }
        public sealed class Track
        {
            public int SongId;
            public int EndTick;
            public int LoopTick = -1;
            public readonly List<Event> Events = new List<Event>();
        }
        public readonly List<Sample> Samples = new List<Sample>();
        public readonly Dictionary<int, Track> Tracks = new Dictionary<int, Track>();
        public byte[] BankData { get; private set; }
        public int WindowAddress { get; set; } = 0x8000;
        public int BankSize { get; private set; } = 4096;
        public bool Pal { get; private set; }
        public double SampleRate { get; private set; }
        public int DestinationsMask { get; private set; }

        private sealed class CapturePlayer : BasePlayer
        {
            readonly Mmc5PcmExportData result;
            readonly Dictionary<(int, int, bool), int> mappings;
            readonly Project source;
            public readonly Track Track;
            public readonly List<int> Boundaries = new List<int>();
            public int Ticks => frameNumber;
            public CapturePlayer(Project project, Song song, Mmc5PcmExportData data, Dictionary<(int, int, bool), int> map)
                : base(NesApu.APU_WAV_EXPORT, data.Pal, project.OutputsStereoAudio)
            {
                result = data; mappings = map; source = project;
                Track = new Track { SongId = song.Id };
                loopMode = song.LoopPoint >= 0 ? LoopMode.LoopPoint : LoopMode.None;
                maxLoopCount = 3;
                BeginPlaySong(song);
            }
            public bool Step()
            {
                var previousLoop = loopCount;
                var running = PlaySongFrame();
                if (loopCount != previousLoop && running) Boundaries.Add(frameNumber - 1);
                return running;
            }
            public override void NotifyRegisterWrite(int apuIndex, int register, int value, int metadata = 0)
            {
                if (register < 0x20000 || register >= 0x20040) return;
                var destination = (register - 0x20000) >> 4;
                var command = register & 15;
                if (command > 3) return;
                result.DestinationsMask |= 1 << destination;
                if (command == 0)
                {
                    var mapping = source.GetInstrument(metadata)?.GetPcmMapping(value);
                    if (mapping?.Sample == null) throw new InvalidOperationException("PCM trigger has no sample mapping.");
                    var key = (mapping.Sample.Id, mapping.PcmPitch, mapping.Loop);
                    if (!mappings.TryGetValue(key, out var index))
                    {
                        var bytes = mapping.Sample.GetPcmData(result.Pal, mapping.PcmPitch, result.SampleRate);
                        if (bytes.Length == 0 || bytes.Length > 65535)
                            throw new InvalidOperationException($"PCM sample '{mapping.Sample.Name}' has {bytes.Length} output samples. The sound engine supports 1..65535 samples per mapping.");
                        index = result.Samples.Count;
                        result.Samples.Add(new Sample { Data = bytes, Loop = mapping.Loop });
                        mappings.Add(key, index);
                    }
                    Track.Events.Add(new Event { Tick = frameNumber, Command = 1, SampleIndex = index, Destination = destination });
                }
                else if (command == 1) Track.Events.Add(new Event { Tick = frameNumber, Command = 2, Destination = destination });
                else if (command == 2) Track.Events.Add(new Event { Tick = frameNumber, Command = 0x10 + value, Destination = destination });
                else if (command == 3) Track.Events.Add(new Event { Tick = frameNumber, Command = 6, Destination = destination });
            }
        }

        public static Mmc5PcmExportData Capture(Project project, int[] songIds, bool pal, double? sampleRate = null, int bankSize = 4096)
        {
            if (!project.UsesPcm) return null;
            if (pal != project.PalMode)
                throw new InvalidOperationException("PCM export must use the project's PAL/NTSC region. Export a separate project for the other region.");
            if (bankSize != 4096 && bankSize != 8192 && bankSize != 16384) throw new ArgumentOutOfRangeException(nameof(bankSize));
            var result = new Mmc5PcmExportData { BankSize = bankSize, Pal = pal, SampleRate = sampleRate ?? Mmc5PcmTiming.GetSampleRate(pal) };
            var mappings = new Dictionary<(int, int, bool), int>();
            foreach (var song in project.Songs.Where(s => songIds.Contains(s.Id)))
            {
                var player = new CapturePlayer(project, song, result, mappings);
                var limit = 60 * 60 * 60 * project.UpdateRateMultiplier;
                try
                {
                    while (player.Step())
                    {
                        if (player.Ticks > limit) throw new InvalidOperationException("PCM control stream exceeds the one-hour compilation limit.");
                        if ((player.Ticks & 255) == 0 && Log.ShouldAbortOperation) throw new InvalidOperationException("PCM export was cancelled.");
                    }
                }
                finally { player.Shutdown(); }
                var track = player.Track;
                track.EndTick = player.Ticks;
                if (player.Boundaries.Count >= 2)
                {
                    var first = player.Boundaries[0];
                    var second = player.Boundaries[1];
                    var end = player.Ticks;
                    var a = track.Events.Where(e => e.Tick >= first && e.Tick < second).Select(e => (e.Tick - first, e.Command, e.SampleIndex, e.Destination)).ToArray();
                    var b = track.Events.Where(e => e.Tick >= second && e.Tick < end).Select(e => (e.Tick - second, e.Command, e.SampleIndex, e.Destination)).ToArray();
                    if (second - first != end - second || !a.SequenceEqual(b))
                        throw new InvalidOperationException($"PCM commands in '{song.Name}' differ between repeated song loops. Add an explicit PCM attack or stop at the loop boundary before exporting.");
                    track.LoopTick = first;
                    track.EndTick = second;
                    track.Events.RemoveAll(e => e.Tick >= second);
                }
                result.Tracks.Add(song.Id, track);
            }
            var bytes = new List<byte>();
            foreach (var sample in result.Samples)
            {
                sample.Offset = bytes.Count;
                bytes.AddRange(sample.Data);
            }
            while ((bytes.Count & (bankSize - 1)) != 0) bytes.Add(128);
            if (bytes.Count > 255 * bankSize) throw new InvalidOperationException("PCM samples exceed the 255-bank sound-engine limit.");
            result.BankData = bytes.ToArray();
            return result;
        }
    }

    public partial class FamitoneMusicFile
    {
        private Mmc5PcmExportData suppliedPcmData;
        private Mmc5PcmExportData pcmData;
        public Mmc5PcmExportData PcmData { get => pcmData; set { suppliedPcmData = value; pcmData = value; } }
        private bool usesMmc5Pcm;

        private int OutputPcmStreams(string filename)
        {
            if (!usesMmc5Pcm) return 0;
            var size = 0;
            lines.Add("; PCM: call famistudio_pcm_service from a periodic IRQ, independently of famistudio_update.");
            lines.Add($"; Used destinations mask: ${PcmData.DestinationsMask:x2} (bit0 APU, bit1 MMC5, bits2/3 VRC6 pulse 1/2).");
            lines.Add($"; PCM bank size: {PcmData.BankSize}. Window: ${PcmData.WindowAddress:x4}. Sample clock: {PcmData.SampleRate:F6} Hz.");
            lines.Add("; Implement famistudio_pcm_bank_callback preserving X. For a temporary music window set FAMISTUDIO_PCM_TRANSIENT_WINDOW=1 and implement the restore callback.");
            lines.Add("; Default volume table: 4 KiB RAM at $6000-$6FFF, initialized once. FAMISTUDIO_PCM_VOLUME_TABLE_ROM=1 requires the same precomputed table in ROM.");
            for (var i = 0; i < PcmData.Samples.Count; i++)
            {
                var sample = PcmData.Samples[i];
                lines.Add($"{llp}pcm_sample_{i}:");
                lines.Add($"\t{dw} {PcmData.WindowAddress + (sample.Offset & (PcmData.BankSize - 1))}, {sample.Data.Length}");
                lines.Add($"\t{db} {sample.Offset / PcmData.BankSize}, {(sample.Loop ? 1 : 0)}");
                size += 6;
            }
            for (var i = 0; i < project.Songs.Count; i++)
            {
                var track = PcmData.Tracks[project.Songs[i].Id];
                lines.Add($"{llp}pcm_song_{i}:");
                var tick = 0;
                var loopWritten = false;
                Action<int> wait = target =>
                {
                    while (tick < target)
                    {
                        var count = Math.Min(65535, target - tick);
                        lines.Add($"\t{db} 0"); lines.Add($"\t{dw} {count}");
                        size += 3; tick += count;
                    }
                };
                // A trigger publishes a descriptor to the independent IRQ.
                // Set its same-tick gain first so an interrupt between commands
                // cannot consume the first sample at the previous gain.
                var orderedEvents = new List<Mmc5PcmExportData.Event>();
                foreach (var group in track.Events.GroupBy(e => e.Tick))
                {
                    // Give up a previous destination before starting another.
                    // Preserve genuine trigger-then-stop ordering on one voice.
                    var earlyStops = new HashSet<Mmc5PcmExportData.Event>();
                    var triggered = new HashSet<int>();
                    foreach (var e in group)
                    {
                        if (e.Command == 1) triggered.Add(e.Destination);
                        if ((e.Command == 2 || e.Command == 6) && !triggered.Contains(e.Destination))
                        { orderedEvents.Add(e); earlyStops.Add(e); }
                    }
                    orderedEvents.AddRange(group.Where(e => !earlyStops.Contains(e)));
                }
                for (int j = 0; j + 1 < orderedEvents.Count; j++)
                    if (orderedEvents[j].Command == 1 &&
                        orderedEvents[j + 1].Command >= 0x10 &&
                        orderedEvents[j].Tick == orderedEvents[j + 1].Tick &&
                        orderedEvents[j].Destination == orderedEvents[j + 1].Destination)
                    {
                        var trigger = orderedEvents[j];
                        orderedEvents[j] = orderedEvents[j + 1];
                        orderedEvents[j + 1] = trigger;
                        j++;
                    }
                foreach (var e in orderedEvents)
                {
                    if (!loopWritten && track.LoopTick >= 0 && track.LoopTick <= e.Tick)
                    {
                        wait(track.LoopTick); lines.Add($"{llp}pcm_song_{i}_loop:"); loopWritten = true;
                    }
                    wait(e.Tick);
                    // Explicit selection is also needed at loop entry: the
                    // previous iteration can finish on a different destination.
                    lines.Add($"\t{db} 5, {e.Destination}"); size += 2;
                    lines.Add($"\t{db} {e.Command}"); size++;
                    if (e.Command == 1) { lines.Add($"\t{dw} {llp}pcm_sample_{e.SampleIndex}"); size += 2; }
                }
                if (track.LoopTick >= 0 && !loopWritten)
                {
                    wait(track.LoopTick); lines.Add($"{llp}pcm_song_{i}_loop:");
                }
                wait(track.EndTick);
                if (track.LoopTick >= 0) { lines.Add($"\t{db} 3"); lines.Add($"\t{dw} {llp}pcm_song_{i}_loop"); size += 3; }
                else
                {
                    for (var destination = 0; destination < 4; destination++)
                        if ((PcmData.DestinationsMask & (1 << destination)) != 0)
                        { lines.Add($"\t{db} 5, {destination}, 2"); size += 3; }
                    lines.Add($"\t{db} 4"); size++;
                }
            }
            File.WriteAllBytes(Path.ChangeExtension(filename, ".pcm"), PcmData.BankData);
            return size;
        }
    }
}
