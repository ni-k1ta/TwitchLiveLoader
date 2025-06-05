using System.Text.Json;

namespace TwitchStreamsRecorder
{
    internal class ConfigService
    {
        private readonly JsonSerializerOptions _opt = new() { WriteIndented = true };
        private readonly object _lock = new();

        public Config LoadConfig(string pathToJson)
        {
            if (!File.Exists(pathToJson))
                throw new FileNotFoundException($"Конфиг-файл \"recorder.json\" не найден по пути {pathToJson}");

            var json = File.ReadAllText(pathToJson);

            var cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config)
                      ?? throw new Exception("invalid recorder.json");

            return cfg;
        }

        public void SaveConfig(Config cfg, string pathToJson)
        {
            lock (_lock)
            { 
                var tmp = cfg.OutputDir;
                cfg.OutputDir = null;

                var json = JsonSerializer.Serialize(cfg, _opt);
                File.WriteAllText(pathToJson, json);

                cfg.OutputDir = tmp;  
            }
        }
    }
}
