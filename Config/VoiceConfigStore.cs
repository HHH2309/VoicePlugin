using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoicePlugin.Config
{
    public sealed class VoiceConfigStore : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            };

        private readonly string _configPath;
        private readonly string _legacyConfigPath;
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private readonly object _scheduleGate = new object();
        private CancellationTokenSource _debounce;
        private Task _pendingWrite = Task.CompletedTask;
        private VoiceConfigSnapshot _latestSnapshot;
        private bool _disposed;

        public VoiceConfigStore(
            string configPath,
            string legacyConfigPath,
            Action<string> log = null,
            Action<string, Exception> logError = null)
        {
            _configPath = configPath;
            _legacyConfigPath = legacyConfigPath;
            _log = log;
            _logError = logError;
        }

        public VoiceConfigSnapshot Load(out bool migrated)
        {
            migrated = false;
            if (!string.IsNullOrWhiteSpace(_configPath) && File.Exists(_configPath))
            {
                try
                {
                    var json = File.ReadAllText(_configPath);
                    var config = JsonSerializer.Deserialize<VoiceConfig>(json, JsonOptions);
                    if (config == null)
                    {
                        throw new InvalidDataException("The JSON configuration was empty.");
                    }

                    // 旧版本配置（schema 低于当前版本）：缺失字段由快照默认值补齐，
                    // 标记迁移，启动后立即重写一次为当前 schema。
                    if (config.SchemaVersion < VoiceConfigSnapshot.CurrentSchemaVersion)
                    {
                        migrated = true;
                        _log?.Invoke(
                            $"[Voice] JSON configuration schema {config.SchemaVersion} is older than {VoiceConfigSnapshot.CurrentSchemaVersion}; it will be rewritten on save.");
                    }

                    _log?.Invoke("[Voice] JSON configuration loaded.");
                    return new VoiceConfigSnapshot(config);
                }
                catch (Exception ex)
                {
                    _logError?.Invoke(
                        "[Voice] JSON configuration is invalid; attempting legacy INI recovery without overwriting the malformed JSON file.",
                        ex);
                    if (!string.IsNullOrWhiteSpace(_legacyConfigPath)
                        && File.Exists(_legacyConfigPath))
                    {
                        try
                        {
                            return LoadLegacyIni(_legacyConfigPath);
                        }
                        catch (Exception legacyException)
                        {
                            _logError?.Invoke(
                                "[Voice] legacy INI recovery also failed; defaults will be used.",
                                legacyException);
                        }
                    }
                    return new VoiceConfigSnapshot(new VoiceConfig());
                }
            }

            // 主配置文件缺失时，尝试恢复上次原子写遗留的临时文件，
            // 避免宿主阻止文件改名导致用户设置整批丢失。
            var temporaryPath = _configPath + ".tmp";
            if (!string.IsNullOrWhiteSpace(_configPath) && File.Exists(temporaryPath))
            {
                try
                {
                    var json = File.ReadAllText(temporaryPath);
                    var config = JsonSerializer.Deserialize<VoiceConfig>(json, JsonOptions);
                    if (config != null)
                    {
                        migrated = true;
                        _log?.Invoke("[Voice] JSON configuration recovered from a pending write.");
                        return new VoiceConfigSnapshot(config);
                    }
                }
                catch (Exception ex)
                {
                    _logError?.Invoke(
                        "[Voice] the leftover pending configuration file is invalid; defaults will be used.",
                        ex);
                }
            }

            if (!string.IsNullOrWhiteSpace(_legacyConfigPath)
                && File.Exists(_legacyConfigPath))
            {
                try
                {
                    var snapshot = LoadLegacyIni(_legacyConfigPath);
                    migrated = true;
                    _log?.Invoke("[Voice] legacy INI configuration migrated in memory.");
                    return snapshot;
                }
                catch (Exception ex)
                {
                    _logError?.Invoke(
                        "[Voice] failed to migrate the legacy INI configuration; defaults will be used.",
                        ex);
                }
            }

            return new VoiceConfigSnapshot(new VoiceConfig());
        }

        public Task ScheduleSaveAsync(
            VoiceConfigSnapshot snapshot,
            int debounceMilliseconds = 400)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            lock (_scheduleGate)
            {
                if (_disposed)
                {
                    return Task.FromException(
                        new ObjectDisposedException(nameof(VoiceConfigStore)));
                }

                _latestSnapshot = snapshot;
                _debounce?.Cancel();
                _debounce?.Dispose();
                _debounce = new CancellationTokenSource();
                var token = _debounce.Token;
                _pendingWrite = Task.Run(() => SaveAfterDelayAsync(
                    snapshot,
                    Math.Max(0, debounceMilliseconds),
                    token));
                return _pendingWrite;
            }
        }

        public async Task FlushAsync(TimeSpan timeout)
        {
            VoiceConfigSnapshot latest;
            lock (_scheduleGate)
            {
                if (_disposed) return;
                latest = _latestSnapshot;
                _debounce?.Cancel();
            }

            if (latest == null) return;

            using var timeoutCancellation = new CancellationTokenSource(timeout);
            try
            {
                await _writeGate.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
                try
                {
                    WriteAtomic(latest);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
            {
            }
        }

        public void Dispose()
        {
            lock (_scheduleGate)
            {
                if (_disposed) return;
                _disposed = true;
                _debounce?.Cancel();
                _debounce?.Dispose();
                _debounce = null;
            }
        }

        private async Task SaveAfterDelayAsync(
            VoiceConfigSnapshot snapshot,
            int delayMilliseconds,
            CancellationToken cancellationToken)
        {
            try
            {
                if (delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds, cancellationToken)
                        .ConfigureAwait(false);
                }

                await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    WriteAtomic(snapshot);
                    _log?.Invoke("[Voice] JSON configuration saved.");
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to save JSON configuration.", ex);
                throw;
            }
        }

        private void WriteAtomic(VoiceConfigSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(_configPath))
            {
                throw new InvalidOperationException(
                    "The plugin configuration folder was not supplied by the host.");
            }

            var directory = Path.GetDirectoryName(_configPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "The plugin configuration path is invalid.");
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = _configPath + ".tmp";
            var backupPath = _configPath + ".bak";
            var json = JsonSerializer.Serialize(snapshot.ToConfig(), JsonOptions);

            try
            {
                File.WriteAllText(temporaryPath, json);

                if (File.Exists(_configPath))
                {
                    File.Replace(temporaryPath, _configPath, backupPath, true);
                    TryDelete(backupPath);
                }
                else
                {
                    File.Move(temporaryPath, _configPath);
                }
            }
            catch (IOException)
            {
                // 宿主可能持有禁止改名/删除的目录句柄（如进程保护），
                // 原子写会被共享冲突阻断；降级为直接写正式文件，
                // 保证设置仍然能够持久化。
                _log?.Invoke("[Voice] atomic configuration write was blocked; falling back to a direct write.");
                File.WriteAllText(_configPath, json);
                TryDelete(temporaryPath);
            }
        }

        private static VoiceConfigSnapshot LoadLegacyIni(string path)
        {
            var config = new VoiceConfig();
            var hasTemplate = false;
            var legacyVoiceId = string.Empty;

            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1);
                switch (key)
                {
                    case "EnableRandomPickTts":
                        if (bool.TryParse(value, out var enabled)) config.Enabled = enabled;
                        break;
                    case "Rate":
                        if (int.TryParse(value, out var rate)) config.Rate = rate;
                        break;
                    case "Volume":
                        if (int.TryParse(value, out var volume)) config.Volume = volume;
                        break;
                    case "VoiceId":
                        legacyVoiceId = value;
                        break;
                    case "SpeechTemplate":
                        config.SpeechTemplate = value;
                        hasTemplate = true;
                        break;
                    case "DelayMilliseconds":
                        if (int.TryParse(value, out var delay)) config.DelayMilliseconds = delay;
                        break;
                    case "SpaceNumericDigits":
                        if (bool.TryParse(value, out var digits)) config.SpaceNumericDigits = digits;
                        break;
                    case "EnablePrefix":
                        if (bool.TryParse(value, out var prefix)) config.EnablePrefix = prefix;
                        break;
                    case "PrefixText":
                        config.PrefixText = value;
                        break;
                    case "EnableSuffix":
                        if (bool.TryParse(value, out var suffix)) config.EnableSuffix = suffix;
                        break;
                    case "SuffixText":
                        config.SuffixText = value;
                        break;
                }
            }

            config.Provider = "Sapi";
            config.VoiceId = legacyVoiceId?.Trim() ?? string.Empty;
            if (!hasTemplate)
            {
                config.SpeechTemplate = BuildLegacyTemplate(config);
            }

            return new VoiceConfigSnapshot(config);
        }

        private static string BuildLegacyTemplate(VoiceConfig config)
        {
            var prefix = config.EnablePrefix
                ? config.PrefixText?.Trim()
                : string.Empty;
            var suffix = config.EnableSuffix
                ? config.SuffixText?.Trim()
                : string.Empty;

            return string.Join(" ", new[]
            {
                prefix,
                VoiceTextTemplate.Placeholder,
                suffix
            }).Trim();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
