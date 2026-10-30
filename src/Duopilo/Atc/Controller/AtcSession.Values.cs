using System.Globalization;
using System.Text.RegularExpressions;
using Kopilot.FlightPlan;

namespace Kopilot.Atc;

/// <summary>Variablen der Vorlagen, Höhen, Stationen und Lage auf der Route.</summary>
public sealed partial class AtcSession
{
    // ------------------------------------------------------------------ Variablen

    private AtcValue? Resolve(string name, Dictionary<string, AtcValue>? locals, AtcStation station)
    {
        if (locals is not null && locals.TryGetValue(name, out var local))
        {
            return local;
        }

        var input = _input;
        var ofp = Plan.Ofp;
        switch (name)
        {
            case "callsign":
                return AtcSpeech.Callsign(Plan.Callsign, _t.CallsignTelephony);
            case "station":
                return station.NameValue;
            case "freq":
                return station.Frequency;
            case "dest":
                return AtcSpeech.Airport(DestIcao, _stations.AirportName(DestIcao, ofp.Destination.Name));
            case "sid":
                return Plan.Sid is { } sid ? AtcSpeech.Ident(sid) : null;
            case "star":
                return Plan.Star is { } star ? AtcSpeech.Ident(star) : null;
            case "approach":
                return AtcSpeech.Approach(_overrides.GetValueOrDefault("approach") ?? Rules.DefaultApproach);
            case "runway":
                return ArrivalSide
                    ? AtcSpeech.Runway(_overrides.GetValueOrDefault("arrivalRunway") ?? Plan.ArrivalRunway ?? (input.OnGround == false ? input.AssignedRunway : null))
                    : AtcSpeech.Runway(_overrides.GetValueOrDefault("departureRunway") ?? Plan.DepartureRunway ?? input.AssignedRunway);
            case "initialAlt":
                return Altitude(InitialClimbFt);
            case "squawk":
                return AtcSpeech.Squawk(Squawk);
            case "level":
                return ClearedAltitudeFt is { } cleared ? Altitude(cleared) : null;
            case "altitude":
                return input.AltitudeFt is { } alt ? Altitude(alt) : null;
            case "speedKt":
                return input.IasKt is { } ias && ias > 30 ? AtcSpeech.Speed(Math.Round(ias / 5) * 5) : null;
            case "heading":
                return input.HeadingMagDeg is { } hdg ? AtcSpeech.Heading(Math.Round(hdg / 5) * 5) : null;
            case "wind":
                return input.WindDirectionDeg is { } dir && input.WindSpeedKt is { } spd ? AtcSpeech.Wind(dir, spd) : null;
            case "qnh":
                return QnhValue(input);
            case "taxiRoute":
            case "holdingPoint":
            case "gate":
            case "direction":
                return TaxiValue(name, input);
            case "entryFix":
                return Plan.Oceanic is { } oceanic ? FixValue(oceanic.EntryFix) : null;
            case "route":
                return OceanicRouteValue();
            case "time":
                return AtcSpeech.Time(Now(input));
            default:
                return null;
        }
    }

    private Dictionary<string, AtcValue>? ClearedLocals() =>
        ClearedAltitudeFt is { } cleared ? new Dictionary<string, AtcValue> { ["level"] = Altitude(cleared) } : null;

    private Dictionary<string, AtcValue> LevelLocals(int feet, string verb) => new()
    {
        ["level"] = Altitude(feet),
        ["levelVerb"] = AtcValue.Text(_t.Word(verb, verb)),
    };

    private AtcValue LevelVerb(double? feet)
    {
        var current = _input.AltitudeFt ?? 0;
        var verb = feet is { } f && f > current + 300 ? "climb" : feet is { } g && g < current - 300 ? "descend" : "maintain";
        return AtcValue.Text(_t.Word(verb, verb));
    }

