using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VoicePlugin.Config;

namespace VoicePlugin.Services
{
    /// <summary>
    /// 播报音频缓存（引擎无关）。
    /// <para>
    /// 缓存键包含全部影响合成输出的参数（引擎、发音人、语速、音量、最终文本），
    /// 参数变化天然产生不同条目，不会播错内容。文件名为键哈希 + 引擎扩展名，
    /// 单目录存储、无索引文件（崩溃安全）。容量按 LRU（LastAccessTime）淘汰，
    /// 写缓存用“临时文件 + 原子改名”，损坏/不可写时全部静默降级为无缓存。
    /// 未来新增 TTS 引擎只需实现 <see cref="ITtsCacheableProvider"/> 即可复用本服务。
    /// </para>
    /// </summary>
    public sealed class TtsAudioCache
    {
        /// <summary>缓存写入中的临时文件后缀（供调用方识别“提交失败需播放后清理”的路径）。</summary>
        public const string TempSuffix = ".tmp";
        private const double EvictToRatio = 0.8;

        private readonly string _cacheDirectory;
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;
        private readonly object _gate = new object();
        private AudioCacheLimitMode _limitMode = AudioCacheLimitMode.Size;
        private long _maxBytes;
        private int _retentionDays = 7;
        private long _totalBytes = -1; // -1 = 尚未完成启动扫描
        private long _entryCount;
        private long _hits;
        private long _misses;
        private long _bytesServed;
        private volatile bool _disabled;

        public TtsAudioCache(
            string cacheDirectory,
            long maxBytes,
            Action<string> log = null,
            Action<string, Exception> logError = null)
        {
            _cacheDirectory = cacheDirectory;
            _maxBytes = Math.Max(1, maxBytes);
            _log = log;
            _logError = logError;

            try
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
            catch (Exception ex)
            {
                _disabled = true;
                _logError?.Invoke("[Voice] the audio cache directory is not writable; caching is disabled.", ex);
            }

            // 启动后台扫描：统计总量，不阻塞插件初始化（低配机友好）。
            if (!_disabled)
            {
                _ = Task.Run(ScanInBackground);
            }
        }

        /// <summary>缓存统计快照（供缓存分析展示）。</summary>
        public readonly record struct CacheStats(
            long Hits,
            long Misses,
            long EntryCount,
            long TotalBytes,
            long BytesServed)
        {
            /// <summary>命中率（0-1）；尚无请求时为 0。</summary>
            public double HitRate => Hits + Misses > 0
                ? (double)Hits / (Hits + Misses)
                : 0;
        }

        /// <summary>缓存键：引擎 + 发音人 + 语速 + 音量 + 最终文本（区分发音人/语速的保证）。</summary>
        public readonly record struct CacheKey(
            TtsProviderKind Provider,
            string VoiceId,
            int Rate,
            int Volume,
            string Text)
        {
            public string CanonicalString =>
                Provider + "|" + (VoiceId ?? string.Empty) + "|" + Rate + "|" + Volume + "|" + (Text ?? string.Empty);
        }

        public bool IsEnabled => !_disabled;

        /// <summary>当前统计快照（命中/未命中为会话内计数）。</summary>
        public CacheStats GetStats()
        {
            lock (_gate)
            {
                return new CacheStats(
                    Volatile.Read(ref _hits),
                    Volatile.Read(ref _misses),
                    Math.Max(0, _entryCount),
                    Math.Max(0, _totalBytes),
                    Math.Max(0, _bytesServed));
            }
        }

        /// <summary>运行时启停缓存（设置热生效；关闭后播报回落为实时合成）。</summary>
        public void SetEnabled(bool enabled)
        {
            lock (_gate)
            {
                _disabled = !enabled;
            }
        }

