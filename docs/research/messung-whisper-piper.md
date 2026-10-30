# Messung: Whisper-Konfidenz und deutsche Piper-Stimmen

Stand: 30.10.2026. Anlass sind die Befunde aus dem ersten Test mit v0.10.0-beta.1 (PR fix/voice-after-beta, Punkte 1 und 5):
Whisper lieferte immer die Konfidenz 0.00, und für Deutsch sollte die beste Piper-Stimme gewählt werden (männlich nur thorsten,
high nur bei vertretbarer CPU-Last).

## Aufbau

- GitHub-Runner `windows-latest`: AMD EPYC 9V74, **2 logische Prozessoren** (1 Kern) – langsamer als ein Spiele-PC, die
  Verhältnisse zählen, nicht die absoluten Zeiten. Whisper läuft wie in Duopilo mit 4 Threads (hier also überbucht).
- Whisper.net 1.9.1 mit den Einstellungen von Duopilo: `ggml-base-q5_1`, CPU, NoContext, SingleSegment, Temperatur 0,
  eingebauter Wortschatz-Hinweis, Englisch. Je Satz 10 Läufe ohne und mit `WithProbabilities()`, abwechselnd, nach einem
  Aufwärmlauf. Sätze: acht Cockpit-/Funksätze, gesprochen von zwei englischen Piper-Stimmen, und `jfk.wav` aus whisper.cpp.
- Piper 2023.11.14-2 (`piper.exe`), **ein Prozess je Satz wie in Duopilo** (also mit Laden des Modells). Acht deutsche
  Smalltalk-Sätze je Stimme, je zwei Läufe. CPU = Benutzer- plus Kernelzeit von piper.exe. Verständlichkeit als Gegenprobe:
  Whisper `small-q5_1` (Deutsch) schreibt die Ausgabe ab, gezählt werden falsche Wörter (Wortfehlerrate); Zahlwörter und
  Ziffern („zwölf“/„12“) gelten als gleich.

## Ergebnis

**Whisper:** `WithProbabilities()` kostet nichts Messbares – im Mittel −15 ms (−0,3 %) bei ~4,6 s je Satz, je Satz zwischen
−56 und +164 ms (Rauschen). Ohne die Option ist die Konfidenz bei allen Sätzen 0.00, mit ihr 0.72–0.87. Nebenbei bestätigt:
„Hey Erik, how are you today?“ schreibt Whisper ohne den Namen im Hinweis als „Hey, Eric, …“.

**Piper, männlich:** thorsten-medium (1 von 79 Wörtern falsch, 0,75 s CPU je Satz, 0,21 s CPU je Sekunde Sprache).
thorsten-high ist nicht besser verständlich (2/79), braucht aber fast das Vierfache an CPU (2,9 s je Satz, 0,83 s CPU je
Sekunde Sprache, ein voller Kern) und entsprechend ~3 s, bevor der Satz beginnt – neben MSFS nicht vertretbar. karlsson-low
(5/79) fällt weg.

**Piper, weiblich:** Für Deutsch gibt es keine weibliche Einzelstimme über „low“ (kerstin-low, ramona-low, eva_k-x_low; dazu
mls-medium mit sehr vielen Sprechern aus Hörbüchern ohne Angabe des Geschlechts – dafür müsste man einen Sprecher von Hand
aussuchen). ramona-low: 4/79 falsch, kerstin-low: 9/79 (am schlechtesten verständlich, „Treffstoff“, „Ausweich für Kaffen“),
eva_k-x_low: 3/79 – gleichauf mit ramona, aber die kleinste Modellstufe (20 MB statt 60 MB). Gewählt: **ramona-low**. Wer
eva_k lieber hört, kann den Eintrag in `PiperSpeechEngine.Catalog` tauschen; das Anhören im Cockpit steht noch aus.

## Rohdaten

### Whisper.net 1.9.1 – WithProbabilities (base-q5_1, CPU, 4 threads, single segment, Duopilo prompt, en)

10 runs per clip and variant, interleaved, after one warm-up run each.

