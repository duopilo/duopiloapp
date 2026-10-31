using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Duopilo.Hosting;
using Duopilo.Localization;

namespace Duopilo.Smalltalk;

/// <summary>Ein Beitrag im Gespräch: "user" (Pilot) oder "assistant" (First Officer).</summary>
public sealed record ChatTurn(string Role, string Text)
{
    public const string User = "user";
    public const string Assistant = "assistant";
}

/// <summary>Eine Anfrage an das Sprachmodell: Systemtext (Rolle, Kontext), Verlauf mit der neuen Frage am Ende, Antwortlänge.</summary>
public sealed record ChatRequest(string System, IReadOnlyList<ChatTurn> Messages, int MaxTokens);

/// <summary>Das Sprachmodell hat nicht geantwortet (HTTP-Fehler, kaputte Antwort). Die Meldung enthält nie den Schlüssel.</summary>
public sealed class ChatModelException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    /// Leere Antwort, weil das Tokenlimit erreicht war (finish_reason "length", bei Claude stop_reason "max_tokens") – meist ein
    /// Reasoning-Modell, das alle Tokens zum Denken verbraucht hat.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>Tokenlimit der Anfrage (bei <see cref="Truncated"/>).</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>Text und Grund des Endes ("stop", "length" …) aus der Antwort eines Anbieters.</summary>
public sealed record ChatCompletion(string Text, string? FinishReason)
{
    /// <summary>Am Tokenlimit abgebrochen (OpenAI/Ollama "length", Claude "max_tokens").</summary>
    public bool HitTokenLimit => FinishReason is "length" or "max_tokens";
}

/// <summary>Schnittstelle zum Sprachmodell – austauschbar für Tests.</summary>
public interface IChatModel
{
    /// <summary>Antworttext des Modells (roh). Wirft bei Fehlern; Abbruch über <paramref name="ct"/> (Timeout).</summary>
    Task<string> CompleteAsync(SmalltalkConfig config, ChatRequest request, CancellationToken ct);
}

/// <summary>
/// HTTP an die drei Anbieter: Claude (Messages API), OpenAI-kompatibel (/chat/completions – auch LM Studio, vLLM, …) und Ollama
/// (/api/chat, ohne Streaming). Eigener HttpClient ohne Protokollierung der Anfragen – der Schlüssel steht nur im Header.
/// </summary>
public sealed class HttpChatModel(HttpClient? http = null) : IChatModel
{
    public const string AnthropicVersion = "2023-06-01";

    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http = http ?? SharedHttp;

