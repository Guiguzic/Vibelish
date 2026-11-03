using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InputTranslate;

/// <param name="Detail">成功时是译文来源（例如 “deepseek-chat · 考研”），失败时是给用户看的原因。</param>
/// <param name="Words">大模型顺带挑出的重点词；没有就是 null。</param>
/// <param name="FirstMs">流式输出时第一个词出来用了多久（毫秒），0 表示没有流式。</param>
sealed record TranslateResult(
    bool Ok,
    string Text,
    string Detail,
    IReadOnlyList<StudyWord>? Words = null,
    long FirstMs = 0,
    long TotalMs = 0);

sealed class Translator
{
    static readonly HttpClient Http = CreateClient();
    static readonly TimeSpan FreeTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan ChatTimeout = TimeSpan.FromSeconds(60);

    /// <summary>腾讯交互翻译慢于这个时间，就同时问 MyMemory 和 Google，谁先回来用谁。</summary>
    static readonly TimeSpan HedgeAfter = TimeSpan.FromMilliseconds(1200);

    /// <summary>大模型在译文后面用这一行隔开，再给重点词的 JSON，省掉一次请求。</summary>
    const string WordsMarker = "<<<WORDS>>>";

    static readonly Regex ThinkBlock = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex WordsSplit = new(@"<<<\s*WORDS\s*>>>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 有的服务不认「关闭思考」的参数，会返回 400；记下来，以后不再带。
    static readonly HashSet<string> NoExtraHosts = new(StringComparer.OrdinalIgnoreCase);
    static readonly string TransmartClient =
        "browser-chrome-120.0.0-Windows_10-" + Guid.NewGuid().ToString() + "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static long _lastWarmUp;

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
        _lastWarmUp = 0;
    }

    /// <summary>设置页的「测试连接」：用表单里还没保存的配置走一遍大模型翻译，顺便量一下速度。</summary>
    public static Task<TranslateResult> TestChatAsync(AppConfig config, CancellationToken cancellationToken)
    {
        var probe = config.Clone();
        probe.Engine = "openai";
        probe.EnglishLevel = "plain";
        probe.StudyWords = false;
        return new Translator(probe).TranslateAsync("今天天气不错，我们去公园走走吧。", cancellationToken, _ => { });
    }

