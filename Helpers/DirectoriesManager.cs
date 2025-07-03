namespace TwitchStreamsRecorder
{
    internal static class DirectoriesManager
    {
        public static string CreateSessionDirectory(string? pathForSessionDirectory, string twitchChannelLogin)
        {
            var safeLogin = Path.GetInvalidFileNameChars().Aggregate(twitchChannelLogin, (s, c) => s.Replace(c, '_'));

            return CreateTimestampedDirectory(pathForSessionDirectory, safeLogin, "yyyy-MM-dd");
        }
        public static string CreateRecordBufferDirectory(string? pathForBufferDirectory) => CreateTimestampedDirectory(pathForBufferDirectory, "buffer", "HH_mm");
        public static string CreateTranscodeResultDirectory(string? pathForResultDirectory) => CreateTimestampedDirectory(pathForResultDirectory, "result", "HH_mm");

        private static string Timestamp(string? format = "yyyy-MM-dd") => DateTime.Now.ToString(format);
        private static string CreateTimestampedDirectory(string? basePath, string directoryPrefix, string timestampFormat)
        {
            basePath ??= AppContext.BaseDirectory;

            var name = $"{directoryPrefix}_{Timestamp(timestampFormat)}";
            var fullPath = Path.Combine(basePath!, name);
            try
            {
                Directory.CreateDirectory(fullPath);
                return fullPath;
            }
            catch (Exception ex)
            {
                throw new IOException($"Не удалось создать директорию {fullPath}: {ex.Message}");
            }
        }
    }
}
