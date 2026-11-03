using System.Globalization;
using System.Speech.Synthesis;

namespace Vibelish;

/// <summary>用 Windows 自带的语音朗读在学的语言。没装这种语言的语音时给出提示，不报错。</summary>
static class Speech
{
    static SpeechSynthesizer? _synth;
    static bool _checked;
    static List<VoiceInfo> _voices = new();
    static string _selected = "";

    /// <summary>有没有这种语言的语音（第一次调用时列出装了哪些语音，之后缓存）。</summary>
    public static bool HasVoice(string culture) => Find(culture) != null;

    public static string MissingHint(string languageName) =>
        "没有" + languageName + "语音：在 Windows 设置 → 时间和语言 → 语音 里添加一个" + languageName + "语音";

    /// <param name="speed">slow、normal、fast。</param>
    /// <param name="culture">要读的语言，例如 en-US、ja-JP。</param>
    /// <returns>没有这种语言的语音时返回 false。</returns>
    public static bool Speak(string text, string speed, string culture)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
            return false;
        var voice = Find(culture);
        if (_synth == null || voice == null)
            return false;
        try
        {
            _synth.SpeakAsyncCancelAll();
            if (_selected != voice.Name)
            {
                _synth.SelectVoice(voice.Name);
                _selected = voice.Name;
            }

            _synth.Rate = speed switch
            {
                "slow" => -3,
                "fast" => 2,
                _ => 0,
            };
            _synth.SpeakAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            TraceLog.Note("speak fail " + TraceLog.Safe(ex.Message));
            return false;
        }
    }

    public static void Stop()
    {
        try
        {
            _synth?.SpeakAsyncCancelAll();
        }
        catch
        {
            // Ignore.
        }
    }

    /// <summary>先找区域完全一样的（en-US），再找同一种语言的（en-GB）。中文要分简繁，不混用。</summary>
    static VoiceInfo? Find(string culture)
    {
        Ensure();
        var exact = _voices.FirstOrDefault(v => v.Culture.Name.Equals(culture, StringComparison.OrdinalIgnoreCase));
        if (exact != null || culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return exact;
        var language = culture.Split('-')[0];
        return _voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName.Equals(language, StringComparison.OrdinalIgnoreCase));
    }

    static void Ensure()
    {
        if (_checked)
            return;
        _checked = true;
        try
        {
            var synth = new SpeechSynthesizer();
            synth.SetOutputToDefaultAudioDevice();
            _voices = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
            _synth = synth;
            TraceLog.Note("speech voices=" + string.Join(",", _voices.Select(v => v.Culture.Name).Distinct())
                + " ui=" + CultureInfo.CurrentUICulture.Name);
        }
        catch (Exception ex)
        {
            TraceLog.Note("speech init fail " + TraceLog.Safe(ex.Message));
            _synth = null;
            _voices = new();
        }
    }

    public static void Dispose()
    {
        try
        {
            _synth?.Dispose();
        }
        catch
        {
            // Ignore.
        }

        _synth = null;
    }
}
