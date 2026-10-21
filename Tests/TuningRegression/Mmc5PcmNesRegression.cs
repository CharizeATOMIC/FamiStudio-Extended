// Compatibility command retained for scripts written before separate PCM libraries.
// The generalized suite includes MMC5, the original DPCM overflow protections,
// APU and both VRC6 pulse DACs, plus same-tick handoffs and overlap rejection.
static class Mmc5PcmNesRegression
{
    public static void Run(string output) => PcmNesRegression.Run(output);
}
