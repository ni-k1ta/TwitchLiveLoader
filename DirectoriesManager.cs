namespace TwitchStreamsRecorder
{
    internal static class DirectoriesManager
    {
        public static string CreateSessionDirectory(string? pathForSessionDirectory, string twitchChannelLogin)
        {
            pathForSessionDirectory ??= AppContext.BaseDirectory;

            var sessionDir = Path.Combine(pathForSessionDirectory, twitchChannelLogin + "_" + DateTime.Now.ToString("yyyy-MM-dd"));

            Directory.CreateDirectory(sessionDir);

            return sessionDir;
        }
        public static string CreateRecordBufferDirectory(string pathForBufferDirectory)
        {
            var bufferDir = Path.Combine(pathForBufferDirectory, "buffer_" + DateTime.Now.ToString("HH-mm"));

            Directory.CreateDirectory(bufferDir);

            return bufferDir;
        }
        public static string CreateTranscodeResultDirectory(string pathForResultDirectory)
        {
            var resultDir = Path.Combine(pathForResultDirectory, "result_" + DateTime.Now.ToString("HH-mm"));

            Directory.CreateDirectory(resultDir);

            return resultDir;
        }
    }
}