| Clip | median ms without | median ms with | Δ ms | confidence without | confidence with | text |
|---|---:|---:|---:|---:|---:|---|
| en_US-lessac-medium: Kopilot, flaps one. | 5214 | 5378 | +164 | 0.00 | 0.86 | Kopilot, flaps one. |
| en_GB-alan-medium: Gear up. | 4394 | 4396 | +2 | 0.00 | 0.77 | Gear up. |
| en_US-lessac-medium: Lufthansa four alpha bravo, climb flight level one two zero. | 4561 | 4583 | +22 | 0.00 | 0.72 | Lufthansa 4-alpha bravo. Climb flight level 120. |
| en_GB-alan-medium: Speed two five zero. | 4467 | 4411 | -56 | 0.00 | 0.86 | Speed 250. |
| en_US-lessac-medium: Hey Erik, how are you today? | 4464 | 4453 | -11 | 0.00 | 0.87 | Hey, Eric, how are you today? |
| en_GB-alan-medium: Düsseldorf Tower, Mayday, Mayday, Mayday. | 4536 | 4505 | -31 | 0.00 | 0.86 | Dusseldorf Tower, Mayday, Mayday, Mayday. |
| en_US-lessac-medium: Set QNH one zero one three. | 4427 | 4419 | -8 | 0.00 | 0.87 | Set QNH 1013. |
| en_GB-alan-medium: Before start checklist. | 4421 | 4403 | -17 | 0.00 | 0.75 | Before start checklist. |
| jfk.wav (11 s) | 4589 | 4600 | +11 | 0.00 | 0.85 | And so my fellow Americans ask not what your country can do for you, ask what you can do for your country. |

All clips: median without 4471.1 ms, with 4450.6 ms; mean without 4646.7 ms, with 4631.5 ms → Δ mean -15.2 ms (-0.3 %).

### Piper (piper.exe 2023.11.14-2, one process per sentence as in Duopilo)

8 German sentences × 2 runs. CPU = user+kernel time of piper.exe incl. model load. Cores = CPU time / wall time. WER = Whisper small-q5_1 (de) round trip, lower is better.

| Voice | Model MB | Sample rate | wall ms/sentence | CPU ms/sentence | CPU s per audio s | avg cores while running | WER |
|---|---:|---:|---:|---:|---:|---:|---:|
| de_DE-thorsten-medium | 60 | 22050 | 785 | 747 | 0.21 | 1.0 | 3.8 % |
| de_DE-thorsten-high | 109 | 22050 | 2947 | 2914 | 0.83 | 1.0 | 5.1 % |
| de_DE-karlsson-low | 60 | 16000 | 684 | 678 | 0.17 | 1.0 | 7.6 % |
| de_DE-kerstin-low | 60 | 16000 | 676 | 655 | 0.17 | 1.0 | 13.9 % |
| de_DE-ramona-low | 60 | 16000 | 673 | 647 | 0.17 | 1.0 | 7.6 % |
| de_DE-eva_k-x_low | 20 | 16000 | 614 | 604 | 0.14 | 1.0 | 3.8 % |

Die Wortfehlerrate in dieser Tabelle zählt Ziffern gegen Zahlwörter noch als Fehler; ohne das: thorsten-medium 1,3 %,
thorsten-high 2,5 %, karlsson-low 5,1 %, kerstin-low 11,4 %, ramona-low 5,1 %, eva_k-x_low 3,8 %.

<details><summary>Round-trip transcripts</summary>

