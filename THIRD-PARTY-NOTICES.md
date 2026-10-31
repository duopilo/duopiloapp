# Third-party notices

Duopilo's own source code is licensed under the [MIT License](LICENSE). It uses, bundles or downloads the components below, which keep their own licenses. The MIT License does **not** cover them.

## Bundled with Duopilo

| Component | Used for | License |
|---|---|---|
| [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) (`ModelContextProtocol`, `ModelContextProtocol.AspNetCore`) | MCP server | Apache-2.0 |
| [KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp) (`KokoroSharp`, `KokoroSharp.CPU`) | Kokoro speech output | MIT |
| Kokoro-82M voices (English `a*`/`b*` voices, shipped via KokoroSharp) | Kokoro speech output | Apache-2.0 |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) (via KokoroSharp) | Kokoro, Silero VAD, Supertonic | MIT |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) incl. [whisper.cpp](https://github.com/ggml-org/whisper.cpp) runtimes | speech recognition | MIT |
| [NAudio](https://github.com/naudio/NAudio) (`NAudio.WinMM`, `NAudio.Wasapi`) | microphone, audio devices | MIT |
| [B612 font](https://github.com/polarsys/b612) | dashboard | SIL Open Font License 1.1 (`licenses/B612-OFL.txt`) |
| Supertonic inference code, adapted from Supertone's example | Supertonic speech output | MIT (`licenses/Supertonic-MIT.txt`) |

Each package also comes with its own license and notice files; the table names what Duopilo uses directly.

## Bundled data

In `data/` next to `Duopilo.exe` (details, revisions and attribution in [`src/Duopilo/data/README.md`](src/Duopilo/data/README.md)):

| Data | Source | License |
|---|---|---|
| FIR boundaries and names (`data/vatspy`) | [VAT-Spy Data Project](https://github.com/vatsimnetwork/vatspy-data-project) | CC BY-SA 4.0 |
| En-route sectors (`data/sectors`) | [VATGlasses Data Project](https://github.com/lennycolton/vatglasses-data) (VATSIM) | **CC BY-NC-SA 4.0 – non-commercial use only** |
| Airlines and callsigns (`data/openflights`) | [OpenFlights](https://openflights.org/data.php) | Open Database License 1.0 / Database Contents License |

The sector data may only be used non-commercially under its license, even though Duopilo's code is MIT.

## Downloaded at runtime (not part of the repository or the installer)

Duopilo downloads these only when the related feature is used, into `%LOCALAPPDATA%\Duopilo`:

| Component | Used for | License |
|---|---|---|
| Kokoro-82M model (`kokoro.onnx`) | Kokoro speech output | Apache-2.0 |
| Whisper models (ggml) | speech recognition | MIT |
| Silero VAD | voice activity detection | MIT |
| Supertonic 3 model and voices (Supertone) | first officer voice in English and German | BigScience OpenRAIL-M – use restrictions apply (e.g. no impersonation of real people; disclose machine-generated speech you publish). A copy is saved next to the model |
| Piper and Piper voices (optional fallback) | speech output | MIT; voices per voice |
| ECAM message tables `EwdMessages.ts` from [FlyByWire](https://github.com/flybywiresim/aircraft) and [Headwind](https://github.com/headwindsim/aircraft) | ECAM plain text | GPL-3.0 |

## Microsoft Flight Simulator SDK

`Microsoft.FlightSimulator.SimConnect.dll` and `SimConnect.dll` are part of the Microsoft Flight Simulator 2024 SDK. They are not in this repository and are subject to the SDK's own terms, not to the MIT License.

## Trademarks

Microsoft Flight Simulator is a trademark of Microsoft. Claude is a trademark of Anthropic. FlyByWire, Headwind, iniBuilds, SimBrief, VATSIM and other names belong to their respective owners. Duopilo is an independent project and not affiliated with or endorsed by any of them.
