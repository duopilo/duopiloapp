using System.Net;
using System.Text;
using System.Text.Json;
using Duopilo.Smalltalk;

namespace Duopilo.Tests;

/// <summary>
/// Testflug 31.10.: Smalltalk über Claude (claude-haiku-5-5) kam leer zurück – „all 324 tokens were used before any text was
/// written“. Claude 5 denkt von selbst (adaptiv), die Denk-Tokens zählen zu max_tokens. Für ein paar Sätze: Denken aus bzw. wenig.
/// </summary>
public sealed class SmalltalkClaudeThinkingTests
{
    private static SmalltalkConfig Claude(string model = "", string reasoning = "auto") =>
        SmalltalkConfig.From(new SmalltalkOptions { Provider = "claude", ApiKey = "sk-ant-test", Model = model, Reasoning = reasoning });

    private static async Task<JsonElement> BodyAsync(SmalltalkConfig config, bool plain = false)
    {
        using var message = HttpChatModel.Build(config, new ChatRequest("system", [new ChatTurn(ChatTurn.User, "Hallo, Erik.")], config.MaxTokens), plain);
        using var doc = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Haiku_5_5_denkt_fuer_den_Smalltalk_nicht()
    {
        var config = Claude();

        var body = await BodyAsync(config);

        Assert.Equal("claude-haiku-5-5", body.GetProperty("model").GetString());
        Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("output_config", out _));
        Assert.Equal(config.AnswerTokens, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Sonnet_5_5_ohne_Denken_vor_der_Antwort()
    {
        var body = await BodyAsync(Claude("claude-sonnet-5-5"));

        Assert.Equal("between_tools", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("output_config", out _));
    }

    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-fable-5-1")]
    [InlineData("claude-mythos-5-1")]
    public async Task Modelle_die_immer_denken_bekommen_wenig_Aufwand_und_Spielraum(string model)
    {
        var config = Claude(model);

        var body = await BodyAsync(config);

        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.Equal("low", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(config.AnswerTokens + 512, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Eingestellter_Aufwand_gilt_mit_Spielraum()
    {
        var config = Claude(reasoning: "medium");

        var body = await BodyAsync(config);

        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.Equal("medium", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(config.AnswerTokens + 1024, config.MaxTokens);
        Assert.Null(config.ReasoningEffort);
    }

    [Theory]
    [InlineData("claude-haiku-4-5")]
    [InlineData("claude-3-5-sonnet-latest")]
    [InlineData("claude-sonnet-4-5-20250929")]
    public async Task Aeltere_Modelle_wie_bisher_ohne_Einstellung(string model)
    {
        var config = Claude(model);

        var body = await BodyAsync(config);

        Assert.Null(config.ClaudeThinking);
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.False(body.TryGetProperty("output_config", out _));
        Assert.Equal(config.AnswerTokens, config.MaxTokens);
    }

    [Fact]
    public void Andere_Anbieter_bekommen_nichts_davon()
    {
        var openai = SmalltalkConfig.From(new SmalltalkOptions { Provider = "openai", Url = "http://127.0.0.1:1234/v1", Model = "claude-haiku-5-5" });

        Assert.Null(openai.ClaudeThinking);
    }

    [Fact]
    public async Task Ohne_Einstellung_nichts_davon_in_der_Anfrage()
    {
        var body = await BodyAsync(Claude(), plain: true);

        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.False(body.TryGetProperty("output_config", out _));
    }

    private sealed class Recording(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(doc.RootElement.Clone());
            var (status, body) = responses[Math.Min(Requests.Count - 1, responses.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Lehnt_das_Modell_die_Einstellung_ab_einmal_ohne()
    {
        var handler = new Recording(
            (HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"thinking.type: disabled is not supported for this model"}}"""),
            (HttpStatusCode.OK, """{"content":[{"type":"text","text":"Hallo, Captain!"}],"stop_reason":"end_turn"}"""));
        using var http = new HttpClient(handler);
        var config = Claude();

        var text = await new HttpChatModel(http).CompleteAsync(config, new ChatRequest("system", [new ChatTurn(ChatTurn.User, "Hallo")], config.MaxTokens), CancellationToken.None);

        Assert.Equal("Hallo, Captain!", text);
        Assert.Equal(2, handler.Requests.Count);
        Assert.True(handler.Requests[0].TryGetProperty("thinking", out _));
        Assert.False(handler.Requests[1].TryGetProperty("thinking", out _));
        Assert.Equal(config.MaxTokens + 1024, handler.Requests[1].GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Anderer_Fehler_wird_nicht_wiederholt()
    {
        var handler = new Recording((HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"messages: empty"}}"""));
        using var http = new HttpClient(handler);
        var config = Claude();

        await Assert.ThrowsAsync<ChatModelException>(() =>
            new HttpChatModel(http).CompleteAsync(config, new ChatRequest("system", [new ChatTurn(ChatTurn.User, "Hallo")], config.MaxTokens), CancellationToken.None));
        Assert.Single(handler.Requests);
    }
}