| Voice | Text | Whisper heard |
|---|---|---|
| de_DE-thorsten-medium | Guten Morgen, Kapitän. Wir haben heute ruhiges Wetter auf der ganzen Strecke. | Guten Morgen Kapitän, wir haben heute ruhiges Wetter auf der ganzen Strecke. |
| de_DE-thorsten-medium | Der Wind in Frankfurt kommt aus Westen mit etwa zwölf Knoten. | Der Wind in Frankfurt kommt aus Westen mit etwa 12 Knoten. |
| de_DE-thorsten-medium | Ich schätze, wir landen ungefähr zehn Minuten vor dem Plan. | Ich schätze, wir landen ungefähr 10 Minuten vor dem Plan. |
| de_DE-thorsten-medium | Die Kabine ist bereit, und alle Passagiere sind angeschnallt. | Die Kabine ist bereit und alle Passagiere sind angeschnallt. |
| de_DE-thorsten-medium | Über den Alpen könnte es später etwas unruhig werden. | Über den Alpen könnte es später etwas unruhig werden. |
| de_DE-thorsten-medium | Unser Treibstoff reicht bequem bis zum Ausweichflughafen. | Unser Treibstoff reicht bequem bis zum Ausweichflughafen. |
| de_DE-thorsten-medium | Das war eine schöne Landung, vielen Dank für den angenehmen Flug. | Das war eine schöne Landung, vielen Dank für den angenehmen Flug. |
| de_DE-thorsten-medium | Möchtest du nach dem Start einen Kaffee aus der Bordküche? | Möchtest du nach dem Start ein Kaffee aus der Bordküche? |
| de_DE-thorsten-high | Guten Morgen, Kapitän. Wir haben heute ruhiges Wetter auf der ganzen Strecke. | Guten Morgen Kapitän. Wir haben heute ruhiges Wetter auf der ganzen Strecke. |
| de_DE-thorsten-high | Der Wind in Frankfurt kommt aus Westen mit etwa zwölf Knoten. | Der Wind in Frankfurt kommt aus Westen mit etwa 12 Knoten. |
| de_DE-thorsten-high | Ich schätze, wir landen ungefähr zehn Minuten vor dem Plan. | Ich schätze, wir landen ungefähr 10 Minuten vor dem Plan. |
| de_DE-thorsten-high | Die Kabine ist bereit, und alle Passagiere sind angeschnallt. | Die Kabine ist bereit und alle Passagiere sind angeschnallt. |
| de_DE-thorsten-high | Über den Alpen könnte es später etwas unruhig werden. | Über den Alten könnte es später etwas unruhig werden. |
| de_DE-thorsten-high | Unser Treibstoff reicht bequem bis zum Ausweichflughafen. | Unser Treibstoff reicht bequem bis zum Ausweichflughafen. |
| de_DE-thorsten-high | Das war eine schöne Landung, vielen Dank für den angenehmen Flug. | Das war eine schöne Landung, vielen Dank für den angenehmen Flug. |
| de_DE-thorsten-high | Möchtest du nach dem Start einen Kaffee aus der Bordküche? | Möchtest du nach dem Start ein Kaffee aus der Bordküche? |
| de_DE-karlsson-low | Guten Morgen, Kapitän. Wir haben heute ruhiges Wetter auf der ganzen Strecke. | Guten Morgen Kapitän. Wir haben heute ruhiges Wetter auf der ganzen Strecke. |
| de_DE-karlsson-low | Der Wind in Frankfurt kommt aus Westen mit etwa zwölf Knoten. | Der Wind in Frankfurt kommt aus Westen mit etwa 12 Knoten. |
| de_DE-karlsson-low | Ich schätze, wir landen ungefähr zehn Minuten vor dem Plan. | Ich schätze, wir landen ungefähr 10 Minuten vor dem Plan. |
| de_DE-karlsson-low | Die Kabine ist bereit, und alle Passagiere sind angeschnallt. | Die Kabine ist breit und alle Passagiere sind angeschnallt. |
| de_DE-karlsson-low | Über den Alpen könnte es später etwas unruhig werden. | Über den Alpen könnte es später etwas unruhig werden. |
| de_DE-karlsson-low | Unser Treibstoff reicht bequem bis zum Ausweichflughafen. | Unser Treibstoff reicht bequem bis zum Ausweichflughafen. |
| de_DE-karlsson-low | Das war eine schöne Landung, vielen Dank für den angenehmen Flug. | Das war eine schöne Landung. Vielen Dank für den angenehmen Flug. |
| de_DE-karlsson-low | Möchtest du nach dem Start einen Kaffee aus der Bordküche? | Wächtest du nach dem Start einen Café aus der Bordgehe? |
| de_DE-kerstin-low | Guten Morgen, Kapitän. Wir haben heute ruhiges Wetter auf der ganzen Strecke. | Duttenmorgen, Kapitän. Wir haben heute ruhiges Wetter auf der ganzen Strecke. |
| de_DE-kerstin-low | Der Wind in Frankfurt kommt aus Westen mit etwa zwölf Knoten. | Der Wind in Frankfurt kommt aus Westen mit etwa 12 Knoten. |
| de_DE-kerstin-low | Ich schätze, wir landen ungefähr zehn Minuten vor dem Plan. | Ich schätze, wir landen ungefähr 10 Minuten vor dem Plan. |
| de_DE-kerstin-low | Die Kabine ist bereit, und alle Passagiere sind angeschnallt. | Die Kabine ist bereit und alle Passatiere sind angeschneit. |
| de_DE-kerstin-low | Über den Alpen könnte es später etwas unruhig werden. | Über den Alpen könnte er später etwas unruhig werden. |
| de_DE-kerstin-low | Unser Treibstoff reicht bequem bis zum Ausweichflughafen. | Unser Treffstoff reicht bequem bis zum Ausweich für Kaffen. |
| de_DE-kerstin-low | Das war eine schöne Landung, vielen Dank für den angenehmen Flug. | Das war eine schöne Landung. Vielen Dank für den angenehmen Flug. |
| de_DE-kerstin-low | Möchtest du nach dem Start einen Kaffee aus der Bordküche? | Möchtest du nach dem Start einen Kaffee aus der Bordküche? |
| de_DE-ramona-low | Guten Morgen, Kapitän. Wir haben heute ruhiges Wetter auf der ganzen Strecke. | Roten Morgen, Kapitän! Wir haben heute ruhiges Wetter auf der ganzen Strecke. |
| de_DE-ramona-low | Der Wind in Frankfurt kommt aus Westen mit etwa zwölf Knoten. | Der Wind in Frankfurt kommt aus Westen mit etwa 12 Knoten. |
| de_DE-ramona-low | Ich schätze, wir landen ungefähr zehn Minuten vor dem Plan. | Ich schätze, wir nannten ungefähr 10 Minuten vor dem Plan. |
| de_DE-ramona-low | Die Kabine ist bereit, und alle Passagiere sind angeschnallt. | Die Kabine ist bereit und alle Passagiere sind angeschaltet. |
| de_DE-ramona-low | Über den Alpen könnte es später etwas unruhig werden. | über den Alpen könnte es später etwas unruhig werden. |
| de_DE-ramona-low | Unser Treibstoff reicht bequem bis zum Ausweichflughafen. | Unser Treibstoff reicht bequem bis zum Ausweichflughafen. |
| de_DE-ramona-low | Das war eine schöne Landung, vielen Dank für den angenehmen Flug. | Das war eine schöne Landung. Vielen Dank für den angenehmen Flug. |
| de_DE-ramona-low | Möchtest du nach dem Start einen Kaffee aus der Bordküche? | Möchtest du nach dem Start einen Kaffee aus der Bordröhe? |
| de_DE-eva_k-x_low | Guten Morgen, Kapitän. Wir haben heute ruhiges Wetter auf der ganzen Strecke. | Guten Morgen Kapitän! Wir haben heute ruhiges Wetter auf der ganzen Strecke. |
| de_DE-eva_k-x_low | Der Wind in Frankfurt kommt aus Westen mit etwa zwölf Knoten. | Der Wind in Frankfurt kommt aus Westen mit etwa zwölf Knoten. |
| de_DE-eva_k-x_low | Ich schätze, wir landen ungefähr zehn Minuten vor dem Plan. | Ich schätze, wir landen ungefähr zehn Minuten vor dem Plan. |
| de_DE-eva_k-x_low | Die Kabine ist bereit, und alle Passagiere sind angeschnallt. | Die Kabine ist bereit und alle Passagiere sind angeschnallt. |
| de_DE-eva_k-x_low | Über den Alpen könnte es später etwas unruhig werden. | Über den Alten könnte es später etwas unruhig werden. |
| de_DE-eva_k-x_low | Unser Treibstoff reicht bequem bis zum Ausweichflughafen. | Unser Treibstor freiget bequem bis zum Ausweichflughafen. |
| de_DE-eva_k-x_low | Das war eine schöne Landung, vielen Dank für den angenehmen Flug. | Das war eine schöne Landung. Vielen Dank für den angenehmen Flug. |
| de_DE-eva_k-x_low | Möchtest du nach dem Start einen Kaffee aus der Bordküche? | Möchtest du nach dem Start einen Kaffee aus der Bordküche? |

</details>