    /// <summary>开始听写时先把到翻译服务的连接建好（TLS 握手要几百毫秒），第一句就不用等。</summary>
    public void WarmUp()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastWarmUp) < 45000)
            return;
        Interlocked.Exchange(ref _lastWarmUp, now);
        var config = _config;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var request = config.UsesChatForTranslation
                    ? new HttpRequestMessage(HttpMethod.Get, ChatBaseUrl(config) + "/models")
                    : new HttpRequestMessage(HttpMethod.Head, "https://transmart.qq.com/api/imt");
                var key = config.OpenAiApiKey.Trim();
                if (config.UsesChatForTranslation && key.Length > 0)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }
            catch
            {
                // 预热失败没关系，正式请求会自己重连。
            }
        });
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
        RememberStudy(key, words);
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

    /// <param name="partial">流式输出时，每来一段就回调一次当前能显示的译文（在线程池线程上调用）。</param>
    public async Task<TranslateResult> TranslateAsync(string text, CancellationToken cancellationToken, Action<string>? partial = null)
    {
        text = text.Trim();
        if (text.Length == 0)
            return new TranslateResult(false, "", "没有可翻译的文字");

        var key = _config.Engine + "\n" + _config.EnglishLevel + "\n" + _config.OpenAiBaseUrl + "\n" + _config.OpenAiModel
            + "\n" + _config.StudyWords + "\n" + text;
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var result = _config.Engine switch
        {
            "transmart" => await TranslateTransmartAsync(text, cancellationToken),
            "mymemory" => await TranslateMyMemoryAsync(text, cancellationToken),
            "google" => await TranslateGoogleAsync(text, cancellationToken),
            "openai" => await TranslateOpenAiAsync(text, cancellationToken, partial),
            _ => await TranslateAutoAsync(text, cancellationToken, partial),
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
    async Task<TranslateResult> TranslateAutoAsync(string text, CancellationToken cancellationToken, Action<string>? partial)
    {
        var chatDetail = "";
        if (_config.HasChatApi)
        {
            var chat = await TranslateOpenAiAsync(text, cancellationToken, partial);
            if (chat.Ok)
                return chat;
            chatDetail = chat.Detail;
            TraceLog.Note("auto chat fail " + TraceLog.Safe(chat.Detail));
        }

        var free = await TranslateFreeAsync(text, cancellationToken);
        if (free.Ok)
            return chatDetail.Length == 0 ? free : free with { Detail = free.Detail + "（大模型接口失败，已改用免费引擎）" };
        return chatDetail.Length == 0 ? free : free with { Detail = chatDetail + "；" + free.Detail };
    }

    /// <summary>先问腾讯交互翻译（国内最快）；1.2 秒还没回来或失败，就同时问 MyMemory 和 Google，谁先成功用谁。</summary>
    async Task<TranslateResult> TranslateFreeAsync(string text, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<TranslateResult>> { TranslateTransmartAsync(text, race.Token) };
        var head = await Task.WhenAny(pending[0], Task.Delay(HedgeAfter, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (head == pending[0] && pending[0].Result.Ok)
            return pending[0].Result with { TotalMs = watch.ElapsedMilliseconds };

        pending.Add(TranslateMyMemoryAsync(text, race.Token));
        pending.Add(TranslateGoogleAsync(text, race.Token));
        var failures = new List<string>();
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                var result = await done;
                if (result.Ok)
                    return result with { TotalMs = watch.ElapsedMilliseconds };
                failures.Add(result.Detail);
            }
        }
        finally
        {
            race.Cancel();
        }

        return new TranslateResult(false, "", string.Join("；", failures));
    }

    async Task<TranslateResult> TranslateTransmartAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            var payload = new
            {
                header = new { fn = "auto_translation", client_key = TransmartClient },
                type = "plain",
                model_category = "normal",
                source = new { lang = "zh", text_list = new[] { text } },
                target = new { lang = "en" },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://transmart.qq.com/api/imt")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            var (status, body) = await SendAsync(request, FreeTimeout, cancellationToken);
            if (status < 200 || status >= 300)
                return new TranslateResult(false, "", "腾讯交互翻译返回 " + status);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("header", out var header)
                && header.TryGetProperty("ret_code", out var code)
                && code.ValueKind == JsonValueKind.String
                && !string.Equals(code.GetString(), "succ", StringComparison.OrdinalIgnoreCase))
                return new TranslateResult(false, "", "腾讯交互翻译：" + code.GetString());
            if (!root.TryGetProperty("auto_translation", out var list) || list.ValueKind != JsonValueKind.Array)
                return new TranslateResult(false, "", "腾讯交互翻译没有返回译文");
            var parts = list.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => (item.GetString() ?? "").Trim());
            var english = string.Join("\n", parts).Trim();
            if (english.Length == 0)
                return new TranslateResult(false, "", "腾讯交互翻译没有返回译文");
            return new TranslateResult(true, english, "腾讯交互翻译");
        }
        catch (TimeoutException)
        {
            return new TranslateResult(false, "", "腾讯交互翻译超时");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TranslateResult(false, "", "腾讯交互翻译：" + Describe(ex));
        }
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
                sb.Append(WebUtility.HtmlDecode(translated).Trim());
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

    /// <summary>一次请求就出考研难度的译文；开了「重点词」时顺带在后面给出重点词，不再单独请求。</summary>
    async Task<TranslateResult> TranslateOpenAiAsync(string text, CancellationToken cancellationToken, Action<string>? partial)
    {
        if (!_config.HasChatApi)
            return new TranslateResult(false, "", "请在设置里填写大模型接口地址、密钥和模型");

        var level = _config.EnglishLevel;
        var withWords = _config.StudyWords;
        var watch = Stopwatch.StartNew();
        long first = 0;
        Action<string>? onDelta = null;
        if (partial != null && _config.StreamOutput)
        {
            onDelta = raw =>
            {
                var visible = VisiblePart(raw);
                if (visible.Length == 0)
                    return;
                if (first == 0)
                    first = Math.Max(1, watch.ElapsedMilliseconds);
                partial(visible);
            };
        }

        var chat = await ChatAsync(TranslationPrompt(level, withWords), text, level == "kaoyan" ? 0.3 : 0.2, cancellationToken, onDelta);
        if (!chat.Ok)
            return chat;

        var (english, words) = SplitWords(chat.Text);
        if (english.Length == 0)
            return new TranslateResult(false, "", "接口没有返回译文");
        if (withWords && words.Count > 0)
            RememberStudy(english + "\n" + text, words);
        return new TranslateResult(true, english, ModelLabel() + " · " + _config.LevelLabel,
            withWords ? words : null, first, watch.ElapsedMilliseconds);
    }

    /// <summary>调一次 chat/completions。除了调用方主动取消，所有错误都变成失败结果，不抛异常。
    /// 给了 onDelta 就用流式输出，每收到一段回调一次累计的原始文字。</summary>
    async Task<TranslateResult> ChatAsync(
        string system,
        string user,
        double temperature,
        CancellationToken cancellationToken,
        Action<string>? onDelta = null,
        bool allowExtras = true)
    {
        var baseUrl = ChatBaseUrl(_config);
        var key = _config.OpenAiApiKey.Trim();
        var model = _config.OpenAiModel.Trim();
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : "";
        var stream = onDelta != null;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = temperature,
            ["stream"] = stream,
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };

        var extras = allowExtras && _config.FastThinking ? ThinkingOff(host, model) : null;
        lock (NoExtraHosts)
        {
            if (NoExtraHosts.Contains(host))
                extras = null;
        }

        if (extras != null)
        {
            foreach (var pair in extras)
                payload[pair.Key] = pair.Value;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ChatTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
            if (key.Length > 0)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (stream)
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(timeout.Token);
                if ((int)response.StatusCode is 400 or 422 && extras != null)
                {
                    // 多半是不认关闭思考的参数：去掉再试一次，以后也不再带。
                    lock (NoExtraHosts)
                        NoExtraHosts.Add(host);
                    TraceLog.Note("chat extras rejected host=" + host);
                    return await ChatAsync(system, user, temperature, cancellationToken, onDelta, allowExtras: false);
                }

                return new TranslateResult(false, "", DescribeHttpError((int)response.StatusCode, error));
            }

            string content;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (stream && mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var (ok, text, detail) = await ReadEventStreamAsync(response, onDelta!, timeout.Token);
                if (!ok)
                    return new TranslateResult(false, "", detail);
                content = text;
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (stream && body.TrimStart().StartsWith("data:", StringComparison.Ordinal))
                {
                    var (ok, text, detail) = ParseEventStream(body);
                    if (!ok)
                        return new TranslateResult(false, "", detail);
                    content = text;
                }
                else
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                        return new TranslateResult(false, "", "接口报错：" + ReadMessage(error));
                    if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                        return new TranslateResult(false, "", "接口返回的格式不是 OpenAI 兼容格式");
                    content = choices[0].TryGetProperty("message", out var message)
                        && message.TryGetProperty("content", out var contentElement)
                        && contentElement.ValueKind == JsonValueKind.String
                            ? contentElement.GetString() ?? ""
                            : "";
                }

                onDelta?.Invoke(content);
            }

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

    /// <summary>读 SSE：每行 data: {...}，取 choices[0].delta.content 拼起来。</summary>
    static async Task<(bool Ok, string Text, string Detail)> ReadEventStreamAsync(
        HttpResponseMessage response,
        Action<string> onDelta,
        CancellationToken cancellationToken)
    {
        using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(body, Encoding.UTF8);
        var text = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var (done, piece, error) = ParseEventLine(line);
            if (error.Length > 0)
                return (false, "", error);
            if (done)
                break;
            if (piece.Length == 0)
                continue;
            text.Append(piece);
            onDelta(text.ToString());
        }

        return (true, text.ToString(), "");
    }

    static (bool Ok, string Text, string Detail) ParseEventStream(string body)
    {
        var text = new StringBuilder();
        foreach (var line in body.Split('\n'))
        {
            var (done, piece, error) = ParseEventLine(line);
            if (error.Length > 0)
                return (false, "", error);
            if (done)
                break;
            text.Append(piece);
        }

        return (true, text.ToString(), "");
    }

    static (bool Done, string Piece, string Error) ParseEventLine(string line)
    {
        line = line.Trim();
        if (!line.StartsWith("data:", StringComparison.Ordinal))
            return (false, "", "");
        var data = line[5..].Trim();
        if (data == "[DONE]")
            return (true, "", "");
        if (data.Length == 0)
            return (false, "", "");
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                return (false, "", "接口报错：" + ReadMessage(error));
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return (false, "", "");
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
                return (false, content.GetString() ?? "", "");
            return (false, "", "");
        }
        catch (JsonException)
        {
            return (false, "", "");
        }
    }

    /// <summary>流式输出时能给用户看的部分：去掉思考过程、重点词 JSON，以及还没收全的分隔行。</summary>
    internal static string VisiblePart(string raw)
    {
        var text = ThinkBlock.Replace(raw ?? "", "");
        var open = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (open >= 0)
            text = text[..open];
        var marker = WordsSplit.Match(text);
        if (marker.Success)
        {
            text = text[..marker.Index];
        }
        else
        {
            for (var k = Math.Min(WordsMarker.Length - 1, text.Length); k > 0; k--)
            {
                if (text.EndsWith(WordsMarker[..k], StringComparison.Ordinal))
                {
                    text = text[..^k];
                    break;
                }
            }
        }

        text = text.TrimStart();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = text.IndexOf('\n');
            text = newline >= 0 ? text[(newline + 1)..] : "";
        }

        return text.TrimStart('"', '“', ' ').TrimEnd();
    }

    /// <summary>把「译文 + 分隔行 + 重点词 JSON」拆开。</summary>
    internal static (string English, IReadOnlyList<StudyWord> Words) SplitWords(string raw)
    {
        var text = ThinkBlock.Replace(raw ?? "", "");
        var marker = WordsSplit.Match(text);
        if (!marker.Success)
            return (CleanModelText(text), Array.Empty<StudyWord>());
        var english = CleanModelText(text[..marker.Index]);
        var words = WordStudy.ParseStudy(text[(marker.Index + marker.Length)..]);
        return (english, words);
    }

    /// <summary>各家关闭「深度思考」的参数不一样；不认识的服务就不带。</summary>
    static Dictionary<string, object>? ThinkingOff(string host, string model)
    {
        if (host.Length == 0)
            return null;
        if (host.Contains("dashscope", StringComparison.OrdinalIgnoreCase) || host.Contains("siliconflow", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, object> { ["enable_thinking"] = false };
        if (host.Contains("bigmodel.cn", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("z.ai", StringComparison.OrdinalIgnoreCase)
            || host.Contains("volces.com", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, object> { ["thinking"] = new { type = "disabled" } };
        if (host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase) && model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, object> { ["reasoning_effort"] = "minimal" };
        return null;
    }

    void RememberStudy(string key, IReadOnlyList<StudyWord> words)
    {
        if (words.Count == 0)
            return;
        if (_studyCache.Count > 50)
            _studyCache.Clear();
        _studyCache[key] = words;
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

    static Task<(int Status, string Body)> GetAsync(string url, TimeSpan limit, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, url), limit, cancellationToken);

    /// <summary>发一个请求；超时抛 <see cref="TimeoutException"/>，跟用户取消区分开。</summary>
    static async Task<(int Status, string Body)> SendAsync(HttpRequestMessage request, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var owned = request;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            using var response = await Http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return ((int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    /// <summary>容错：去掉末尾的 /chat/completions，用户经常把完整地址粘进来。</summary>
    static string ChatBaseUrl(AppConfig config)
    {
        var url = config.OpenAiBaseUrl.Trim().TrimEnd('/');
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
        "Preserve the exact meaning and every detail: add nothing, omit nothing, explain nothing. "
        + "Keep names, numbers and line breaks.";

    /// <summary>考研难度以前要两次请求（先直译再润色），现在一次到位。</summary>
    static string TranslationPrompt(string level, bool withWords)
    {
        var style = level switch
        {
            "plain" =>
                "Translate the Chinese text into natural, everyday English with simple, idiomatic wording. ",
            "cet6" =>
                "Translate the Chinese text into clear, formal written English at upper-intermediate level (CET-6), "
                + "with some advanced vocabulary and complex sentences, but still natural. ",
            _ =>
                "Translate the Chinese text into English at C1-C2 level for Chinese students preparing for 考研英语, "
                + "so that it reads like a sentence from The Economist or a 考研英语 reading passage. "
                + "Prefer precise, less common words to plain ones (for example \"led me to appreciate\" rather than \"made me realize\", "
                + "\"formidable\" rather than \"difficult\", \"attain proficiency in\" rather than \"master\"). "
                + "Where it reads naturally, use one complex construction: a nominalization, a participle clause, a relative clause or an inversion. "
                + "Formal written register: no contractions, no spoken fillers. ",
        };

        if (!withWords)
            return style + FidelityRules + " Output only the English text, with no notes, labels or quotation marks.";

        return style + FidelityRules
            + " First output only the English text, with no notes, labels or quotation marks. "
            + "Then output a line containing only " + WordsMarker + " followed by one line of JSON: "
            + "{\"words\":[{\"en\":\"the word or phrase exactly as it appears in your English\",\"zh\":\"它在这句里的中文意思\",\"why\":\"不超过16个字的中文说明\"}]}. "
            + "Pick 3 to 6 items worth learning; skip proper nouns, product names, URLs and function words such as the, a, is, to, of.";
    }

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

    static HttpClient CreateClient()
    {
        // 连接多留一会儿，两句之间不用重新握手；能用 HTTP/2 就用。
        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
        };

        // 超时由每个请求自己控制，区分「超时」和「用户取消」。
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("InputTranslate/" + (typeof(Translator).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
        return client;
    }
}
