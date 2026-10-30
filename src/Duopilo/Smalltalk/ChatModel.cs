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
public sealed class ChatModelException(string message, Exception? inner = null) : Exception(message, inner);

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
        using var message = Build(config, request);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ChatModelException(Loc.T("smalltalk.http.status", (int)response.StatusCode, ErrorText(body, config.ApiKey)));
        }

        try
        {
            return Parse(config.Provider, body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException or KeyNotFoundException)
        {
            throw new ChatModelException(Loc.T("smalltalk.http.badResponse"), ex);
        }
    }

    /// <summary>Die HTTP-Anfrage für den Anbieter (öffentlich für Tests).</summary>
    public static HttpRequestMessage Build(SmalltalkConfig config, ChatRequest request)
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
    public static string Parse(string provider, string body)
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

                return sb.ToString();
            }

            case SmalltalkProviders.OpenAi:
                return root["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? throw new JsonException();

            case SmalltalkProviders.Ollama:
                return root["message"]?["content"]?.GetValue<string>() ?? throw new JsonException();

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
