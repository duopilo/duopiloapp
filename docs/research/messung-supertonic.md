# Messung: Supertonic 3 als deutsche Stimme (30.10.2026)

Frage: Gibt es eine lokale deutsche Stimme, die verständlicher ist als Piper (vor allem weiblich – Piper hat Deutsch weiblich nur
in „low“) und neben MSFS auf der CPU läuft?

## Methode

- GitHub-Runner `windows-latest` (AMD EPYC, 2 vCPU), ONNX Runtime auf der CPU (Standard-Threads).
- Supertonic 3, Revision `aafc6e32416a594460b32413efc49d7fe4ce6d46` von `supertone-oss-archive/supertonic-3`, Inferenz wie im
  C#-Beispiel von Supertone: `total-step` 8, `speed` 1,05, Sprache `de`, alle zehn Stimmen (M1–M5, F1–F5).
- Je Stimme drei Sätze: Zungenbrecher „Zwei weiße Zwerge zwängen sich zwischen zwei Zweige.“, „Guten Abend, ich bin heute dein
  First Officer. Das Wetter in Frankfurt sieht gut aus, wir sollten pünktlich landen.“ (Deutsch mit englischen Wörtern) und
  „Flaps one, speed checked. Positive rate, gear up.“ (Englisch).
- Verständlichkeit: Rücktranskription mit Whisper.net 1.9.1, `ggml-base-q5_1` (wie Duopilo) und `ggml-medium-q5_0`, Sprache `de`,
  Temperatur 0, ohne Prompt. Wortfehler nach Kleinschreibung, ohne Satzzeichen, „2“ = „zwei“.
- Vergleich mit dem gleichen Zungenbrecher: Piper `thorsten-medium`, `ramona-low`, `kerstin-low`, `eva_k-x_low` und eine
  Hörprobe des deutschen Kokoro-Community-Modells (kikiri).
- Klang: Hörprobe aller zehn Stimmen durch Florian.

## Ergebnis Verständlichkeit

Zungenbrecher (8 Wörter), Whisper base. Gezählt sind Wortfehler; ein zusammengeschriebenes „Weißezwerge“ zählt als zwei, obwohl
es nur die Schreibung ist.

| Stimme | gehört | Fehler |
|---|---|---:|
| Supertonic M1, F2, F3, F4, F5 | „… Zwerge **zwingen** sich …“ | 1 |
| Supertonic M2, M3, M4, F1 | „Zwei **Weißezwerge zwingen** …“ | 3 |
| Supertonic M5 | „2 Weiße **2ge** zwingen …“ | 2 |
| Kokoro Deutsch (kikiri) | „… **zwingen** …“ | 1 |
| Piper thorsten-medium | „2 **Weissezwerge zwingen** …“ | 3 |
| Piper eva_k-x_low | „2 Weißer 2 Gezvenen sich zwischen 2 Zweige“ | 3 |
| Piper ramona-low | „2 weißet Werke 2 in sich zwischen 2 und 2.“ | 6 |
| Piper kerstin-low | „2 weißer Zwerd, zwingend zischend, 2, 2 hier.“ | 6 |

„zwängen“ → „zwingen“ macht Whisper base bei fast jeder Stimme. Mit Whisper medium sind alle zehn Supertonic-Stimmen fehlerfrei, ebenso
Thorsten, kerstin und kikiri; ramona 1 Fehler, eva_k 2.

First-Officer-Satz (19 Wörter): Whisper base bei 9 von 10 Supertonic-Stimmen fehlerfrei, F4 spricht „First Officer“ so deutsch,
dass base „Fürstoffisser“ versteht; Whisper medium bei allen fehlerfrei.

## Ergebnis Zeit (2 vCPU)

| Satz | Audio | Rechenzeit | CPU-s je Audio-s |
|---|---:|---:|---:|
| Zungenbrecher | 3,3–4,0 s | 1,1–1,7 s | 0,32–0,47 |
| First-Officer-Satz | 6,3–7,9 s | 2,1–2,6 s | 0,30–0,34 |
| Callout (Englisch) | 3,3–4,0 s | 1,1–1,4 s | 0,33–0,41 |

Gesamt: 146,7 s Audio in 49,7 s (Ø 1,66 s je Satz). Zum Vergleich Piper thorsten-medium: 0,21 CPU-s je Audio-s (ein Prozess je
Satz). Supertonic braucht also etwa 1,5-mal so viel CPU, bleibt aber deutlich schneller als Echtzeit. Die ONNX Runtime nutzte
auf dem Runner praktisch einen Kern (CPU-Zeit ≈ Wandzeit); Duopilo stellt 2 Threads ein (`Smalltalk:SupertonicThreads`).

### Duopilo-Engine (PR #37) auf dem Runner

Gleiche Maschine (AMD EPYC 9V74, 1 Kern / 2 logische Prozessoren), Sprechtempo 1,1 wie im Dashboard:

| | kurzer Satz (~2,3 s Audio) | langer Satz (~6,3 s Audio) |
|---|---:|---:|
| Duopilo-Engine, 1 Thread, im laufenden Betrieb | 1,4 s | 3,1 s |
| Duopilo-Engine, erster Satz (Modell laden) | 6,5 s | – |
| C#-Beispiel von Supertone (ORT 1.20.1) | – | 3,2 s (7,7 s Audio, Tempo 1,05) |

Die Duopilo-Engine ist so schnell wie das Beispiel (Rechenzeit hängt vor allem an der Textlänge). 1 und 2 Threads sind auf
diesem Runner gleich schnell (nur ein echter Kern); auf einem PC mit mehreren Kernen sollten 2 Threads (Standard,
`Smalltalk:SupertonicThreads`) die Zeit verkürzen. Die Zeiten schwanken zwischen Runnern deutlich (gleicher Satz 2,4–3,2 s).

## Auswahl

Nach Hörprobe: **M1, M2** (männlich) und **F1** (weiblich) klingen gut; die übrigen nicht übernommen. Herstellerbeschreibung:
M1 lebhaft, klar; M2 tief, ruhig; F1 ruhig, etwas tiefer.

## Lizenz

- Gewichte: BigScience OpenRAIL-M – frei nutzbar, auch kommerziell; Nutzungsbeschränkungen (Anhang A), u. a. keine
  Nachahmung echter Personen, keine Täuschung, Belästigung, Diskriminierung, keine medizinische Beratung, maschinell erzeugte
  Inhalte beim Veröffentlichen kennzeichnen. Wer das Modell weitergibt, muss die Lizenz beilegen und die Beschränkungen
  weitergeben – Duopilo gibt es nicht weiter, sondern lädt es beim Nutzer direkt von Hugging Face (die Lizenz wird mitgeladen).
- Inferenz-Code: nach dem MIT-lizenzierten Beispiel (`licenses\Supertonic-MIT.txt`).
- Das Repo ist archiviert – keine Updates mehr; die gepinnte Revision mit Prüfsummen bleibt gültig, solange Hugging Face sie
  ausliefert.

## Rohdaten

Branch `bench/voice-after-beta`: `results/supertonic/supertonic.md` (Whisper je Stimme, Zeiten je Satz, Hörproben als WAV),
`results/check.md` (Zungenbrecher Piper und kikiri), `results/supertonic-files.txt` (Größen und SHA-256 der Revision),
`results/live/` (Duopilo-Engine: Hörproben, Whisper, Threads 1/2) und `results/same/` (Beispiel und Duopilo auf derselben Maschine).