    private Dictionary<string, AtcValue> OceanicRequestLocals(AtcOceanicSegment oceanic)
    {
        var locals = new Dictionary<string, AtcValue>
        {
            ["entryFix"] = FixValue(oceanic.EntryFix),
            ["level"] = Altitude(Math.Max(Plan.CruiseFt, ClearedAltitudeFt ?? 0)),
        };
        if (Plan.FixDistanceNm(oceanic.EntryFix) is { } entryNm)
        {
            locals["eta"] = Eta(entryNm);
        }

        return locals;
    }

    private Dictionary<string, AtcValue>? PositionReportLocals()
    {
        if (Plan.Oceanic is not { } oceanic || _reportIndex >= oceanic.ReportFixes.Count)
        {
            return null;
        }

        var fix = oceanic.ReportFixes[_reportIndex];
        var locals = new Dictionary<string, AtcValue>
        {
            ["fix"] = FixValue(fix),
            ["time"] = AtcSpeech.Time(Now(_input)),
        };
        if (_input.AltitudeFt is { } alt)
        {
            locals["level"] = Altitude(alt);
        }

        // Nächster Meldepunkt, nach dem Austritt der nächste Fix der Route
        var next = _reportIndex + 1 < oceanic.ReportFixes.Count ? oceanic.ReportFixes[_reportIndex + 1] : NextFixWithPosition(oceanic.ExitFix);
        if (next is { } n)
        {
            locals["nextFix"] = FixValue(n);
            if (Plan.FixDistanceNm(n) is { } nm)
            {
                locals["eta"] = Eta(nm);
            }
        }

        return locals;
    }

    private int? NextFixWithPosition(int after)
    {
        for (var i = after + 1; i < Plan.Ofp.Fixes.Count; i++)
        {
            if (Plan.Ofp.Fixes[i].Lat is not null && Plan.Ofp.Fixes[i].Ident is not ("TOC" or "TOD"))
            {
                return i;
            }
        }

        return null;
    }

    private string CenterContactMessage()
    {
        var vs = _input.VerticalSpeedFpm ?? 0;
        var alt = _input.AltitudeFt ?? 0;
        if (vs > 500 && (ClearedAltitudeFt ?? 0) > alt + 500)
        {
            return AtcMessageIds.CenterContactClimbing;
        }

        return vs < -500 ? AtcMessageIds.CenterContactDescending : AtcMessageIds.CenterContactLevel;
    }

    /// <summary>Höhe mit der passenden Übergangshöhe: abflugseitig die des Startflughafens, sonst die des Ziels.</summary>
    private AtcValue Altitude(double feet)
    {
        var arrival = ArrivalSide || (_alongNm is { } along && along > Plan.TotalNm / 2);
        var airport = arrival ? Plan.Ofp.Destination : Plan.Ofp.Origin;
        return AtcSpeech.Altitude(feet, airport.TransitionAltitudeFt ?? DefaultTransitionAltitude(airport.Icao));
    }

    private static int DefaultTransitionAltitude(string? icao) =>
        icao is { Length: > 0 } && icao[0] is 'K' or 'C' or 'P' ? 18000 : 5000;

    private AtcValue? QnhValue(AtcInput input)
    {
        var arrival = ArrivalSide || (_alongNm is { } along && along > Plan.TotalNm / 2);
        var airport = arrival ? Plan.Ofp.Destination : Plan.Ofp.Origin;
        var inHg = airport.Icao is { Length: > 0 } icao && icao[0] is 'K' or 'C' or 'P';
        var near = arrival ? (DistanceToDest(input) is < 60) : (DistanceFromOrigin(input) is < 30);
        if (near && input.QnhHpa is { } sim && sim > 900)
        {
            return AtcSpeech.Qnh(sim, inHg);
        }

        if (MetarQnh(airport.Metar) is { } metar)
        {
            return AtcSpeech.Qnh(metar, inHg);
        }

        return input.QnhHpa is { } fallback && fallback > 900 ? AtcSpeech.Qnh(fallback, inHg) : null;
    }

