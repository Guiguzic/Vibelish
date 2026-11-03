using System.Globalization;
using System.Speech.Synthesis;

namespace Vibelish;

/// <summary>用 Windows 自带的英文语音朗读译文和单词。没装英文语音时给出提示，不报错。</summary>
static class Speech
{
    static SpeechSynthesizer? _synth;
    static bool _checked;
    static string _voice = "";

    /// <summary>有没有可用的英文语音（第一次调用时检查，之后缓存）。</summary>
    public static bool Available
    {
        get
        {
            Ensure();
            return _voice.Length > 0;
        }
    }

    public static string MissingHint => "没有英文语音：在 Windows 设置 → 时间和语言 → 语音 里添加一个英语语音";

    /// <param name="speed">slow、normal、fast。</param>
    /// <returns>没有英文语音时返回 false。</returns>
    public static bool Speak(string text, string speed)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
            return false;
        Ensure();
        if (_synth == null || _voice.Length == 0)
            return false;
        try
        {
            _synth.SpeakAsyncCancelAll();
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

    static void Ensure()
    {
        if (_checked)
            return;
        _checked = true;
        try
        {
            var synth = new SpeechSynthesizer();
            synth.SetOutputToDefaultAudioDevice();
            var voices = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
            var english = voices.FirstOrDefault(v => v.Culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                ?? voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == "en");
            if (english != null)
            {
                synth.SelectVoice(english.Name);
                _voice = english.Name;
            }

            _synth = synth;
            TraceLog.Note("speech voice=" + (_voice.Length == 0 ? "none" : _voice) + " installed=" + voices.Count
                + " ui=" + CultureInfo.CurrentUICulture.Name);
        }
        catch (Exception ex)
        {
            TraceLog.Note("speech init fail " + TraceLog.Safe(ex.Message));
            _synth = null;
            _voice = "";
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
