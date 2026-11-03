using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InputTranslate;

/// <param name="Detail">成功时是译文来源（例如 “DeepSeek · 考研”），失败时是给用户看的原因。</param>
sealed record TranslateResult(bool Ok, string Text, string Detail);

sealed class Translator
{
    static readonly HttpClient Http = CreateClient();
    static readonly TimeSpan FreeTimeout = TimeSpan.FromSeconds(12);
    static readonly TimeSpan ChatTimeout = TimeSpan.FromSeconds(45);

    static readonly Regex ThinkBlock = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly Dictionary<string, TranslateResult> _cache = new(StringComparer.Ordinal);
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

    /// <summary>设置页的「测试连接」：用表单里还没保存的配置走一遍大模型翻译。</summary>
    public static Task<TranslateResult> TestChatAsync(AppConfig config, CancellationToken cancellationToken)
    {
        var probe = config.Clone();
        probe.Engine = "openai";
        probe.EnglishLevel = "plain";
        return new Translator(probe).TranslateAsync("今天天气不错，我们去公园走走吧。", cancellationToken);
    }

    public async Task<IReadOnlyList<StudyWord>> StudyAsync(string english, string chinese, CancellationToken cancellationToken)
    {
        english = english.Trim();
        chinese = chinese.Trim();
        if (english.Length == 0 || !_config.HasChatApi)
            return Array.Empty<StudyWord>();

        var key = english + "\n" + chinese;
        if (_studyCache.TryGetValue(key, out var cached))
            return cached;

        var user = "英文：\n" + TrimForLookup(english) + "\n\n中文原文：\n" + TrimForLookup(chinese);
        var chat = await ChatAsync(StudyPrompt, user, 0.2, cancellationToken);
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
        if (_config.HasChatApi)
        {
            result = await LookupChatAsync(word, sentence, cancellationToken);
            if (!result.Ok)
            {
                var fallback = await LookupDictionaryAsync(word, cancellationToken);
                if (fallback.Ok)
                    result = fallback with { Banner = "中文释义暂时没取到（" + result.Detail + "），先显示英英释义。" };
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
        if (!_config.HasChatApi)
            return new SenseRewrite(false, "", "", "请在设置里填写大模型接口地址和模型");

        var user = "原句：" + TrimForLookup(sentence)
            + "\n单词：" + word.Trim()
            + "\n选定释义：" + sense.Pos + " " + sense.Chinese;
        var chat = await ChatAsync(RewritePrompt, user, 0.4, cancellationToken);
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
            return cached;

        var result = _config.Engine switch
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
            _cache[key] = result;
        }

        return result;
    }

    /// <summary>配置了大模型接口就用它（难度设置只对它生效），失败或没配置时用免费引擎。</summary>
    async Task<TranslateResult> TranslateAutoAsync(string text, CancellationToken cancellationToken)
    {
        var chatDetail = "";
        if (_config.HasChatApi)
        {
            var chat = await TranslateOpenAiAsync(text, cancellationToken);
            if (chat.Ok)
                return chat;
            chatDetail = chat.Detail;
            TraceLog.Note("auto chat fail " + TraceLog.Safe(chat.Detail));
        }

        var first = await TranslateMyMemoryAsync(text, cancellationToken);
        if (first.Ok)
            return chatDetail.Length == 0 ? first : first with { Detail = first.Detail + "（大模型接口失败，已改用免费引擎）" };
        var second = await TranslateGoogleAsync(text, cancellationToken);
        if (second.Ok)
            return chatDetail.Length == 0 ? second : second with { Detail = second.Detail + "（大模型接口失败，已改用免费引擎）" };

        var reasons = new List<string>();
        if (chatDetail.Length > 0)
            reasons.Add(chatDetail);
        reasons.Add(first.Detail);
        reasons.Add(second.Detail);
        return new TranslateResult(false, "", string.Join("；", reasons));
    }

    async Task<TranslateResult> TranslateMyMemoryAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var piece in SplitPieces(text, 450))
            {
                var url = "https://api.mymemory.translated.net/get?langpair=zh-CN|en&q=" + Uri.EscapeDataString(piece);
                var (status, body) = await GetAsync(url, FreeTimeout, cancellationToken);
                if (status < 200 || status >= 300)
                    return new TranslateResult(false, "", "MyMemory 返回 " + status);
                using var doc = JsonDocument.Parse(body);
                var translated = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
                if (translated.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                    return new TranslateResult(false, "", "MyMemory 今天的免费额度用完了");
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(System.Net.WebUtility.HtmlDecode(translated).Trim());
            }

            var english = sb.ToString().Trim();
            if (english.Length == 0)
                return new TranslateResult(false, "", "MyMemory 没有返回译文");
            return new TranslateResult(true, english, "MyMemory");
        }
        catch (TimeoutException)
        {
            return new TranslateResult(false, "", "MyMemory 超时");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TranslateResult(false, "", "MyMemory：" + Describe(ex));
        }
    }

    async Task<TranslateResult> TranslateGoogleAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var piece in SplitPieces(text, 1500))
            {
                var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&q="
                    + Uri.EscapeDataString(piece);
                var (status, body) = await GetAsync(url, FreeTimeout, cancellationToken);
                if (status < 200 || status >= 300)
                    return new TranslateResult(false, "", "Google 返回 " + status);
                var translated = ParseGoogle(body);
                if (translated.Length == 0)
                    return new TranslateResult(false, "", "Google 没有返回译文");
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(translated.Trim());
            }

            return new TranslateResult(true, sb.ToString().Trim(), "Google");
        }
        catch (TimeoutException)
        {
            return new TranslateResult(false, "", "Google 超时（国内网络可能连不上）");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TranslateResult(false, "", "Google：" + Describe(ex));
        }
    }

    async Task<TranslateResult> TranslateOpenAiAsync(string text, CancellationToken cancellationToken)
    {
        if (!_config.HasChatApi)
            return new TranslateResult(false, "", "请在设置里填写大模型接口地址、密钥和模型");

        var level = _config.EnglishLevel;
        var source = ModelLabel() + " · " + _config.LevelLabel;

        // First pass: a faithful, readable translation.
        var plain = await ChatAsync(TranslationPrompt(level), text, 0, cancellationToken);
        if (!plain.Ok)
            return plain;
        var first = CleanModelText(plain.Text);
        if (first.Length == 0)
            return new TranslateResult(false, "", "接口没有返回译文");

        if (level != "kaoyan")
            return new TranslateResult(true, first, source);

        // Second pass: raise the register to 考研英语 level. The Chinese original is
        // included so the rewrite cannot drift away from the source meaning.
        var rewriteInput = "Chinese original:\n" + text
            + "\n\nPlain English translation:\n" + first
            + "\n\nRewrite the English translation at the required level, preserving the meaning of the Chinese original.";
        var polished = await ChatAsync(KaoyanRewritePrompt, rewriteInput, 0.4, cancellationToken);
        var rewritten = polished.Ok ? CleanModelText(polished.Text) : "";
        if (rewritten.Length == 0)
        {
            TraceLog.Note("rewrite fail " + TraceLog.Safe(polished.Detail));
            return new TranslateResult(true, first, ModelLabel() + " · 润色失败，显示直译");
        }

        return new TranslateResult(true, rewritten, source);
    }

    /// <summary>调一次 chat/completions。除了调用方主动取消，所有错误都变成失败结果，不抛异常。</summary>
    async Task<TranslateResult> ChatAsync(string system, string user, double temperature, CancellationToken cancellationToken)
    {
        var baseUrl = ChatBaseUrl();
        var key = _config.OpenAiApiKey.Trim();
        var model = _config.OpenAiModel.Trim();
        var payload = new
        {
            model,
            temperature,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ChatTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
            if (key.Length > 0)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new TranslateResult(false, "", DescribeHttpError((int)response.StatusCode, body));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                return new TranslateResult(false, "", "接口报错：" + ReadMessage(error));
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return new TranslateResult(false, "", "接口返回的格式不是 OpenAI 兼容格式");
            var content = choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String
                    ? contentElement.GetString() ?? ""
                    : "";
            content = ThinkBlock.Replace(content, "").Trim();
            if (content.Length == 0)
                return new TranslateResult(false, "", "接口没有返回内容");
            return new TranslateResult(true, content, model);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new TranslateResult(false, "", "接口超时（" + (int)ChatTimeout.TotalSeconds + " 秒没有响应）");
        }
        catch (JsonException)
        {
            return new TranslateResult(false, "", "接口返回的不是 JSON，请检查接口地址（通常以 /v1 结尾）");
        }
        catch (Exception ex)
        {
            return new TranslateResult(false, "", "接口：" + Describe(ex));
        }
    }

    async Task<WordGloss> LookupChatAsync(string word, string sentence, CancellationToken cancellationToken)
    {
        var chat = await ChatAsync(GlossPrompt, "单词：" + word + "\n句子：" + TrimForLookup(sentence), 0.2, cancellationToken);
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
            var (status, body) = await GetAsync(url, FreeTimeout, cancellationToken);
            if (status == 404)
                return WordStudy.Fail(word, "词典里没有这个词");
            if (status < 200 || status >= 300)
                return WordStudy.Fail(word, "词典返回 " + status);
            return WordStudy.ParseDictionary(word, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return WordStudy.Fail(word, "词典暂时打不开");
        }
    }

    /// <summary>GET 一个地址；超时抛 <see cref="TimeoutException"/>，跟用户取消区分开。</summary>
    static async Task<(int Status, string Body)> GetAsync(string url, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            using var response = await Http.GetAsync(url, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return ((int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    /// <summary>容错：去掉末尾的 /chat/completions，用户经常把完整地址粘进来。</summary>
    string ChatBaseUrl()
    {
        var url = _config.OpenAiBaseUrl.Trim().TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/chat/completions".Length];
        return url;
    }

    string ModelLabel()
    {
        var model = _config.OpenAiModel.Trim();
        var slash = model.LastIndexOf('/');
        return slash >= 0 && slash < model.Length - 1 ? model[(slash + 1)..] : model;
    }

    static string DescribeHttpError(int status, string body)
    {
        var message = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
                message = error.ValueKind == JsonValueKind.Object ? ReadMessage(error) : error.ToString();
            else if (doc.RootElement.TryGetProperty("message", out var plain))
                message = plain.ToString();
        }
        catch
        {
            message = "";
        }

        var hint = status switch
        {
            401 or 403 => "密钥不对或没有权限",
            404 => "地址或模型名不对",
            429 => "请求太频繁或额度用完",
            >= 500 => "服务端出错",
            _ => "",
        };
        var text = "接口返回 " + status;
        if (hint.Length > 0)
            text += "（" + hint + "）";
        if (message.Length > 0)
            text += "：" + (message.Length > 120 ? message[..120] + "…" : message);
        return text;
    }

    static string ReadMessage(JsonElement error)
    {
        if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString() ?? "";
        return error.ToString();
    }

    static string Describe(Exception ex)
    {
        if (ex is HttpRequestException http)
        {
            var inner = http.InnerException?.Message;
            return string.IsNullOrEmpty(inner) ? "网络连接失败" : "网络连接失败（" + inner + "）";
        }

        return ex.Message;
    }

    static string TrimForLookup(string text)
    {
        text = text.Trim();
        return text.Length <= 800 ? text : text[..800];
    }

    /// <summary>去掉推理模型的 &lt;think&gt; 段落、代码块和整句引号。</summary>
    internal static string CleanModelText(string text)
    {
        text = ThinkBlock.Replace(text ?? "", "").Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = text.IndexOf('\n');
            text = newline >= 0 ? text[(newline + 1)..] : "";
            var fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
                text = text[..fence];
            text = text.Trim();
        }

        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '“' && text[^1] == '”')))
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
        // 超时由每个请求自己控制，区分「超时」和「用户取消」。
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("InputTranslate/" + (typeof(Translator).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
        return client;
    }
}