    /// <summary>QNH aus dem METAR des OFP: "Q1013" (hPa) oder "A2992" (inHg).</summary>
    public static double? MetarQnh(string? metar)
    {
        if (string.IsNullOrWhiteSpace(metar))
        {
            return null;
        }

        var m = MetarPressure().Match(metar);
        if (!m.Success)
        {
            return null;
        }

        var value = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return m.Groups[1].Value == "Q" ? value : value / 100 / 0.0295299830714;
    }

    private AtcValue? TaxiValue(string name, AtcInput input)
    {
        if (_overrides.TryGetValue(name, out var manual))
        {
            return name switch
            {
                "taxiRoute" => AtcSpeech.TaxiRoute(manual.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)),
                "direction" => AtcValue.Text(manual.ToLowerInvariant(), manual.ToLowerInvariant()),
                _ => AtcSpeech.Ident(manual),
            };
        }

        var arrival = ArrivalSide;
        var request = new TaxiRequest(
            arrival ? DestIcao : OriginIcao,
            arrival ? Plan.ArrivalRunway : Plan.DepartureRunway,
            input.Lat,
            input.Lon,
            input.HeadingMagDeg,
            _overrides.GetValueOrDefault("gate"));
        var route = arrival ? _context.TaxiRoutes.Arrival(request) : _context.TaxiRoutes.Departure(request);
        if (route is null)
        {
            return null;
        }

