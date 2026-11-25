# Pedal Bass Filterer

A managed ReBuzz emulation of **FSM Philta**, Krzysztof Foltman's LFO-swept
6-pole filter for Buzz. It has the same parameters as the original, in the same
order, and it produces the same output, sample for sample (see
[Verification](#verification)).

Version 1.0.0 · managed effect · net10.0-windows · GPL-3.0-or-later

## Parameters

The first eight are the original's track parameters: same names, ranges and
defaults.

| Name | Range | Default | What it does |
|---|---|---|---|
| Cutoff | 0–240 | 120 | Filter cutoff. The read-out is the centre frequency for the current filter type, before the LFO. Each type has its own curve (for example 2L FMish runs 132 Hz–5.3 kHz, 6L HiSquelch 132 Hz–12.7 kHz). |
| Resonance | 0–240 | 120 | Resonance. Each type uses it differently: 6L Separated gets *narrower* peaks as it goes down, and 2L FMish moves its two peaks. |
| Filter | 0–7 | 6 (2L FMish) | 6L Multipeak, 6L Separated, 6L HiSquelch, 4L TwinPeaks, 2L FrontFlt, 2L LaserOne, 2L FMish, Notchez. |
| LFO rate | 0–254 | 30 | 0–239 is 0.05 Hz to 26 Hz. 240–254 is one LFO cycle in ticks: 1, 2, 3, 4, 6, 8, 12, 16, 24, 28, 32, 48, 64, 96, 128. |
| LFO mod | 0–240 | 60 | Depth, in cutoff steps peak to peak. |
| LFO Shape | 0–16 | sine | sine, saw up, saw down, square, triangle, weird 1–4, steps up/down, upsaws up/down, dnsaws up/down, S'n'H 1, S'n'H 2. |
| Inertia | 0–60 | 10 | How slowly the cutoff follows its target (LFO plus Cutoff). 0 is fastest: up to 10 cutoff steps per 64 samples. |
| Set phase | 0–127 | 0 | Restarts the LFO at this point of its cycle whenever it's written. |
| Thev factor | 0–20 | 0 | The original's *Theviderness factor* attribute, as a parameter: it reduces resonance at low cutoff. |
| Channels | Mono, Stereo | Mono | **Mono** is what the original does in ReBuzz: left and right are summed, filtered once, and sent to both sides. **Stereo** filters each side separately with the same LFO and cutoff. Both give the same level for a centred signal. |

The amp and pan rows above these in the parameter window belong to the input
connection, not to the machine.

## What the original does

Recovered from the FSM Philta 0.1a DLL (`FSM Philta version 0.1a`).

- **Three biquads in series**, coefficients recomputed every 64 samples from the
  current cutoff. The 64-sample blocks restart at every Work call. Output is
  doubled.
- **The 0.1a filter types are not the ones in the later published source.** The
  Philta source in the Buzztrax *buzzmachines* collection (2001–2007) has
  eighteen types, and its type 6 is *4L Phlatt*. The 0.1a binary has the eight
  types listed above. Even the types that share a name use different cutoff
  curves, Q ranges and section gains. Everything here follows the binary:

  | Type | Cutoff curve (Hz) | Sections |
  |---|---|---|
  | 6L Multipeak | 528·16^(c/240) | LP at f/3, 2f/3, f |
  | 6L Separated | 264·40^(c/240), max 16 kHz | LP at f and f·(1±sep) |
  | 6L HiSquelch | 132·96^(c/240) | LP ×3 at f |
  | 4L TwinPeaks | 264·40^(c/240) | LP ×2, peak EQ at f/2 |
  | 2L FrontFlt | 264·40^(c/240) | LP, peak EQ at f/2 and f/4 |
  | 2L LaserOne | 264·40^(c/240) | LP, peak EQ at 3f/4 and f/2 |
  | 2L FMish | 132·40^(c/240) | LP, two peak EQs that move with Resonance |
  | Notchez | 132·80^((240−c)/240) | three notches |

  All curves are clamped to 33 Hz–20 kHz, which matters only when the LFO
  pushes the cutoff outside 0–240.
- **Coefficients assume 44.1 kHz** at any sample rate (the constants are
  hard-coded). At 48 kHz every frequency comes out about 9% higher. This port
  keeps that, so a song sounds the same as with the original.
- **In ReBuzz the original always processes.** ReBuzz calls native effects with
  read and write set, so its own idle check never runs. It feeds a mono effect
  `L + R` and copies the result to both sides.
- **Quiet tails are cut.** At the start of each Work call, a section whose
  four state values are all below 1 (about −90 dBFS) is cleared.
- The published source computes the LFO speed from *LFO mod* instead of *LFO
  rate*. The 0.1a binary doesn't have that bug: it uses LFO rate.

## Where this port differs

- **The LFO follows tempo and sample-rate changes.** The original computes the
  LFO speed only when LFO rate is written, so a tempo change left a tick-synced
  LFO running at the old speed. This port recomputes it when the tempo or sample
  rate changes. At a constant tempo nothing changes.
- **Thev factor** is a parameter, and **Channels** is new; both are appended
  after the original's parameters.
- **Idle shortcut.** With silent input and a fully decayed filter, Work only
  advances the LFO and inertia and returns false. The output is the same
  silence the original would produce.

## Verification

`tests/difftest.py` runs the original x86 DLL under Unicorn and the C# engine
(`tests/Harness`) on the same input, then compares them:

- the 15 coefficients of every filter type at 4,800 random settings: all
  bit-identical;
- 31 audio scenarios (about 1.6 million samples): the settings used on the bass
  in *amanita_aliena_26_072*, every filter type swept, all 17 LFO shapes,
  tick-synced rates, Set phase, Thev, filter-type changes, random chunk sizes,
  silence tails and 48 kHz. Every output sample is bit-identical.

The emulator uses the C library's sine and tangent, so on real hardware the
original may differ from this port in the last bit now and then. That is far
below anything audible.

To run it you need the original `FSM Philta.dll` (not included):

```
cd tests/Harness && dotnet build -c Release -o ../bin
cd .. && python3 -m pip install unicorn keystone-engine numpy
python3 difftest.py "<path>/FSM Philta.dll" bin
```

Cost: about 32 ns per sample on a laptop core (0.14% of one core at 44.1 kHz).
Work allocates nothing.

## Presets

`Pedal Bass Filterer.prs.xml` loads automatically when the machine is created:
Init, pedal_bass_001 (the bass settings from *amanita_aliena_26_072*), Static
Low-pass, Squelch Sweep, Slow Multipeak, Twin Peaks Wobble, Laser Steps, FMish
Vowel, Notch Drift and SnH Chatter. `gen_presets.py` generates the bundle; run it
with `--check` to check the parameter order and ranges against the source. It
isn't deployed.

## Build

```
dotnet build -c Release
```

The build copies `Pedal Bass Filterer.NET.dll` and the preset bundle to
`C:\Program Files\ReBuzz\Gear\Effects\`. Close ReBuzz first, because it locks
the DLL. If ReBuzz is installed somewhere else, use `-p:ReBuzzPath=<dir>`.
After deploying, check that the version under right-click → About... is the new
one.

## Credits and licence

Based on **FSM Philta** by Krzysztof Foltman (FSM Philta 0.1a, Copyright (C)
2001). The filter behaviour was recovered from that DLL. The later Philta source
published in the Buzztrax *buzzmachines* collection, Copyright (C) 2001–2007
Krzysztof Foltman, under the GNU Library General Public License version 2 or
later, was read for structure and names. No DLL or source of the original is
included here.

Pedal Bass Filterer is Copyright (C) 2026 thepedal, licensed under the GNU
General Public License version 3 or (at your option) any later version. See
`LICENSE`.
