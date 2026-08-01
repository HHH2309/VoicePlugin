using System.Threading;
using System.Threading.Tasks;

namespace VoicePlugin.Services
{
    /// <summary>
    /// 可选缓存能力：实现该接口的 TTS 引擎自动获得播报音频缓存与预缓存支持
    /// （缓存命中时直接播放缓存文件，未命中时经 <see cref="SynthesizeAsync"/>
    /// 合成并写入缓存）。未来的新引擎（如本地 Piper 等）实现此接口即可复用
    /// <see cref="TtsAudioCache"/> 与预缓存管线，无需改动上层。
    /// </summary>
    public interface ITtsCacheableProvider
    {
        /// <summary>缓存音频文件的扩展名（如 ".mp3"、".wav"）。</summary>
        string CacheExtension { get; }

        /// <summary>
        /// 把文本合成到 <paramref name="outputPath"/>（不播放、不删除该文件）。
        /// 取消时抛 <see cref="System.OperationCanceledException"/>，调用方负责清理。
        /// </summary>
        Task SynthesizeAsync(
            string text,
            TtsRequest request,
            string outputPath,
            CancellationToken cancellationToken);
    }
}
