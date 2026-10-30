using Kopilot.Input;

namespace Kopilot.Tests;

/// <summary>Modus "ausführen + Callout" der Sprachschleife im Eingabe-Modul (v0.9.2, Punkt 3).</summary>
public sealed partial class InputTests
{
    [Fact]
    public async Task Sofort_ausfuehren_ohne_Readback_mit_Callout_nach_der_Pruefung()
    {
        var (input, backend, _) = Create();
        backend.OnEvent = (name, data) =>
        {
            if (name == "A32NX.FCU_SPD_SET")
            {
                backend.State["L:A32NX_FCU_AFS_DISPLAY_SPD_MACH_VALUE"] = data;
            }
        };

        var result = await input.ExecuteNowAsync("fcu.speed", "250", default);

        Assert.Equal(InputResult.Executed, result.Status);
        Assert.True(result.Confirmed);
        Assert.Equal("Speed 250 set", result.Callout);
        Assert.Equal(new[] { "A32NX.FCU_SPD_SET:250", "A32NX.FCU_SPD_PULL:0" }, backend.Sent);
        Assert.Null(input.Pending);
        Assert.Equal(InputOutcome.Executed, input.History[0].Code);
        Assert.Equal(InputOutcome.Prepared, input.History[1].Code);
    }

    [Fact]
    public async Task Sofort_ausfuehren_beachtet_Sperren_und_Freigabe()
    {
        var (input, backend, _) = Create();
        backend.Current = Ground();

        // Fahrwerk einfahren am Boden: gesperrt wie bei prepare_action
        var refused = await input.ExecuteNowAsync("gear.up", null, default);
        Assert.Equal(InputResult.Refused, refused.Status);

        // Klappen über VFE: gesperrt
        backend.Current = Air(260);
        Assert.Equal(InputResult.Refused, (await input.ExecuteNowAsync("flaps", "2", default)).Status);
        Assert.Empty(backend.Sent);

        // Eingaben im Dashboard gesperrt
        var (locked, lockedBackend, _) = Create(enabled: false);
        Assert.Equal(InputResult.Disabled, (await locked.ExecuteNowAsync("fcu.speed", "250", default)).Status);
        Assert.Empty(lockedBackend.Sent);
    }

    [Fact]
    public async Task Aktion_mit_Rueckfrage_wird_nicht_sofort_ausgefuehrt()
    {
        var (input, backend, time) = Create();
        backend.Profile!.Actions.Add(new ProfileAction
        {
            Id = "xpdr.mode", Area = "Transponder", Label = "Transponder mode", Readback = "Transponder {value}",
            Parameter = new ActionParameter { Type = "choice", Options = new() { ["stby"] = 0, ["auto"] = 1, ["on"] = 2 }, Spoken = new() { ["stby"] = "standby" } },
            Steps = [new ActionStep { Set = "L:A32NX_TRANSPONDER_MODE", Value = J("\"{value}\"") }],
            Check = new ActionCheck { Variable = "L:A32NX_TRANSPONDER_MODE", Expect = J("\"{value}\"") },
        });
        backend.State["L:A32NX_TRANSPONDER_MODE"] = 1;

        var result = await input.ExecuteNowAsync("xpdr.mode", "stby", default);

        // Readback wie bei prepare_action, danach Rückfrage mit Warnhinweis – erst dann ausführen
        Assert.Equal(InputResult.AwaitingConfirmation, result.Status);
        Assert.True(result.CautionFollows);
        Assert.Equal("Transponder standby", result.Readback);
        Assert.Empty(backend.Sent);

        time.Now += TimeSpan.FromSeconds(3);
        var caution = await input.ConfirmAsync(result.Token!, "confirm", default);
        Assert.Equal(InputResult.AwaitingCautionConfirmation, caution.Status);
        Assert.Empty(backend.Sent);

        time.Now += TimeSpan.FromSeconds(3);
        Assert.Equal(InputResult.Executed, (await input.ConfirmAsync(result.Token!, "confirm", default)).Status);
        Assert.Equal(new[] { "L:A32NX_TRANSPONDER_MODE=0" }, backend.Sent);
    }

    [Fact]
    public async Task Sofort_ausfuehren_verwirft_die_offene_Aktion()
    {
        var (input, backend, _) = Create();
        backend.Current = Air(200);
        var prepared = await input.PrepareAsync("flaps", "1", default);
        Assert.NotNull(input.Pending);

        await input.ExecuteNowAsync("fcu.speed", "250", default);

        Assert.Null(input.Pending);
        Assert.Equal(InputResult.Failed, (await input.ConfirmAsync(prepared.Token!, "confirm", default)).Status);
    }

    [Theory]
    [InlineData("Speed {value} selected", "250", "Speed 250 set")]
    [InlineData("Flaps {value}", "2", "Flaps 2")]
    [InlineData("Heading {value} selected", "090", "Heading 090 set")]
    public void Callout_aus_dem_Readback(string readback, string value, string callout)
    {
        var action = new ProfileAction { Id = "x", Readback = readback };

        Assert.Equal(callout, ActionPlanner.CalloutFor(action, readback.Replace("{value}", value, StringComparison.Ordinal), value));
        Assert.Equal("Gear down", ActionPlanner.CalloutFor(new ProfileAction { Id = "gear.down", Callout = "Gear down" }, "Gear down", null));
    }
}
