using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FamiStudio;

static class ChipCurveRegression
{
    static int checks;
    static void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; Console.WriteLine("PASS " + text); }
    static bool Rejects(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    static byte[] Snapshot(Project p) { var b = new ProjectSaveBuffer(p); p.Serialize(b); return b.GetBuffer(); }

    static Project Make(int mask = 0)
    {
        var p = Program.MakeProject();
        p.SetExpansionAudioMask(mask, 8);
        var s = p.Songs[0];
        s.TuningSettings = new TuningSettings();
        s.GetPatternCustomSettings(1).Tuning = new TuningSettings();
        foreach (var c in s.Channels.Where(c => c.SupportsEffect(Note.EffectFinePitch)))
        {
            var inst = p.Instruments.FirstOrDefault(i => i.Expansion == c.Expansion) ?? p.CreateInstrument(c.Expansion);
            c.GetOrCreatePattern(0).Notes[0] = new Note(49) { Instrument = inst, Duration = 64 };
        }
        return p;
    }

    public static int Run(string output)
    {
        checks = 0;
        output = Path.Combine(Path.GetFullPath(output), "chip-curves");
        Directory.CreateDirectory(output);
        var p = Make(127);
        var s = p.Songs[0];
        Check(!p.ChipTuning.HasAnyOffsets && !s.UsesCustomTuning, "zero project curves preserve legacy tuning mode");
        foreach (var c in s.Channels)
        foreach (bool pal in new[] { false, true })
            Check(NesApu.GetNoteTableForChannelType(c.Type, pal, 8).SequenceEqual(NesApu.GetNoteTableForChannelType(c.Type, pal, 8, 440, null, p.ChipTuning)), $"zero curve is bit-identical for channel {c.Type}, PAL {pal}");

        p.ChipTuning.SetValue(ExpansionType.None, 49, p.ChipTuning.GetDefaultValue(ExpansionType.None, 49) + 7);
        Check(p.HasActiveChipTuning && s.UsesCustomTuning, "project curve enables custom engine tables without a song override");
        Check(!p.ChipTuning.HasOverride(ExpansionType.None, 37) && !p.ChipTuning.HasOverride(ExpansionType.Vrc6, 49), "curve points are independent across octaves and chips");
        var baseTable = NesApu.GetNoteTableForChannelType(ChannelType.Square1, false, 8);
        var edited = NesApu.GetNoteTableForChannelType(ChannelType.Square1, false, 8, 440, null, p.ChipTuning);
        Check(edited[49] != baseTable[49] && Enumerable.Range(0, 97).Where(n => n != 49).All(n => edited[n] == baseTable[n]), "one edited point leaves every other legacy timer unchanged");
        foreach (int unpitched in new[] { ChannelType.Noise, ChannelType.Dpcm, ChannelType.Mmc5Dpcm, ChannelType.EPSMrythm1 })
            Check(NesApu.GetNoteTableForChannelType(unpitched, false, 8).SequenceEqual(NesApu.GetNoteTableForChannelType(unpitched, false, 8, 440, new int[12], p.ChipTuning)), $"unpitched channel {unpitched} ignores curves and temperament");
        Check(Rejects(() => p.ChipTuning.SetValue(0, 46, p.ChipTuning.GetHardwareMaximumValue(0, 46) + 1)) && Rejects(() => p.ChipTuning.SetValue(0, 46, p.ChipTuning.GetHardwareMinimumValue(0, 46) - 1)), "curve rejects values outside representable hardware bounds");
        Check(Rejects(() => p.ChipTuning.SetValue(-1, 1, 0)) && Rejects(() => p.ChipTuning.SetValue(0, 0, 0)) && Rejects(() => p.ChipTuning.SetValue(0, 97, 0)), "curve rejects invalid chips and notes");

        for (int chip = 0; chip < ExpansionType.Count; chip++)
            for (int note = 1; note <= 96; note++)
                p.ChipTuning.SetValue(chip, note, Math.Clamp(p.ChipTuning.GetDefaultValue(chip, note) + (note + chip * 7) % 59 - 29, p.ChipTuning.GetMinimumValue(chip, note), p.ChipTuning.GetMaximumValue(chip, note)));
        var expected = p.ChipTuning.Clone();
        var cloned = p.DeepClone();
        Check(cloned.ChipTuning.ContentEquals(expected), "binary deep clone preserves all 768 integer hardware curve values");
        cloned.ChipTuning.ResetChip(0);
        Check(!cloned.ChipTuning.HasOffsets(0) && p.ChipTuning.HasOffsets(0), "curve clones do not alias source arrays");
        var assignment = p.ChipTuning.Clone(); cloned.ChipTuning = assignment; assignment.ResetChip(1);
        Check(cloned.ChipTuning.HasOffsets(1), "project curve setter takes an independent snapshot");

        string fms = Path.Combine(output, "all-curves.fms"), txt = Path.ChangeExtension(fms, "txt");
        Check(new ProjectFile().Save(p, fms), "current format curve project saves");
        Check(BitConverter.ToInt32(File.ReadAllBytes(fms), 4) == Project.Version, "curve projects declare the current fork format version");
        Check(new ProjectFile().Load(fms).ChipTuning.ContentEquals(expected), "curve binary round trip preserves every bit");
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(new FamistudioTextFile().Save(p, txt, new[] { s.Id }, false), "curve text export works with comma-decimal UI culture");
            Check(new FamistudioTextFile().Load(txt).ChipTuning.ContentEquals(expected), "curve text import round-trips integer hardware values independently of culture");
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        var undo = new ProjectSaveBuffer(p, ProjectBufferFlags.UndoRedo); p.Serialize(undo);
        p.ChipTuning.ResetChip(2);
        p.Serialize(new ProjectLoadBuffer(p, undo.GetBuffer(), Project.Version, ProjectBufferFlags.UndoRedo));
        Check(p.ChipTuning.ContentEquals(expected), "project undo restores curve points and quantization cache state");
        var legacy = new ProjectFile().Load(Path.Combine("Tests", "TuningRegression", "Fixtures", "format1001.fms"));
        Check(legacy != null && !legacy.ChipTuning.HasAnyOffsets && legacy.Songs.Any(song => song.UsesCustomTuning), "format 1001 projects retain song tuning and receive zero chip curves");

        var source = Make(); source.Songs[0].Name = "Imported curve"; source.ChipTuning.SetValue(0, 46, source.ChipTuning.GetDefaultValue(0, 46) + 5);
        var target = Make(); var before = Snapshot(target);
        Check(!target.MergeProject(source.DeepClone()) && before.SequenceEqual(Snapshot(target)), "incompatible global curves reject song merge without mutating destination");
        target.ChipTuning = source.ChipTuning;
        Check(target.MergeProject(source.DeepClone()), "matching global curves allow song merge");
        var empty = new Project();
        Check(empty.MergeProject(source.DeepClone()) && empty.ChipTuning.ContentEquals(source.ChipTuning), "empty destination adopts imported project curve");
        var instrumentsOnly = source.DeepClone(); instrumentsOnly.DeleteAllSongs();
        Check(new Project().MergeProject(instrumentsOnly), "instrument-only imports do not require matching project curves");

        // Recover exact APU timers from a deliberately nonuniform game-like table.
        // No production quantizer is used to compute the expected integer periods.
        var recovered = new ChipTuningSettings();
        for (int note = 13; note <= 72; note++)
        {
            double nominal = 440 * Math.Pow(2, (note - 46) / 12.0);
            int period = (int)Math.Round(NesApu.FreqNtsc / 16.0 / nominal - 1) + (note % 3) - 1;
            recovered.SetValue(0, note, period);
            Check(NesApu.GetNoteTableForChannelType(ChannelType.Square1, false, 1, 440, null, recovered)[note] == period, $"recovered game period is exact at note {note}");
        }
        var additive = new ChipTuningSettings(); additive.SetValue(0, 46, additive.GetDefaultValue(0, 46) + 9);
        var offsets = new int[12]; offsets[9] = -17;
        int result = NesApu.GetNoteTableForChannelType(ChannelType.Square1, false, 1, 432, offsets, additive)[46];
        double desired = NesApu.FreqNtsc / 16.0 / (additive.GetValue(0, 46) + 1) * 432 / 440 * Math.Pow(2, -17 / 1200.0);
        int nearest = Enumerable.Range(8, 2040).OrderBy(timer => Math.Abs(Math.Log((NesApu.FreqNtsc / 16.0 / (timer + 1)) / desired))).First();
        Check(result == nearest, "integer period curve combines with song reference ratio and twelve-note temperament");

        CheckIntegerUnits(output);
        CheckManualRange(output);

        CheckNsfAndEditor(output);
        Console.WriteLine($"All {checks} chip-curve checks passed.");
        return checks;
    }

    static void CheckManualRange(string output)
    {
        var p = Make(); var s = p.Songs[0]; var curve = p.ChipTuning;
        int normal = curve.GetDefaultValue(0, 46);
        Check(curve.GetMinimumValue(0, 46) == normal - 50 && curve.GetMaximumValue(0, 46) == normal + 50, "graph drag band is the FamiStudio default plus/minus 50");
        curve.SetValue(0, 46, 1000);
        Check(curve.GetValue(0, 46) == 1000 && curve.GetValue(0, 46) > curve.GetMaximumValue(0, 46), "manual integer entry can exceed the graph drag band");
        curve.Validate();
        Check(p.DeepClone().ChipTuning.GetValue(0, 46) == 1000, "clone and undo serialization preserve manual values outside the drag band");
        string binary = Path.Combine(output, "manual-period.fms"), text = Path.ChangeExtension(binary, "txt");
        Check(new ProjectFile().Save(p, binary) && new ProjectFile().Load(binary).ChipTuning.GetValue(0, 46) == 1000, "binary save/load preserves out-of-band manual integer values");
        Check(new FamistudioTextFile().Save(p, text, new[] { s.Id }, false) && new FamistudioTextFile().Load(text).ChipTuning.GetValue(0, 46) == 1000, "text save/load preserves out-of-band manual integer values");
        s.Channels[0].GetOrCreatePattern(0).Notes[0].Value = 46;
        var editor = new CapturePlayer(false, false); editor.Render(s);
        int actual = editor.Frames[8][0x4002] | ((editor.Frames[8][0x4003] & 7) << 8);
        Check(actual == 1000, "editor emits exact manually entered APU timer 1000");
        string nsfPath = Path.Combine(output, "manual-period.nsf");
        Check(new NsfFile().Save(p, FamiToneKernel.FamiStudio, nsfPath, new[] { s.Id }, "Manual period", "Test", "", MachineType.NTSC, false), "NSF exports a manual value outside the graph drag band");
        var nsf = NotSoFatso.NsfOpen(nsfPath);
        try
        {
            if (nsf == IntPtr.Zero) throw new Exception("Manual-period NSF failed to open.");
            NotSoFatso.NsfSetTrack(nsf, 0);
            for (int frame = 0; frame < 9; frame++) NotSoFatso.NsfRunFrame(nsf);
            Check(NotSoFatso.NsfGetState(nsf, ChannelType.Square1, NotSoFatso.STATE_PERIOD, 0) == 1000, "NSF CPU execution emits exact manually entered APU timer 1000");
        }
        finally { if (nsf != IntPtr.Zero) NotSoFatso.NsfClose(nsf); }
        curve.ClearPoint(0, 46);
        Check(!curve.HasAnyOffsets && curve.GetValue(0, 46) == normal, "reset restores default after an out-of-band manual edit");
    }

    static void CheckIntegerUnits(string output)
    {
        var basis = new ChipTuningSettings(); basis.ConfigureBasis(432, true, 5);
        Check(basis.BasisReferenceHz == 432 && basis.BasisPal && basis.BasisN163Channels == 5, "curve basis records reference, region and N163 channels");
        var channels = new[] { ChannelType.Square1, ChannelType.Vrc6Square1, ChannelType.Vrc7Fm1, ChannelType.FdsWave, ChannelType.Mmc5Square1, ChannelType.N163Wave1, ChannelType.S5BSquare1, ChannelType.EPSMSquare1 };
        foreach (bool pal in new[] { false, true })
        {
            var curve = new ChipTuningSettings(); curve.ConfigureBasis(432, pal, 5);
            for (int chip = 0; chip < ExpansionType.Count; chip++)
            for (int note = 1; note <= 96; note++)
            {
                int normal = curve.GetDefaultValue(chip, note);
                int value = normal + 5 <= curve.GetMaximumValue(chip, note) ? normal + 5 : normal - 5;
                value = Math.Clamp(value, curve.GetMinimumValue(chip, note), curve.GetMaximumValue(chip, note));
                curve.SetValue(chip, note, value);
                var table = CustomTuningTables.Get(channels[chip], pal, 5, 432, null, curve);
                int actual = table.Periods[note];
                if (chip == ExpansionType.N163) actual = (actual << 2) | table.Fraction[note];
                if (chip == ExpansionType.Vrc7) actual >>= curve.GetFmBlock(chip, note);
                if (chip == ExpansionType.S5B) actual++;
                Check(actual == value, $"absolute integer chip {chip} note {note} PAL {pal} produces exactly requested hardware value {value}");
                curve.ClearPoint(chip, note);
                if (note == 96) Check(!curve.HasOffsets(chip), $"clearing all points restores default chip {chip} PAL {pal}");
            }
        }
        basis.SetValue(0, 46, basis.GetDefaultValue(0, 46) + 5);
        var p = Make(); p.ChipTuning = basis;
        string fms = Path.Combine(output, "pal-basis.fms"), txt = Path.ChangeExtension(fms, "txt");
        Check(new ProjectFile().Save(p, fms) && new ProjectFile().Load(fms).ChipTuning.ContentEquals(basis), "binary saves persisted PAL/432Hz/five-channel reference basis");
        Check(new FamistudioTextFile().Save(p, txt, new[] { p.Songs[0].Id }, false) && new FamistudioTextFile().Load(txt).ChipTuning.ContentEquals(basis), "text saves absolute integer values and persisted reference basis");
        int raw = basis.GetValue(0, 46);
        int converted = NesApu.GetNoteTableForChannelType(0, false, 1, 432, null, basis)[46];
        double desired = NesApu.FreqPal / 16.0 / (raw + 1);
        int nearest = Enumerable.Range(8, 2040).OrderBy(timer => Math.Abs(Math.Log((NesApu.FreqNtsc / 16.0 / (timer + 1)) / desired))).First();
        Check(converted == nearest, "region conversion retains pitch from the persisted hardware-clock basis");
        bool immutableBasis = false; try { basis.ConfigureBasis(440, false, 1); } catch (InvalidOperationException) { immutableBasis = true; }
        Check(immutableBasis, "an active curve cannot silently change its hardware reference basis");
        int originalDefault = basis.GetDefaultValue(0, 46); basis.SetValue(0, 46, originalDefault);
        Check(!basis.HasAnyOffsets && basis.GetValue(0, 46) == originalDefault, "entering the FamiStudio default removes the override");
    }

    static int HardwarePeriod(int type, CustomTuningTables table, int note)
    {
        int value = table.Periods[note];
        if (type >= ChannelType.N163Wave1 && type <= ChannelType.N163Wave8) value = (value << 2) | table.Fraction[note];
        if (type >= ChannelType.EPSMFm1 && type <= ChannelType.EPSMFm6)
        {
            int block = 0; while (value >= 512) { value >>= 1; block++; }
            value = ((value << 2) | table.Fraction[note]) << block;
        }
        return value;
    }

    static void CheckNsfAndEditor(string output)
    {
        foreach (bool pal in new[] { false, true })
        {
            var p = Make(127); var s = p.Songs[0];
            for (int chip = 0; chip < ExpansionType.Count; chip++) p.ChipTuning.SetValue(chip, 49, p.ChipTuning.GetDefaultValue(chip, 49) + (chip - 3) * 3 + 1);
            s.GetPatternCustomSettings(1).Tuning = new TuningSettings { Override = true, ReferenceHz = 432, Cents = new[] { 17, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } };
            var editor = new CapturePlayer(pal, p.OutputsStereoAudio); editor.Render(s);
            int editorPulse0 = editor.Frames[8][0x4002] | ((editor.Frames[8][0x4003] & 7) << 8);
            int editorPulse1 = editor.Frames[45][0x4002] | ((editor.Frames[45][0x4003] & 7) << 8);
            Check(editorPulse0 == NesApu.GetNoteTableForChannelType(0, pal, 8, 440, null, p.ChipTuning)[49], $"editor applies project-only curve PAL {pal}");
            Check(editorPulse1 == NesApu.GetNoteTableForChannelType(0, pal, 8, 432, s.GetTuning(1).Cents, p.ChipTuning)[49], $"editor combines curve with pattern tuning on a sustained note PAL {pal}");
            var path = Path.Combine(output, pal ? "curves-pal.nsf" : "curves-ntsc.nsf");
            Check(new NsfFile().Save(p, FamiToneKernel.FamiStudio, path, new[] { s.Id }, "Chip curves", "Test", "", pal ? MachineType.PAL : MachineType.NTSC, false), $"all-chip curve NSF exports PAL {pal}");
            var handle = NotSoFatso.NsfOpen(path);
            Check(handle != IntPtr.Zero, "curve NSF loads in independent CPU emulator");
            var epsm = new int[512]; int address0 = 0, address1 = 0;
            NotSoFatso.WriteRegisterDelegate callback = (reg, value) =>
            {
                if (reg == NesApu.EPSM_ADDR0) address0 = value;
                if (reg == NesApu.EPSM_ADDR1) address1 = value;
                if (reg == NesApu.EPSM_DATA0) epsm[address0] = value;
                if (reg == NesApu.EPSM_DATA1) epsm[256 + address1] = value;
            };
            try
            {
                NotSoFatso.NsfSetApuWriteCallback(handle, callback); NotSoFatso.NsfSetTrack(handle, 0);
                for (int frame = 0; frame < 48; frame++)
                {
                    NotSoFatso.NsfRunFrame(handle);
                    if (frame != 8 && frame != 45) continue;
                    int pattern = frame == 8 ? 0 : 1;
                    foreach (var c in s.Channels.Where(c => c.SupportsEffect(Note.EffectFinePitch)))
                    {
                        var table = CustomTuningTables.Get(c.Type, pal, 8, s.GetTuningHz(pattern), s.GetTuning(pattern)?.Cents, p.ChipTuning);
                        int expected = HardwarePeriod(c.Type, table, 49);
                        int actual = NotSoFatso.NsfGetState(handle, c.Type, NotSoFatso.STATE_PERIOD, 0);
                        if (c.IsVrc7Channel) actual <<= NotSoFatso.NsfGetState(handle, c.Type, NotSoFatso.STATE_FMOCTAVE, 0);
                        if (c.IsS5BChannel) actual--;
                        if (c.IsEPSMFmChannel)
                        {
                            int idx = c.Type - ChannelType.EPSMFm1, bank = idx < 3 ? 0 : 256, reg = idx % 3;
                            int high = epsm[bank + 0xa4 + reg];
                            actual = (((high & 7) << 8) | epsm[bank + 0xa0 + reg]) << ((high >> 3) & 7);
                        }
                        Check(actual == expected, $"curve NSF register matches editor table: channel {c.Type}, pattern {pattern}, PAL {pal}: {actual} = {expected}");
                    }
                }
            }
            finally { NotSoFatso.NsfClose(handle); GC.KeepAlive(callback); }
        }
    }
}
