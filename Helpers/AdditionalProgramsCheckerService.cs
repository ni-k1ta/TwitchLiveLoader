using Serilog;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TwitchStreamsRecorder.Helpers
{
    internal class AdditionalProgramsCheckerService
    {
        private readonly ILogger _log;
        private readonly FfmpegGate _ffmpegGate;
        private readonly StreamlinkGate _streamGate;
        private readonly CancellationToken _ct;

        public AdditionalProgramsCheckerService(ILogger log,
                                   FfmpegGate ffmpegGate,
                                   StreamlinkGate streamGate,
                                   CancellationToken ct)
        {
            _log = log.ForContext("Source", "ProgramsChecker") ?? throw new ArgumentNullException(nameof(log));
            _ffmpegGate = ffmpegGate;
            _streamGate = streamGate;
            _ct = ct;
        }

        public async Task EnsureToolsInstalledAsync()
        {
            _log.Information("Проверка наличия установленных сторонних программ (ffmpeg и streamlink)...");
            if (!await ExecExistsAsync("ffmpeg"))
            {
                _log.Warning("ffmpeg не найдена – installing...");
                await using (await _ffmpegGate.AcquireAsync(_ct))
                {
                    try
                    {
                        await InstallFfmpegAsync();
                    }
                    catch (Exception) { throw; }
                }
            }
            else _log.Information("ffmpeg найдена - ок.");

            if (!await ExecExistsAsync("streamlink"))
            {
                _log.Information("streamlink не найдена – installing...");
                await using (await _streamGate.AcquireAsync(_ct))
                {
                    try
                    {
                        await InstallStreamlinkAsync();
                    }
                    catch (Exception) { throw; }
                }
            }
            else _log.Information("streamlink найдена - ок.");
        }

        private static async Task<bool> ExecExistsAsync(string exe)
        {
            var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new ProcessStartInfo("where", exe)
                : new ProcessStartInfo("which", exe);

            psi.RedirectStandardOutput = false;
            psi.RedirectStandardError = false;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            try
            {
                using var p = Process.Start(psi)!;
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch (Exception) { throw; }
        }

        private async Task InstallFfmpegAsync()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    await RunShellAsync("sudo apt-get update && sudo apt-get install -y ffmpeg");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (await ExecExistsAsync("winget"))
                        await RunShellAsync("winget install --id Gyan.FFmpeg -e --accept-package-agreements --accept-source-agreements");
                    else if (await ExecExistsAsync("choco"))
                        await RunShellAsync("choco install ffmpeg -y");
                    else
                    {
                        _log.Error("Cannot install ffmpeg automatically – neither winget nor choco found. Please install manually.");
                        throw new NotSupportedException("Cannot install ffmpeg automatically – neither winget nor choco found. Please install manually.");
                    }
                }
                else
                {
                    _log.Warning("Unsupported OS for automatic ffmpeg install.");
                    throw new NotSupportedException("Unsupported OS for automatic ffmpeg install.");
                }
            }
            catch (Exception) { throw; }
        }

        private async Task InstallStreamlinkAsync()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    await RunShellAsync("sudo apt-get update && sudo apt-get install -y streamlink");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (await ExecExistsAsync("winget"))
                        await RunShellAsync("winget install --id Streamlink.Streamlink -e --accept-package-agreements --accept-source-agreements");
                    else if (await ExecExistsAsync("choco"))
                    {
                        await RunShellAsync("choco install streamlink -y");
                    }
                    else
                    {
                        _log.Error("Cannot install streamlink automatically – neither winget nor choco found. Please install manually.");
                        throw new NotSupportedException("Cannot install ffmpeg automatically – neither winget nor choco found. Please install manually.");
                    }
                }
                else
                {
                    _log.Warning("Unsupported OS for automatic streamlink install.");
                    throw new NotSupportedException("Unsupported OS for automatic ffmpeg install.");
                }
            }
            catch (Exception) { throw; }
        }

        private async Task RunShellAsync(string cmd)
        {
            _log.Information("Выполнение: {Cmd}...", cmd);
            ProcessStartInfo psi;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                psi = new ProcessStartInfo("bash", "-c \"" + cmd.Replace("\"", "\\\"") + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(_ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(_ct);
            await proc.WaitForExitAsync(_ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode == 0)
                _log.Information("Комманда \"{cmd}\" выполнена успешно. ", cmd);
            else
            {
                _log.Error("{Cmd} FAILED (code {Code})\n{Err}", cmd, proc.ExitCode, stderr);
                throw new OperationCanceledException($"{cmd} FAILED (code {proc.ExitCode})\n{stderr}");
            }    
        }
    }
}