        return name switch
        {
            "taxiRoute" => route.Taxiways.Count > 0 ? AtcSpeech.TaxiRoute(route.Taxiways) : null,
            "holdingPoint" => route.HoldingPoint is { Length: > 0 } hp ? AtcSpeech.Ident(hp) : null,
            "gate" => route.Gate is { Length: > 0 } g ? AtcSpeech.Ident(g) : null,
            "direction" => route.PushbackFacing is { Length: > 0 } d ? AtcValue.Text(d.ToLowerInvariant(), d.ToLowerInvariant()) : null,
            _ => null,
        };
    }

    private AtcValue FixValue(int fixIndex)
    {
        var fix = Plan.Ofp.Fixes[fixIndex];
        return AtcSpeech.IsLatLonIdent(fix.Ident) && fix.Lat is { } lat && fix.Lon is { } lon
            ? AtcSpeech.Position(lat, lon)
            : AtcSpeech.Fix(fix.Ident, fix.Name, fix.Lat, fix.Lon);
    }

    /// <summary>Oceanic-Route nach dem Eintrittspunkt bis zum Austritt: "55N030W 54N040W … LOMSI".</summary>
    private AtcValue? OceanicRouteValue()
    {
        if (Plan.Oceanic is not { } oceanic)
        {
            return null;
        }

        var values = oceanic.ReportFixes.Where(f => f != oceanic.EntryFix).Select(FixValue).ToList();
        return values.Count == 0
            ? null
            : AtcValue.Text(string.Join(" ", values.Select(v => v.Written)), string.Join(", ", values.Select(v => v.Spoken)));
    }

    private AtcValue Eta(double targetNm)
    {
        var gs = _input.GroundSpeedKt is > 100 ? _input.GroundSpeedKt.Value : 450;
        var hours = Math.Max(0, targetNm - (_alongNm ?? 0)) / gs;
        return AtcSpeech.Time(Now(_input) + TimeSpan.FromHours(hours));
    }

    private static TimeSpan Now(AtcInput input) =>
        input.ZuluTimeSec is { } s && double.IsFinite(s) ? TimeSpan.FromSeconds(((s % 86400) + 86400) % 86400) : input.TimeUtc.TimeOfDay;

    // ------------------------------------------------------------------ Höhen

    private int InitialClimbFt => Math.Min(Plan.CruiseFt, _t.Airport(OriginIcao)?.InitialClimbFt ?? Rules.InitialClimbFt);

    private int DepartureLevelFt => Math.Max(InitialClimbFt, Math.Min(Plan.CruiseFt, Rules.DepartureClimbFt));

    private int ApproachLevelFt
    {
        get
        {
            var elevation = Plan.Ofp.Destination.ElevationFt ?? 0;
            var minimum = (int)Math.Ceiling((elevation + 2000) / 1000.0) * 1000;
            return Math.Max(Rules.ApproachDescentFt, minimum);
        }
    }

    // ------------------------------------------------------------------ Stationen und Lage

    private AtcStation OriginStation(AtcStationType type) => _stations.Airport(OriginIcao, Plan.Ofp.Origin.Name, type);

    private AtcStation DestStation(AtcStationType type) => _stations.Airport(DestIcao, Plan.Ofp.Destination.Name, type);

    private AtcStation OceanicStation() =>
        Plan.Oceanic is { } oceanic ? _stations.Fir(oceanic.EntryFir, oceanic: true) : _stations.Fir(null, oceanic: true);

    private void UpdateRoute(AtcInput input)
    {
        if (input.Lat is not { } lat || input.Lon is not { } lon || Plan.Points.Count < 2)
        {
            return;
        }

        var progress = RouteTracker.Locate(Plan.Points, lat, lon, _legHint);
        if (progress is null)
        {
            return;
        }

        _legHint = progress.LegIndex;
        _alongNm = progress.DistanceFlownNm;
    }

    private void UpdateMotion(AtcInput input)
    {
        var now = input.TimeUtc;
        var gs = input.GroundSpeedKt ?? 0;
        if (gs > 5)
        {
            _movingSince ??= now;
            _stoppedSince = null;
            if (now - _stepSince > TimeSpan.Zero)
            {
                _movedInStep = true;
            }
        }
        else
        {
            _movingSince = null;
            if (gs < 1)
            {
                _stoppedSince ??= now;
            }
            else
            {
                _stoppedSince = null;
            }
        }
    }

    private double StoppedFor(DateTimeOffset now) => _stoppedSince is { } s ? (now - s).TotalSeconds : 0;

    private double MovingFor(DateTimeOffset now) => _movingSince is { } s ? (now - s).TotalSeconds : 0;

    private double? DistanceToDest(AtcInput input) =>
        input.Lat is { } lat && input.Lon is { } lon && Plan.Ofp.Destination.Lat is { } dlat && Plan.Ofp.Destination.Lon is { } dlon
            ? RouteMath.DistanceNm(lat, lon, dlat, dlon)
            : null;

    private double? DistanceFromOrigin(AtcInput input) =>
        input.Lat is { } lat && input.Lon is { } lon && Plan.Ofp.Origin.Lat is { } olat && Plan.Ofp.Origin.Lon is { } olon
            ? RouteMath.DistanceNm(lat, lon, olat, olon)
            : null;

    /// <summary>Squawk pro Flug fest: 4 Oktalziffern, ohne Sondercodes (7500/7600/7700, 7000, 2000, 1200).</summary>
    public static string GenerateSquawk(string flightKey)
    {
        var random = new Random(AtcVoiceAssigner.StableHash(flightKey + "|squawk"));
        while (true)
        {
            var code = string.Concat(random.Next(1, 7).ToString(CultureInfo.InvariantCulture), random.Next(0, 8).ToString(CultureInfo.InvariantCulture),
                random.Next(0, 8).ToString(CultureInfo.InvariantCulture), random.Next(0, 8).ToString(CultureInfo.InvariantCulture));
            if (code is not ("1200" or "2000") && !code.EndsWith("00", StringComparison.Ordinal))
            {
                return code;
            }
        }
    }

    [GeneratedRegex(@"\b([QA])(\d{4})\b")]
    private static partial Regex MetarPressure();
}