    public async Task<string> CompleteAsync(SmalltalkConfig config, ChatRequest request, CancellationToken ct)
    {
        var (status, body) = await SendAsync(config, request, plain: false, ct);

        // Claude lehnt die Einstellung zum Denken ab (anderes Modell als erwartet): einmal ohne, mit Spielraum für die Denk-Tokens
        if (status == 400 && config.ClaudeThinking is not null && (body.Contains("thinking", StringComparison.OrdinalIgnoreCase)
            || body.Contains("effort", StringComparison.OrdinalIgnoreCase)))
        {
            request = request with { MaxTokens = request.MaxTokens + SmalltalkReasoning.Headroom(SmalltalkReasoning.Medium) };
            (status, body) = await SendAsync(config, request, plain: true, ct);
        }

        if (status is < 200 or >= 300)
        {
            throw new ChatModelException(Loc.T("smalltalk.http.status", status, ErrorText(body, config.ApiKey)));
        }

        ChatCompletion completion;
        try
        {
            completion = ParseCompletion(config.Provider, body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException or KeyNotFoundException)
        {
            throw new ChatModelException(Loc.T("smalltalk.http.badResponse"), ex);
        }

        // Nichts geschrieben, weil das Tokenlimit erreicht war: eigene, verständliche Meldung statt "leere Antwort"
        if (string.IsNullOrWhiteSpace(completion.Text) && completion.HitTokenLimit)
        {
            throw new ChatModelException(Loc.T("smalltalk.http.truncated", request.MaxTokens)) { Truncated = true, MaxTokens = request.MaxTokens };
        }

        return completion.Text;
    }

    private async Task<(int Status, string Body)> SendAsync(SmalltalkConfig config, ChatRequest request, bool plain, CancellationToken ct)
    {
        using var message = Build(config, request, plain);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Die HTTP-Anfrage für den Anbieter (öffentlich für Tests).</summary>
    /// <param name="plain">Claude ohne Einstellung zum Denken (Wiederholung, wenn das Modell sie ablehnt).</param>
    public static HttpRequestMessage Build(SmalltalkConfig config, ChatRequest request, bool plain = false)
    {
        var baseUrl = config.EffectiveUrl.TrimEnd('/');
        JsonObject body;
        HttpRequestMessage message;
        switch (config.Provider)
        {
            case SmalltalkProviders.Claude:
                body = new JsonObject
                {
                    ["model"] = config.EffectiveModel,
                    ["max_tokens"] = request.MaxTokens,
                    ["system"] = request.System,
                    ["messages"] = Messages(request.Messages, null),
                };

                // Claude 5 denkt von selbst, und das zählt zu max_tokens: für ein paar Sätze abschalten bzw. wenig Aufwand
                if (!plain && config.ClaudeThinking is { } thinking)
                {
                    if (thinking.Thinking is { } type)
                    {
                        body["thinking"] = new JsonObject { ["type"] = type };
                    }

                    if (thinking.Effort is { } effort)
                    {
                        body["output_config"] = new JsonObject { ["effort"] = effort };
                    }
                }

                message = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/messages");
                message.Headers.TryAddWithoutValidation("x-api-key", config.ApiKey);
                message.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                break;

            case SmalltalkProviders.OpenAi:
                body = new JsonObject
                {
                    ["model"] = config.EffectiveModel,
                    ["max_tokens"] = request.MaxTokens,
                    ["messages"] = Messages(request.Messages, request.System),
                };

                // Nur, wenn eingestellt bzw. bei gpt-oss – andere Server lehnen das unbekannte Feld teils ab
                if (config.ReasoningEffort is { } effort)
                {
                    body["reasoning_effort"] = effort;
                }

                message = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
                if (config.HasApiKey)
                {
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
                }

                break;

            case SmalltalkProviders.Ollama:
                body = new JsonObject
                {
                    ["model"] = config.EffectiveModel,
                    ["stream"] = false,
                    ["messages"] = Messages(request.Messages, request.System),
                    ["options"] = new JsonObject { ["num_predict"] = request.MaxTokens },
                };
                message = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/chat");
                break;

            default:
                throw new ChatModelException(Loc.T("smalltalk.config.off"));
        }

        message.Headers.UserAgent.ParseAdd(AppNames.Product);
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return message;
    }

    /// <summary>Text aus der Antwort des Anbieters.</summary>
    public static string Parse(string provider, string body) => ParseCompletion(provider, body).Text;

    /// <summary>
    /// Text und Grund des Endes aus der Antwort. Reasoning-Modelle (gpt-oss bei Groq) liefern ihre Gedanken in einem eigenen Feld
    /// ("reasoning") – das bleibt draußen; am Tokenlimit ist "content" dann leer oder fehlt.
    /// </summary>
    public static ChatCompletion ParseCompletion(string provider, string body)
    {
        var root = JsonNode.Parse(body) ?? throw new JsonException();
        switch (provider)
        {
            case SmalltalkProviders.Claude:
            {
                var sb = new StringBuilder();
                foreach (var block in root["content"]?.AsArray() ?? new JsonArray())
                {
                    if (block?["type"]?.GetValue<string>() == "text")
                    {
                        sb.Append(block["text"]?.GetValue<string>());
                    }
                }

                return new ChatCompletion(sb.ToString(), root["stop_reason"]?.GetValue<string>());
            }

            case SmalltalkProviders.OpenAi:
            {
                var choice = root["choices"]?[0] ?? throw new JsonException();
                var message = choice["message"] ?? throw new JsonException();
                var finish = choice["finish_reason"]?.GetValue<string>();
                var content = message["content"];
                if (content is null && finish is null)
                {
                    throw new JsonException();
                }

                return new ChatCompletion(content?.GetValue<string>() ?? "", finish);
            }

            case SmalltalkProviders.Ollama:
            {
                var finish = root["done_reason"]?.GetValue<string>();
                var content = root["message"]?["content"];
                if (content is null && finish is null)
                {
                    throw new JsonException();
                }

                return new ChatCompletion(content?.GetValue<string>() ?? "", finish);
            }

            default:
                throw new JsonException();
        }
    }

    private static JsonArray Messages(IReadOnlyList<ChatTurn> turns, string? system)
    {
        var array = new JsonArray();
        if (system is not null)
        {
            array.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        foreach (var turn in turns)
        {
            array.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Text });
        }

        return array;
    }

    /// <summary>Kurze Fehlermeldung aus der Antwort (error.message), ohne Schlüssel, höchstens 160 Zeichen.</summary>
    public static string ErrorText(string? body, string? apiKey)
    {
        string text;
        try
        {
            var root = body is null ? null : JsonNode.Parse(body);
            var error = root?["error"];
            text = (error is JsonObject ? error["message"]?.ToString() : error?.ToString()) ?? body ?? "";
        }
        catch (JsonException)
        {
            text = body ?? "";
        }

        text = SmalltalkService.Redact(text.ReplaceLineEndings(" ").Trim(), apiKey);
        return text.Length > 160 ? text[..160] + "…" : text;
    }
}
