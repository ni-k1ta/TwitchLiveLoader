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
                throw new FileNotFoundException($"Конфиг-файл не найден: {pathToJson}");

            try
            {
                var json = File.ReadAllText(pathToJson);

                var cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config)
                          ?? throw new InvalidDataException($"Файл конфигурации пуст или имеет неверную структуру.");

                cfg.Validate();

                return cfg;
            }
            catch (JsonException je)
            {
                throw new InvalidDataException($"Не удалось разобрать JSON в файле {pathToJson}: {je.Message}", je);
            }
            catch (IOException io)
            {
                throw new IOException($"Ошибка чтения файла {pathToJson}: {io.Message}", io);
            }
        }

        public void SaveConfig(Config cfg, string pathToJson)
        {
            try
            {
                lock (_lock)
                {
                    var tmp = Path.ChangeExtension(pathToJson, ".tmp");
                    var json = JsonSerializer.Serialize(cfg, _opt);
                    File.WriteAllText(tmp, json);
                    File.Replace(tmp, pathToJson, null);
                }
            }
            catch (IOException io)
            {
                throw new IOException($"Ошибка записи в файл {pathToJson}: {io.Message}", io);
            }
        }

        internal static string GetDefaultConfigPath() => Path.Combine(AppContext.BaseDirectory, "recorder.json");
    }
}
