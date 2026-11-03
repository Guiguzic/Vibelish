using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InputTranslate;

sealed record TranslateResult(bool Ok, string Text, string Detail);

sealed class Translator
{
    static readonly HttpClient Http = CreateClient();
    readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    readonly Dictionary<string, WordGloss> _glossCache = new(StringComparer.Ordinal);
    readonly Dictionary<string, IReadOnlyList<StudyWord>> _studyCache = new(StringComparer.Ordinal);
    AppConfig _config;

    public Translator(AppConfig config)
    {
        _config = config;
    }

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        _cache.Clear();
        _glossCache.Clear();
        _studyCache.Clear();
    }

    public async Task<IReadOnlyList<StudyWord>> StudyAsync(string english, string chinese, CancellationToken cancellationToken)
    {
        english = english.Trim();
        chinese = chinese.Trim();
        if (english.Length == 0 || !HasChat())
            return Array.Empty<StudyWord>();

        var key = english + "\n" + chinese;
        if (_studyCache.TryGetValue(key, out var cached))
            return cached;

        var user = "英文：\n" + TrimForLookup(english) + "\n\n中文原文：\n" + TrimForLookup(chinese);
        var chat = await ChatAsync(
            ChatBaseUrl(),
            _config.OpenAiApiKey.Trim(),
            _config.OpenAiModel.Trim(),
            StudyPrompt,
            user,
            0.2,
            cancellationToken);
        if (!chat.Ok)
            return Array.Empty<StudyWord>();

        var words = WordStudy.ParseStudy(chat.Text);
        if (words.Count > 0)
        {
            if (_studyCache.Count > 50)
                _studyCache.Clear();
            _studyCache[key] = words;
        }

        return words;
    }

    public async Task<WordGloss> LookupWordAsync(string word, string sentence, CancellationToken cancellationToken)
    {
        word = word.Trim();
        sentence = sentence.Trim();
        if (word.Length == 0)
            return WordStudy.Fail(word, "没有点到单词");

        var key = word.ToLowerInvariant() + "\n" + sentence;
        if (_glossCache.TryGetValue(key, out var cached))
            return cached;

        WordGloss result;
        if (HasChat())
        {
            result = await LookupChatAsync(word, sentence, cancellationToken);
            if (!result.Ok)
            {
                var fallback = await LookupDictionaryAsync(word, cancellationToken);
                if (fallback.Ok)
                    result = fallback with { Banner = "中文释义暂时没取到，先显示英英释义。" };
            }
        }
        else
        {
            result = await LookupDictionaryAsync(word, cancellationToken);
        }

        if (result.Ok)
        {
            if (_glossCache.Count > 100)
                _glossCache.Clear();
            _glossCache[key] = result;
        }

        return result;
    }

    public async Task<SenseRewrite> RewriteWithSenseAsync(
        string sentence,
        string word,
        WordSense sense,
        CancellationToken cancellationToken)
    {
        if (!HasChat())
            return new SenseRewrite(false, "", "", "请在设置里填写接口地址、密钥和模型");

        var user = "原句：" + TrimForLookup(sentence)
            + "\n单词：" + word.Trim()
            + "\n选定释义：" + sense.Pos + " " + sense.Chinese;
        var chat = await ChatAsync(
            ChatBaseUrl(),
            _config.OpenAiApiKey.Trim(),
            _config.OpenAiModel.Trim(),
            RewritePrompt,
            user,
            0.4,
            cancellationToken);
        if (!chat.Ok)
            return new SenseRewrite(false, "", "", chat.Detail);
        return WordStudy.ParseRewrite(chat.Text);
    }

    public async Task<TranslateResult> TranslateAsync(string text, CancellationToken cancellationToken)
    {
        text = text.Trim();
        if (text.Length == 0)
            return new TranslateResult(false, "", "没有可翻译的文字");

        var key = _config.Engine + "\n" + _config.EnglishLevel + "\n" + _config.OpenAiBaseUrl + "\n" + _config.OpenAiModel + "\n" + text;
        if (_cache.TryGetValue(key, out var cached))
            return new TranslateResult(true, cached, "缓存");

        var engine = _config.Engine;
        TranslateResult result = engine switch
        {
            "mymemory" => await TranslateMyMemoryAsync(text, cancellationToken),
            "google" => await TranslateGoogleAsync(text, cancellationToken),
            "openai" => await TranslateOpenAiAsync(text, cancellationToken),
            _ => await TranslateAutoAsync(text, cancellationToken),
        };

        if (result.Ok && result.Text.Length > 0)
        {
            if (_cache.Count > 200)
                _cache.Clear();
            _cache[key] = result.Text;
        }

        return result;
    }

    async Task<TranslateResult> TranslateAutoAsync(string text, CancellationToken cancellationToken)
    {
        var first = await TranslateMyMemoryAsync(text, cancellationToken);
        if (first.Ok)
            return first;
        var second = await TranslateGoogleAsync(text, cancellationToken);
        if (second.Ok)
            return second;
        return new TranslateResult(false, "", first.Detail + "；" + second.Detail);
    }

    async Task<TranslateResult> TranslateMyMemoryAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            var pieces = SplitPieces(text, 450);
            var sb = new StringBuilder();
            foreach (var piece in pieces)
            {
                var url = "https://api.mymemory.translated.net/get?langpair=zh-CN|en&q=" + Uri.EscapeDataString(piece);
                using var response = await Http.GetAsync(url, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return new TranslateResult(false, "", "MyMemory 返回 " + (int)response.StatusCode);
                using var doc = JsonDocument.Parse(body);
                var translated = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
                if (translated.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                    return new TranslateResult(false, "", "MyMemory 免费额度已用完");
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(translated.Trim());
            }

            var english = sb.ToString().Trim();
            if (english.Length == 0)
                return new TranslateResult(false, "", "MyMemory 没有返回译文");
            return new TranslateResult(true, english, "MyMemory");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TranslateResult(false, "", "MyMemory：" + ex.Message);
        }
    }

    async Task<TranslateResult> TranslateGoogleAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            var pieces = SplitPieces(text, 1500);
            var sb = new StringBuilder();
            foreach (var piece in pieces)
            {
                var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&q="
                    + Uri.EscapeDataString(piece);
                using var response = await Http.GetAsync(url, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return new TranslateResult(false, "", "Google 返回 " + (int)response.StatusCode);
                var translated = ParseGoogle(body);
                if (translated.Length == 0)
                    return new TranslateResult(false, "", "Google 没有返回译文");
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(translated.Trim());
            }

            return new TranslateResult(true, sb.ToString().Trim(), "Google");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TranslateResult(false, "", "Google：" + ex.Message);
        }
    }

    async Task<TranslateResult> TranslateOpenAiAsync(string text, CancellationToken cancellationToken)
    {
        var baseUrl = _config.OpenAiBaseUrl.Trim().TrimEnd('/');
        var key = _config.OpenAiApiKey.Trim();
        var model = _config.OpenAiModel.Trim();
        if (baseUrl.Length == 0 || key.Length == 0 || model.Length == 0)
            return new TranslateResult(false, "", "请在设置里填写接口地址、密钥和模型");

        var level = (_config.EnglishLevel ?? "").Trim().ToLowerInvariant();
        try
        {
            // First pass: a faithful, readable translation.
            var plain = await ChatAsync(baseUrl, key, model, TranslationPrompt(level), text, 0, cancellationToken);
            if (!plain.Ok)
                return plain;

            if (level != "kaoyan")
                return plain;

            // Second pass: raise the register to 考研英语 level. The Chinese original is
            // included so the rewrite cannot drift away from the source meaning.
            var rewriteInput = "Chinese original:\n" + text
                + "\n\nPlain English translation:\n" + plain.Text
                + "\n\nRewrite the English translation at the required level, preserving the meaning of the Chinese original.";
            var polished = await ChatAsync(baseUrl, key, model, KaoyanRewritePrompt, rewriteInput, 0.4, cancellationToken);
            if (!polished.Ok || polished.Text.Length == 0)
            {
                TraceLog.Note("rewrite fail " + TraceLog.Safe(polished.Detail));
                return plain;
            }

            return new TranslateResult(true, CleanRewrite(polished.Text), model + " · 中文 → 英文");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TranslateResult(false, "", "接口：" + ex.Message);
        }
    }

    async Task<TranslateResult> ChatAsync(
        string baseUrl,
        string key,
        string model,
        string system,
        string user,
        double temperature,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model,
            temperature,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new TranslateResult(false, "", "接口返回 " + (int)response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var english = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()?
            .Trim() ?? "";
        if (english.Length == 0)
            return new TranslateResult(false, "", "接口没有返回译文");
        return new TranslateResult(true, english, model);
    }

    async Task<WordGloss> LookupChatAsync(string word, string sentence, CancellationToken cancellationToken)
    {
        var chat = await ChatAsync(
            ChatBaseUrl(),
            _config.OpenAiApiKey.Trim(),
            _config.OpenAiModel.Trim(),
            GlossPrompt,
            "单词：" + word + "\n句子：" + TrimForLookup(sentence),
            0.2,
            cancellationToken);
        if (!chat.Ok)
            return WordStudy.Fail(word, chat.Detail);
        return WordStudy.ParseGloss(word, chat.Text, canRewrite: true);
    }

    async Task<WordGloss> LookupDictionaryAsync(string word, CancellationToken cancellationToken)
    {
        var gloss = await FetchDictionaryAsync(word, cancellationToken);
        if (!gloss.Ok && word.Contains('\''))
            gloss = await FetchDictionaryAsync(word.Replace("'", ""), cancellationToken);
        return gloss;
    }

    async Task<WordGloss> FetchDictionaryAsync(string word, CancellationToken cancellationToken)
    {
        try
        {
            var url = "https://api.dictionaryapi.dev/api/v2/entries/en/" + Uri.EscapeDataString(word);
            using var response = await Http.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return WordStudy.Fail(word, "词典里没有这个词");
            if (!response.IsSuccessStatusCode)
                return WordStudy.Fail(word, "词典返回 " + (int)response.StatusCode);
            return WordStudy.ParseDictionary(word, body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return WordStudy.Fail(word, "词典暂时打不开");
        }
    }

    bool HasChat()
    {
        return _config.OpenAiBaseUrl.Trim().Length > 0
            && _config.OpenAiApiKey.Trim().Length > 0
            && _config.OpenAiModel.Trim().Length > 0;
    }

    string ChatBaseUrl() => _config.OpenAiBaseUrl.Trim().TrimEnd('/');

    static string TrimForLookup(string text)
    {
        text = text.Trim();
        return text.Length <= 800 ? text : text[..800];
    }

    static string CleanRewrite(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1].Trim();
        return text;
    }

    static string ParseGoogle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return "";
        var segments = root[0];
        if (segments.ValueKind != JsonValueKind.Array)
            return "";
        var sb = new StringBuilder();
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array
                && segment.GetArrayLength() > 0
                && segment[0].ValueKind == JsonValueKind.String)
            {
                sb.Append(segment[0].GetString());
            }
        }

        return sb.ToString();
    }

    static List<string> SplitPieces(string text, int maxChars)
    {
        var pieces = new List<string>();
        if (text.Length <= maxChars)
        {
            pieces.Add(text);
            return pieces;
        }

        var parts = Regex.Split(text, @"(?<=[。！？!?；;\n])");
        var buf = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0)
                continue;
            if (buf.Length > 0 && buf.Length + part.Length > maxChars)
            {
                pieces.Add(buf.ToString());
                buf.Clear();
            }

            if (part.Length > maxChars)
            {
                if (buf.Length > 0)
                {
                    pieces.Add(buf.ToString());
                    buf.Clear();
                }

                for (var i = 0; i < part.Length; i += maxChars)
                    pieces.Add(part.Substring(i, Math.Min(maxChars, part.Length - i)));
                continue;
            }

            buf.Append(part);
        }

        if (buf.Length > 0)
            pieces.Add(buf.ToString());
        return pieces;
    }

    const string FidelityRules =
        "Preserve the exact meaning and every detail: do not add, omit, explain, or simplify. "
        + "Keep names, numbers, and line breaks. "
        + "Return only the English text, with no notes, labels, or quotation marks.";

    static string TranslationPrompt(string level) => level switch
    {
        "plain" =>
            "Translate the Chinese text into natural, everyday English with simple, idiomatic wording. " + FidelityRules,
        "cet6" =>
            "Translate the Chinese text into clear formal written English at upper-intermediate level (CET-6), "
            + "with some advanced vocabulary and complex sentences, but still natural. " + FidelityRules,
        _ =>
            "Translate the Chinese text into accurate, clear, natural English. "
            + "This is a first draft; the style will be raised in a second pass, so focus on fidelity and readability. "
            + FidelityRules,
    };

    const string StudyPrompt =
        "你在帮中国学生学一句刚译好的英文。挑 3 到 6 个值得记的词或短语，优先选这句里词义不那么直白、或值得积累的用法。"
        + "跳过专有名词、产品名、网址，也跳过 the、a、is、are、to、of 这种功能词。"
        + "只返回 JSON，不要 Markdown：{\"words\":[{\"en\":\"句中的原词\",\"zh\":\"它在这句里的中文意思\",\"why\":\"不超过16个字\"}]}。"
        + "zh 必须是中文。";

    const string GlossPrompt =
        "你是英语学习助手，面向正在看中文译文的中国学生。用户点了一个英文单词。"
        + "只返回一个 JSON 对象，不要 Markdown，不要解释。格式："
        + "{\"lemma\":\"词典原形\",\"phonetic\":\"/音标/或空字符串\",\"senses\":[{\"pos\":\"v.\",\"zh\":\"简短中文释义\",\"fit\":true,\"note\":\"这个词在这句里的意思，一两句中文\"}],\"tip\":\"20字以内的记忆点\"}。"
        + "senses 给 2 到 4 个常用义项，并且恰好一个 fit 为 true。"
        + "zh 和 note 必须是中文。音标不确定就用空字符串，不要编造。";

    const string RewritePrompt =
        "你是英语学习助手。学习者看中一句英文，并为其中一个词选定了一个中文释义。"
        + "请按这个释义写一句新的英文，尽量保留原句的其余信息；如果这个释义放不进原句，就写一句自然的短句来体现该释义，并尽量靠近原句场景。"
        + "只返回 JSON：{\"english\":\"新英文\",\"chinese\":\"这句中文\"}。不要 Markdown。";

    const string KaoyanRewritePrompt =
        "You are a bilingual editor producing English at C1-C2 level for Chinese students preparing for 考研英语. "
        + "Rewrite the user's English sentence so that it reads like a sentence from The Economist or a 考研英语 reading passage.\n"
        + "Rules:\n"
        + "- Replace common words with precise, less frequent equivalents (for example \"made me realize\" -> \"led me to appreciate\"; "
        + "\"difficult\" -> \"formidable\"; \"master a skill\" -> \"attain proficiency in a skill\").\n"
        + "- Include at least one complex construction: nominalization, a participle clause, a relative clause, or inversion.\n"
        + "- Formal written register, no contractions, no spoken fillers.\n"
        + "- Preserve the meaning of the Chinese original exactly; add no information and leave nothing out.\n"
        + "- Keep the same sentence boundaries as the plain translation.\n"
        + "- Output only the rewritten English text, with no notes, labels, or quotation marks.";

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("InputTranslate/1.0");
        return client;
    }
}
