namespace TwitchStreamsRecorder.Helpers
{
    internal static class Globals
    {
        public static readonly FfmpegGate FfmpegGate = new();
        public static readonly StreamlinkGate StreamlinkGate = new();
    }
}
