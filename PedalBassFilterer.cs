// SPDX-License-Identifier: GPL-3.0-or-later
// Pedal Bass Filterer — a managed ReBuzz emulation of the FSM Philta filter.
// Copyright (C) 2026 thepedal.
//
// Based on FSM Philta 0.1a by Krzysztof Foltman (Copyright (C) 2001). The
// filter behaviour was recovered from the original DLL and checked sample for
// sample against it (tests/difftest.py). See README for credits and licences.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version. See LICENSE.

using System;
using System.Collections.Generic;
using System.Windows;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace Pedal.BassFilterer
{
    [MachineDecl(Name = "Pedal Bass Filterer", ShortName = "Filterer", Author = "thepedal")]
    public class PedalBassFiltererMachine : IBuzzMachine
    {
        internal const string Version = "1.0.0";

        readonly IBuzzMachineHost _host;
        readonly PhiltaEngine _engine = new PhiltaEngine(2);

        // Work buffers (ReBuzz chunks are at most 256 samples; grown if ever larger).
        float[] _in0 = new float[256], _in1 = new float[256];
        float[] _out0 = new float[256], _out1 = new float[256];
        readonly float[][] _inSets = new float[2][], _outSets = new float[2][];

        // Parameter writes are collected here and applied at the start of Work,
        // in the original's TickTrack order.
        [Flags]
        enum P
        {
            None = 0, Cutoff = 1, Resonance = 2, Filter = 4, LfoRate = 8, LfoMod = 16,
            Shape = 32, Inertia = 64, Phase = 128, Thev = 256, Channels = 512,
        }
        P _pending;
        int _cutoff = 120, _resonance = 120, _filter = 6, _lfoRate = 30, _lfoMod = 60,
            _shape = 0, _inertia = 10, _phase = 0, _thev = 0, _channels = 0;
        int _activeChannels = 0;

        // Last host rate seen in Work, for the read-outs (MasterInfo is only valid in Work).
        volatile int _sampleRate = 44100;
        volatile float _ticksPerSec = 8.4f;

        public PedalBassFiltererMachine(IBuzzMachineHost host)
        {
            _host = host;
        }

        // ── Parameters ─────────────────────────────────────────────────────
        // Names, order, ranges and defaults are the original's track parameters,
        // so presets and pattern columns line up with FSM Philta. Append new
        // parameters at the end (Build §3.3).

        [ParameterDecl(Name = "Cutoff", MinValue = 0, MaxValue = 240, DefValue = 120,
            Description = "Cutoff frequency. The read-out is the centre frequency for the current filter type, before the LFO.")]
        public int Cutoff { get => _cutoff; set { _cutoff = value; _pending |= P.Cutoff; } }

        [ParameterDecl(Name = "Resonance", MinValue = 0, MaxValue = 240, DefValue = 120,
            Description = "Resonance. Each filter type uses it differently (for 2L FMish it moves the peaks).")]
        public int Resonance { get => _resonance; set { _resonance = value; _pending |= P.Resonance; } }

        [ParameterDecl(Name = "Filter", DefValue = 6, Description = "Filter type",
            ValueDescriptions = new[] { "6L Multipeak", "6L Separated", "6L HiSquelch", "4L TwinPeaks",
                                        "2L FrontFlt", "2L LaserOne", "2L FMish", "Notchez" })]
        public int Filter { get => _filter; set { _filter = value; _pending |= P.Filter; } }

        [ParameterDecl(Name = "LFO rate", MinValue = 0, MaxValue = 254, DefValue = 30,
            Description = "LFO rate. 0 to 239 is 0.05 to 26 Hz; 240 to 254 is one cycle in ticks (1 to 128).")]
        public int LfoRate { get => _lfoRate; set { _lfoRate = value; _pending |= P.LfoRate; } }

        [ParameterDecl(Name = "LFO mod", MinValue = 0, MaxValue = 240, DefValue = 60,
            Description = "LFO modulation depth, in cutoff steps (peak to peak).")]
        public int LfoMod { get => _lfoMod; set { _lfoMod = value; _pending |= P.LfoMod; } }

        [ParameterDecl(Name = "LFO Shape", DefValue = 0, Description = "LFO shape",
            ValueDescriptions = new[] { "sine", "saw up", "saw down", "square", "triangle", "weird 1", "weird 2",
                                        "weird 3", "weird 4", "steps up", "steps down", "upsaws up", "upsaws down",
                                        "dnsaws up", "dnsaws down", "S'n'H 1", "S'n'H 2" })]
        public int LfoShape { get => _shape; set { _shape = value; _pending |= P.Shape; } }

        [ParameterDecl(Name = "Inertia", MinValue = 0, MaxValue = 60, DefValue = 10,
            Description = "Cutoff frequency inertia: how slowly the cutoff follows its target. 0 is fastest.")]
        public int Inertia { get => _inertia; set { _inertia = value; _pending |= P.Inertia; } }

        [ParameterDecl(Name = "Set phase", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Set the LFO phase (0 to 127 is one cycle). Writing it restarts the LFO there.")]
        public int SetPhase { get => _phase; set { _phase = value; _pending |= P.Phase; } }

        // ── Added in this port (appended after the original's parameters) ────

        [ParameterDecl(Name = "Thev factor", MinValue = 0, MaxValue = 20, DefValue = 0,
            Description = "Theviderness factor (an attribute in the original): reduces resonance at low cutoff. 0 is off, 20 is the most.")]
        public int ThevFactor { get => _thev; set { _thev = value; _pending |= P.Thev; } }

        [ParameterDecl(Name = "Channels", DefValue = 0,
            Description = "Mono sums left and right and filters once, as the original does in ReBuzz. Stereo filters each side at the same level.",
            ValueDescriptions = new[] { "Mono", "Stereo" })]
        public int Channels { get => _channels; set { _channels = value; _pending |= P.Channels; } }

        void ApplyPending()
        {
            P p = _pending;
            if (p == P.None) return;
            _pending = P.None;
            // TickTrack order: rate, phase, depth, inertia, cutoff, resonance, shape, type.
            if ((p & P.LfoRate) != 0) _engine.SetLfoRate(_lfoRate);
            if ((p & P.Phase) != 0) _engine.SetLfoPhase(_phase);
            if ((p & P.LfoMod) != 0) _engine.SetLfoDepth(_lfoMod);
            if ((p & P.Inertia) != 0) _engine.SetInertia(_inertia);
            if ((p & P.Cutoff) != 0) _engine.SetCutoff(_cutoff);
            if ((p & P.Resonance) != 0) _engine.SetResonance(_resonance);
            if ((p & P.Shape) != 0) _engine.SetLfoShape(_shape);
            if ((p & P.Filter) != 0) _engine.SetFilterType(Math.Clamp(_filter, 0, PhiltaEngine.FilterTypeCount - 1));
            if ((p & P.Thev) != 0) _engine.SetThev(_thev);
            if ((p & P.Channels) != 0)
            {
                int ch = _channels == 1 ? 1 : 0;
                if (ch == 1 && _activeChannels == 0) _engine.CopyStates(1);   // continue without a click
                _activeChannels = ch;
            }
        }

        // ── Audio ──────────────────────────────────────────────────────────

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            var mi = _host?.MasterInfo;
            if (mi != null && mi.SamplesPerSec > 0)
            {
                _sampleRate = mi.SamplesPerSec;
                _ticksPerSec = mi.TicksPerSec;
            }

            ApplyPending();
            _engine.UpdateTiming(_sampleRate, _ticksPerSec);

            if (_in0.Length < n)
            {
                _in0 = new float[n]; _in1 = new float[n];
                _out0 = new float[n]; _out1 = new float[n];
            }

            // ReBuzz always calls effects with WM_READWRITE, so silence is judged from the buffer.
            bool silent = true;
            int sets;
            if (_activeChannels == 0)
            {
                // What ReBuzz feeds a native mono effect: L + R. The output goes to both sides.
                for (int i = 0; i < n; i++)
                {
                    float l = input[i].L, r = input[i].R;
                    if (l != 0f || r != 0f) silent = false;
                    _in0[i] = l + r;
                }
                sets = 1;
            }
            else
            {
                // Each side doubled, so a centred signal comes out exactly as in Mono.
                for (int i = 0; i < n; i++)
                {
                    float l = input[i].L, r = input[i].R;
                    if (l != 0f || r != 0f) silent = false;
                    _in0[i] = l + l;
                    _in1[i] = r + r;
                }
                sets = 2;
            }

            _inSets[0] = _in0; _inSets[1] = _in1;
            _outSets[0] = _out0; _outSets[1] = _out1;

            if (!_engine.Process(_inSets, _outSets, sets, n, silent))
            {
                // Silent in, silent state: nothing to write. A false return makes the
                // host forward the input, which is silence here (PedalMasterN §1).
                return false;
            }

            if (sets == 1)
                for (int i = 0; i < n; i++) { output[i].L = _out0[i]; output[i].R = _out0[i]; }
            else
                for (int i = 0; i < n; i++) { output[i].L = _out0[i]; output[i].R = _out1[i]; }
            return true;
        }

        // ── Display ────────────────────────────────────────────────────────

        public string? DescribeValue(IParameter parameter, int value)
        {
            switch (parameter?.Name)
            {
                case "Cutoff":
                    {
                        float hz = PhiltaEngine.CutoffHz(Math.Clamp(_filter, 0, 7), value);
                        return hz >= 1000f ? (hz / 1000f).ToString("0.00") + " kHz" : hz.ToString("0") + " Hz";
                    }
                case "LFO rate":
                    if (value < 240) return PhiltaEngine.LfoHz(value).ToString("0.000") + " Hz";
                    {
                        int t = PhiltaEngine.LfoTicks(value);
                        return t == 1 ? "1 tick" : t + " ticks";
                    }
                case "Set phase":
                    return (value * 360 / 128) + " deg";
                case "Thev factor":
                    return value == 0 ? "off" : (value / 20.0).ToString("0.00");
                default:
                    return null;
            }
        }

        // ── Right-click menu ───────────────────────────────────────────────

        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                yield return new MenuItemVM()
                {
                    Text = "About...",
                    Command = new SimpleCommand()
                    {
                        CanExecuteDelegate = p => true,
                        ExecuteDelegate = p => MessageBox.Show(
                            $"Pedal Bass Filterer   v{Version}\n\n" +
                            "A managed emulation of FSM Philta 0.1a: an LFO-swept\n" +
                            "6-pole filter with eight filter types.\n\n" +
                            "Based on FSM Philta by Krzysztof Foltman (2001).\n" +
                            "Thank you, Krzysztof.\n\n" +
                            "GPL-3.0-or-later",
                            "About Pedal Bass Filterer")
                    }
                };
            }
        }
    }
}