        /// <summary>设置淘汰策略（按容量 / 按日期 / 不限制，设置热生效）。</summary>
        public void SetPolicy(
            AudioCacheLimitMode limitMode,
            long maxBytes,
            int retentionDays)
        {
            lock (_gate)
            {
                _limitMode = limitMode;
                _maxBytes = Math.Max(1, maxBytes);
                _retentionDays = Math.Clamp(retentionDays, 1, 30);
            }

            // 策略变更触发的淘汰含目录枚举与文件删除，可能被 UI 线程
            // （设置热生效路径）调用：移到后台执行，避免阻塞 UI。
            _ = Task.Run(() =>
            {
                try
                {
                    lock (_gate)
                    {
                        TryEvictLocked();
                    }
                }
                catch (Exception ex)
                {
                    _logError?.Invoke("[Voice] audio cache eviction failed.", ex);
                }
            });
        }

        /// <summary>清空全部缓存（含未提交的临时文件）。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(
                        _cacheDirectory,
                        "*",
                        SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception)
                        {
                            // 正在被占用/播放：跳过。
                        }
                    }
                    _totalBytes = 0;
                    _entryCount = 0;
                }
                catch (Exception ex)
                {
                    _logError?.Invoke("[Voice] failed to clear the audio cache.", ex);
                }
            }
            _log?.Invoke("[Voice] audio cache cleared.");
        }

        /// <summary>
        /// 命中返回缓存文件路径；未命中时调用 <paramref name="synthesizeToFile"/>
        /// 合成到缓存临时文件并原子提交。取消时删除临时文件并抛出。
        /// <paramref name="countMiss"/> 为 false 时（如预缓存预检）不计入未命中统计。
        /// </summary>
        public async Task<string> GetOrCreateAsync(
            CacheKey key,
            string extension,
            Func<string, CancellationToken, Task> synthesizeToFile,
            CancellationToken cancellationToken,
            bool countMiss = true)
        {
            if (_disabled || string.IsNullOrWhiteSpace(key.Text))
            {
                throw new InvalidOperationException("The audio cache is disabled or the text is empty.");
            }

            var finalPath = GetFilePath(key, extension);
            if (TryGet(key, extension, out var hitPath, countAsHit: true))
            {
                return hitPath;
            }

            if (countMiss)
            {
                Interlocked.Increment(ref _misses);
            }

            // 临时文件带唯一后缀：预缓存与实时播报并发合成同一文本时，
            // 各自持有独立的临时文件，互不阻塞提交（否则共享 .tmp 会导致
            // “文件被占用”反复失败并播放半成品音频）。
            var tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + TempSuffix;
            try
            {
                await synthesizeToFile(tempPath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                lock (_gate)
                {
                    // 提交：复制而非改名。合成器（如 EdgeTTS）在 SaveAsync 返回后
                    // 可能仍持有临时文件的句柄（或杀软正在扫描），File.Move 需要
                    // 删除源文件权限而反复失败；File.Copy 只读源文件即可成功。
                    // 后到者覆盖（并发安全）；目标被占用时降级为播放临时文件。
                    try
                    {
                        File.Copy(tempPath, finalPath, overwrite: true);
                        TryDelete(tempPath);
                        _totalBytes = _totalBytes < 0 ? 0 : _totalBytes;
                        _entryCount++;
                        try
                        {
                            _totalBytes += new FileInfo(finalPath).Length;
                        }
                        catch
                        {
                        }
                        TryEvictLocked();
                    }
                    catch (Exception ex)
                    {
                        // 提交失败（目标被占用/磁盘满/权限）：降级为播放临时文件。
                        _logError?.Invoke("[Voice] audio cache commit failed; the temporary audio will be played instead.", ex);
                        return tempPath;
                    }
                }

                return finalPath;
            }
            catch (Exception)
            {
                // 合成失败（无论是否取消）都会留下未提交的临时文件：
                // 立即删除，避免孤儿 .tmp 堆积到下次启动的扫描才清理。
                TryDelete(tempPath);
                throw;
            }
        }

        /// <summary>命中检查并刷新最近访问时间；<paramref name="countAsHit"/> 为 false 时不进入命中统计。</summary>
        public bool TryGet(
            CacheKey key,
            string extension,
            out string path,
            bool countAsHit = true)
        {
            path = null;
            if (_disabled) return false;

            var filePath = GetFilePath(key, extension);
            try
            {
                if (!File.Exists(filePath)) return false;

                // 命中刷新 LastAccessTime（供 LRU 淘汰）。
                try
                {
                    File.SetLastAccessTimeUtc(filePath, DateTime.UtcNow);
                }
                catch
                {
                }

                if (countAsHit)
                {
                    Interlocked.Increment(ref _hits);
                    try
                    {
                        Interlocked.Add(ref _bytesServed, new FileInfo(filePath).Length);
                    }
                    catch
                    {
                    }
                }
                path = filePath;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string GetHash(string canonical)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            var builder = new StringBuilder(40);
            for (var index = 0; index < 20; index++)
            {
                builder.Append(bytes[index].ToString("x2"));
            }
            return builder.ToString();
        }

        private string GetFilePath(CacheKey key, string extension)
        {
            return Path.Combine(
                _cacheDirectory,
                GetHash(key.CanonicalString) + extension);
        }

        private void ScanInBackground()
        {
            try
            {
                long total = 0;
                long count = 0;
                var orphanCutoff = DateTime.UtcNow.AddMinutes(-10);
                foreach (var file in Directory.EnumerateFiles(
                    _cacheDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    // 清理提交失败/取消后残留的孤儿临时文件（超过 10 分钟）。
                    if (file.EndsWith(TempSuffix, StringComparison.Ordinal))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            if (info.LastWriteTimeUtc < orphanCutoff)
                            {
                                info.Delete();
                            }
                        }
                        catch
                        {
                        }
                        continue;
                    }
                    count++;
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch
                    {
                    }
                }

                lock (_gate)
                {
                    if (_disabled) return;
                    _totalBytes = total;
                    _entryCount = count;
                    TryEvictLocked();
                }

                _log?.Invoke(
                    $"[Voice] audio cache scan: {_totalBytes / 1024} KB in the cache directory.");
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] audio cache scan failed; eviction will rely on commit-time checks.", ex);
            }
        }

        /// <summary>按当前策略淘汰（按容量 LRU / 按日期超期删除 / 不限制）。调用方需持有 _gate。</summary>
        private void TryEvictLocked()
        {
            if (_totalBytes < 0) return; // 尚未完成扫描，等扫描兜底

            try
            {
                var files = Directory.EnumerateFiles(
                        _cacheDirectory,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .Where(path => !path.EndsWith(TempSuffix, StringComparison.Ordinal))
                    .Select(path => new FileInfo(path))
                    .ToList();

                if (_limitMode == AudioCacheLimitMode.Date)
                {
                    // 按日期：删除超过保留天数的条目。
                    var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
                    foreach (var file in files)
                    {
                        if (file.LastAccessTimeUtc >= cutoff) continue;

                        try
                        {
                            var length = file.Length;
                            File.Delete(file.FullName);
                            _totalBytes -= length;
                            _entryCount--;
                        }
                        catch (Exception)
                        {
                            // 正在播放/被占用：跳过。
                        }
                    }
                    return;
                }

                if (_limitMode == AudioCacheLimitMode.Unlimited
                    || _totalBytes <= _maxBytes)
                {
                    return;
                }

                // 按容量：LRU（最后访问时间升序）淘汰至上限的 80%。
                foreach (var file in files.OrderBy(info => info.LastAccessTimeUtc))
                {
                    if (_totalBytes <= _maxBytes * EvictToRatio) break;

                    try
                    {
                        var length = file.Length;
                        File.Delete(file.FullName);
                        _totalBytes -= length;
                        _entryCount--;
                    }
                    catch (Exception)
                    {
                        // 正在播放/被占用：跳过。
                    }
                }
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] audio cache eviction failed.", ex);
            }
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
