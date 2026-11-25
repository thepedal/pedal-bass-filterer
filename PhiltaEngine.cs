// SPDX-License-Identifier: GPL-3.0-or-later
// Pedal Bass Filterer: DSP engine.
// Copyright (C) 2026 thepedal.
//
// Behaviour recreated from the FSM Philta 0.1a binary (FSM Philta.dll,
// Copyright (C) 2001 Krzysztof Foltman). The later Philta source in the
// Buzztrax buzzmachines collection (LGPL-2.0-or-later) was read for structure
// and names; the filter formulas here follow the 0.1a binary, which differs
// from that source (see README, "What the original does").
//
// Pure DSP: no ReBuzz types, so the test harness can build it on its own.

using System;

namespace Pedal.BassFilterer
{
    /// <summary>
    /// One Philta "track": an LFO, cutoff inertia and a cascade of three
    /// biquads whose coefficients are recomputed every 64 samples. Several
    /// state sets can share one control path (Stereo mode).
    /// </summary>
    public sealed class PhiltaEngine
    {
        public const int Block = 64;
        public const int FilterTypeCount = 8;
        public const int LfoShapeCount = 17;

        // The original hard-codes these. Keep them exactly.
        const double Pi = 3.141592653589793;     // .rdata
        const double TwoPiLfo = 6.2831852;       // LFO rate and phase use this rounded 2*pi
        const double OmegaScale = 2.0 / 44100.0; // coefficients assume 44.1 kHz at any rate
        const float CHalf = 7.123793329810724e-05f;
        const float CQuarter = 3.561896664905362e-05f;
        const float CThreeQ = 0.00010685689630918205f;
        const float CFull = 0.00014247586659621447f;

        static readonly int[] TickTable = { 1, 2, 3, 4, 6, 8, 12, 16, 24, 28, 32, 48, 64, 96, 128 };

        // ── control state (CTrack in the original) ───────────────────────
        float _cutoff = 120, _resonance = 120, _lfoDepth = 60, _inertia = 10f / 240f;
        int _filterType = 6, _lfoShape = 0, _lfoRate = 30;
        double _lfoPhase, _deltaPhase;
        float _curCutoff;
        float _thev;

        // rate cache: DeltaPhase follows sample rate and tempo
        int _rateSr = -1, _rateVal = -1;
        float _rateTps = float.NaN;

        // coefficients, 3 sections x {b0, b1, b2, a1, a2}
        readonly float[] _c = new float[15];

        // state sets, each 3 sections x {x1, x2, y1, y2}
        readonly float[][] _s;

        public PhiltaEngine(int stateSets = 2)
        {
            _s = new float[stateSets][];
            for (int i = 0; i < stateSets; i++) _s[i] = new float[12];
        }

        // ── parameters (TickTrack) ───────────────────────────────────────
        public void SetCutoff(int v) => _cutoff = v;
        public void SetResonance(int v) => _resonance = v;
        public void SetLfoDepth(int v) => _lfoDepth = v;
        public void SetLfoShape(int v) => _lfoShape = v;
        public void SetLfoRate(int v) { _lfoRate = v; _rateVal = -1; }
        public void SetInertia(int v) => _inertia = (float)(v * (1.0 / 240.0));
        public void SetLfoPhase(int v) => _lfoPhase = v * (TwoPiLfo / 128.0);
        /// <summary>Theviderness factor 0..20 (an attribute in the original).</summary>
        public void SetThev(int v) => _thev = v;

        public void SetFilterType(int v)
        {
            if (v != _filterType) ResetStates();
            _filterType = v;
        }

        public void ResetStates()
        {
            foreach (var s in _s) Array.Clear(s, 0, s.Length);
        }

        /// <summary>Copy state set 0 into set <paramref name="to"/> (Mono to Stereo switch).</summary>
        public void CopyStates(int to) => Array.Copy(_s[0], _s[to], 12);

        public int FilterType => _filterType;

        // ── rates ────────────────────────────────────────────────────────
        /// <summary>Recompute the LFO increment when rate, sample rate or tempo changed.</summary>
        public void UpdateTiming(int samplesPerSec, float ticksPerSec)
        {
            if (samplesPerSec <= 0) return;
            if (_rateVal == _lfoRate && _rateSr == samplesPerSec &&
                (_lfoRate < 240 || _rateTps == ticksPerSec)) return;
            _rateVal = _lfoRate; _rateSr = samplesPerSec; _rateTps = ticksPerSec;
            _deltaPhase = LfoDeltaPhase(_lfoRate, samplesPerSec, ticksPerSec);
        }

        public static double LfoDeltaPhase(int rate, int samplesPerSec, float ticksPerSec)
        {
            if (rate < 240)
                return Math.Pow(800.0, rate * (1.0 / 255.0)) * 0.05 * TwoPiLfo / samplesPerSec;
            int idx = Math.Min(rate - 240, TickTable.Length - 1);
            return ticksPerSec * TwoPiLfo / (double)(TickTable[idx] * samplesPerSec);
        }

        public static double LfoHz(int rate) => 0.05 * Math.Pow(800.0, rate / 255.0);
        public static int LfoTicks(int rate) => TickTable[Math.Min(Math.Max(rate - 240, 0), TickTable.Length - 1)];

        // ── processing ───────────────────────────────────────────────────
        /// <summary>
        /// Process <paramref name="n"/> samples. <paramref name="sets"/> state sets
        /// run in parallel on in[k]/out[k] with the same coefficients. Mirrors one
        /// call of the original WorkTrack (AvoidExceptions, then DoWork), so block
        /// boundaries restart at every call, as in the original.
        /// If <paramref name="inputSilent"/> is true (every input sample is zero) and,
        /// after AvoidExceptions, every state is zero, only the control path runs and
        /// the outputs are not written; the method returns false and the caller
        /// zeroes them. That is exact: the filter of silence with zero state is silence.
        /// </summary>
        public bool Process(float[][] input, float[][] output, int sets, int n, bool inputSilent)
        {
            for (int k = 0; k < sets; k++) AvoidExceptions(_s[k]);
            bool idle = inputSilent && StatesZero(sets);

            float thevFactor = (float)(_thev * 0.05);
            float ai = (float)(Math.Exp(-_inertia * 9.0) * 10.0);
            double aiD = ai;

            for (int i = 0; i < n; i += Block)
            {
                double lfo = Lfo();
                double dest = lfo * _lfoDepth * 0.5f + _cutoff;
                if (Math.Abs(_curCutoff - dest) < aiD)
                    _curCutoff = (float)dest;
                else
                    _curCutoff = (float)(Math.CopySign(aiD, dest - _curCutoff) + _curCutoff);

                int jmax = Math.Min(i + Block, n);
                if (!idle)
                {
                    CalcCoeffs(_filterType, _curCutoff, _resonance, thevFactor, _c);
                    for (int k = 0; k < sets; k++)
                        Run(_c, _s[k], input[k], output[k], i, jmax);
                }

                _lfoPhase += (jmax - i) * _deltaPhase;
                if (_lfoPhase > Pi * 1024.0) _lfoPhase -= Pi * 1024.0;
            }
            return !idle;
        }

        /// <summary>True when every state of the first <paramref name="sets"/> sets is exactly zero.</summary>
        public bool StatesZero(int sets)
        {
            for (int k = 0; k < sets; k++)
                foreach (float v in _s[k]) if (v != 0f) return false;
            return true;
        }

        static void AvoidExceptions(float[] s)
        {
            for (int b = 0; b < 12; b += 4)
            {
                if (Math.Abs(s[b]) < 1.0f && Math.Abs(s[b + 1]) < 1.0f &&
                    Math.Abs(s[b + 2]) < 1.0f && Math.Abs(s[b + 3]) < 1.0f)
                {
                    s[b] = 0f; s[b + 1] = 0f; s[b + 2] = 0f; s[b + 3] = 0f;
                }
            }
        }

        // The cascade, in the original's operation order. Section 1 output is
        // stored as float before section 2; section 2's output feeds section 3
        // unrounded; the result is doubled after rounding to float.
        static void Run(float[] c, float[] s, float[] x, float[] y, int from, int to)
        {
            float b0a = c[0], b1a = c[1], b2a = c[2], a1a = c[3], a2a = c[4];
            float b0b = c[5], b1b = c[6], b2b = c[7], a1b = c[8], a2b = c[9];
            float b0c = c[10], b1c = c[11], b2c = c[12], a1c = c[13], a2c = c[14];
            float x1a = s[0], x2a = s[1], y1a = s[2], y2a = s[3];
            float x1b = s[4], x2b = s[5], y1b = s[6], y2b = s[7];
            float x1c = s[8], x2c = s[9], y1c = s[10], y2c = s[11];

            for (int j = from; j < to; j++)
            {
                float inp = x[j];
                double o = (double)b1a * x1a + (double)inp * b0a + (double)b2a * x2a
                           - (double)a1a * y1a - (double)a2a * y2a;
                float oa = (float)o;
                x2a = x1a; x1a = inp; y2a = y1a; y1a = oa;

                o = (double)b1b * x1b + (double)oa * b0b + (double)b2b * x2b
                    - (double)y1b * a1b - (double)a2b * y2b;
                x2b = x1b; x1b = oa; y2b = y1b; y1b = (float)o;
                double ob = o;

                o = (double)b1c * x1c + ob * b0c + (double)b2c * x2c
                    - (double)y1c * a1c - (double)a2c * y2c;
                float oc = (float)o;
                x2c = x1c; x1c = (float)ob; y2c = y1c; y1c = oc;

                y[j] = oc + oc;
            }

            s[0] = x1a; s[1] = x2a; s[2] = y1a; s[3] = y2a;
            s[4] = x1b; s[5] = x2b; s[6] = y1b; s[7] = y2b;
            s[8] = x1c; s[9] = x2c; s[10] = y1c; s[11] = y2c;
        }

        // ── LFO ──────────────────────────────────────────────────────────
        double Lfo()
        {
            double lp = _lfoPhase;
            float phs = (float)(lp % (Pi + Pi));
            switch (_lfoShape)
            {
                case 0: return Math.Sin(phs);
                case 1: return ((phs - Pi) / Pi - 0.5) * 2.0;
                case 2: return ((phs - Pi) / Pi - 0.5) * -2.0;
                case 3: return phs < Pi ? 1.0 : -1.0;
                case 4: return ((phs < Pi ? phs / Pi : 2.0 - phs / Pi) - 0.5) * 2.0;
                case 5: return Math.Sin(Math.Sin(lp) * Pi * 0.25 + lp);
                case 6: return Math.Sin(Math.Sin(lp + lp) * Pi * (1.0 / 6.0) + lp);
                case 7: return Math.Sin(Math.Cos(lp * 3.0) * Pi + (lp + lp));
                case 8: return (Math.Sin(lp + lp) + Math.Cos(lp * 3.0)) * 0.5;
                case 9: return Math.Floor(Steps(phs)) * 0.25;
                case 10: return Math.Floor(Steps(phs)) * -0.25;
                case 11: return Math.Floor(Steps(phs)) * 0.125 + (phs % (Pi * 0.25)) * 0.5 / (Pi * 0.25);
                case 12: return (phs % (Pi * 0.25)) * 0.5 / (Pi * 0.25) - Math.Floor(Steps(phs)) * 0.125;
                case 13: return Math.Floor(Steps(phs)) * 0.125 + ((Pi + Pi - phs) % (Pi * 0.25)) * 0.5 / (Pi * 0.25);
                case 14: return ((Pi + Pi - phs) % (Pi * 0.25)) * 0.5 / (Pi * 0.25) - Math.Floor(Steps(phs)) * 0.125;
                case 15:
                    {
                        double f = Math.Floor(lp * 8.0 / Pi);
                        return Math.Sin(f * 19123.0 + Math.Sin(f * 12.0) * 40.0) * 0.5;
                    }
                case 16:
                    {
                        double s = Math.Sin(Math.Floor(lp * 16.0 / Pi) * 15.0) * 40.0;
                        return Math.Sin(Math.Floor(lp * 4.0 / Pi) * 1239543.0 + s) * 0.5;
                    }
                default: return 0.0;
            }
        }

        static double Steps(float phs) => ((phs - Pi) / Pi - 0.5) * 8.0;

        // ── coefficients (one routine per filter type) ───────────────────
        public static readonly string[] FilterNames =
        {
            "6L Multipeak", "6L Separated", "6L HiSquelch", "4L TwinPeaks",
            "2L FrontFlt", "2L LaserOne", "2L FMish", "Notchez",
        };

        public static readonly string[] ShapeNames =
        {
            "sine", "saw up", "saw down", "square", "triangle", "weird 1", "weird 2",
            "weird 3", "weird 4", "steps up", "steps down", "upsaws up", "upsaws down",
            "dnsaws up", "dnsaws down", "S'n'H 1", "S'n'H 2",
        };

        /// <summary>Nominal centre frequency in Hz for a cutoff value, as the given type maps it.</summary>
        public static float CutoffHz(int type, float cur) => type switch
        {
            0 => CutoffHz(cur, 16.0, 528.0, 20000f),
            1 => CutoffHz(cur, 40.0, 264.0, 16000f),
            2 => CutoffHz(cur, 96.0, 132.0, 20000f),
            6 => CutoffHz(cur, 40.0, 132.0, 20000f),
            7 => CutoffHz(240.0 - cur, 80.0, 132.0, 20000f),
            _ => CutoffHz(cur, 40.0, 264.0, 20000f),
        };

        static float CutoffHz(double cur, double b, double mult, float hi)
        {
            float cf = (float)(mult * Math.Pow(b, cur * (1.0 / 240.0)));
            if (!(cf < hi)) cf = hi;
            else if (cf < 33f) cf = 33f;
            return cf;
        }

        static double ScaleRes(float cf, float thev) => Math.Pow(cf * 5e-05, thev);

        public static void CalcCoeffs(int type, float cur, float res, float thev, float[] c)
        {
            switch (type)
            {
                case 0: // 6L Multipeak
                    {
                        float cf = CutoffHz(cur, 16.0, 528.0, 20000f);
                        double fq = ScaleRes(cf, thev) * res * 7.0 * (1.0 / 240.0) + 1.01;
                        double gain = 1.0 / Math.Sqrt(fq);
                        LpfGain(c, 0, cf * (double)(1f / 3f), fq + fq, gain);
                        Lpf(c, 5, cf * (double)(2f / 3f), fq);
                        Lpf(c, 10, cf, fq * (double)(2f / 3f));
                        break;
                    }
                case 1: // 6L Separated
                    {
                        float cf = CutoffHz(cur, 40.0, 264.0, 16000f);
                        float ir = 240f - res;
                        double fq = ScaleRes(cf, thev) * (ir * (double)0.6f * (1.0 / 240.0) + 2.5);
                        float sep = (float)(ir * (0.2 / 240.0) + 0.05);
                        double gain = (float)(1.0 / fq);
                        LpfSeparated(c, 0, cf, fq + fq, gain);
                        Lpf(c, 5, (1.0 - sep) * cf, fq + fq);
                        Lpf(c, 10, (sep + 1.0) * cf, fq + fq);
                        break;
                    }
                case 2: // 6L HiSquelch
                    {
                        float cf = CutoffHz(cur, 96.0, 132.0, 20000f);
                        float fq = (float)(ScaleRes(cf, thev) * res * 4.0 * (1.0 / 240.0) + 0.71);
                        double gain = 1.0 / Math.Pow(fq > 1.0 ? fq : 1.0, 1.5);
                        float twoq = fq + fq;
                        LpfGain(c, 0, cf, twoq, gain);
                        Lpf(c, 5, cf, twoq);
                        Lpf(c, 10, cf, twoq);
                        break;
                    }
                case 3: // 4L TwinPeaks
                    {
                        float cf = CutoffHz(cur, 40.0, 264.0, 20000f);
                        double fq = ScaleRes(cf, thev) * res * 4.0 * (1.0 / 240.0) + 0.71;
                        double gain = 1.0 / (fq > 1.0 ? fq : 1.0);
                        float twoq = (float)(fq + fq);
                        double x = fq - 0.7;
                        LpfGain(c, 0, cf, twoq, gain);
                        Lpf(c, 5, cf, twoq);
                        Peq(c, 10, cf * (double)CHalf, (float)(x * 3.0 + 1.0), x * 8.0 + 1.0);
                        break;
                    }
                case 4: // 2L FrontFlt
                    {
                        float cf = CutoffHz(cur, 40.0, 264.0, 20000f);
                        double fq = ScaleRes(cf, thev) * res * 6.0 * (1.0 / 240.0) + 0.71;
                        double gain = 1.0 / (fq > 1.0 ? fq : 1.0);
                        double q = (fq - 0.7) * 3.0 + 1.0;
                        LpfGain(c, 0, cf, fq * 4.0, gain);
                        PeqFront(c, 5, cf * (double)CHalf, q);
                        Peq(c, 10, cf * (double)CQuarter, (float)q, q);
                        break;
                    }
                case 5: // 2L LaserOne
                    {
                        float cf = CutoffHz(cur, 40.0, 264.0, 20000f);
                        double fq = ScaleRes(cf, thev) * res * 6.0 * (1.0 / 240.0) + 0.71;
                        double gain = 1.0 / (fq > 1.0 ? fq : 1.0);
                        double x = fq - 0.7;
                        float q = (float)(x * 2.0 + 1.0);
                        double v = x * 3.0 + 1.0;
                        LpfGain(c, 0, cf, fq * 4.0, gain);
                        Peq(c, 5, cf * (double)CThreeQ, q, v);
                        Peq(c, 10, cf * (double)CHalf, q, v);
                        break;
                    }
                case 6: // 2L FMish
                    {
                        float cf = CutoffHz(cur, 40.0, 132.0, 20000f);
                        float fq = (float)(ScaleRes(cf, thev) * 720.0 * (1.0 / 240.0) + 0.71);
                        double e = (1.0 - res * (1.0 / 240.0)) + 0.5;
                        float sc1 = (float)Math.Pow(Math.Min(0.89, cur * 0.0008333333333333334 + 0.33), e);
                        double sc2 = Math.Pow(Math.Min(0.9, cur * 0.0004166666666666667 + 0.14), e);
                        double gain = 1.0 / (fq > 1.0 ? fq : 1.0);
                        double x = fq - 0.7;
                        float q = (float)(x * 2.0 + 1.0);
                        double v = x * 3.0 + 1.0;
                        LpfGain(c, 0, cf, fq * 4.0, gain);
                        Peq(c, 5, sc1 * (double)cf * CFull, q, v);
                        Peq(c, 10, sc2 * cf * CFull, q, v);
                        break;
                    }
                default: // 7: Notchez
                    {
                        float cf = CutoffHz(240.0 - cur, 80.0, 132.0, 20000f);
                        float q = (float)(res * (double)0.002500000176951289f + (double)0.1f); // the binary's float, not 0.0025f
                        double spacing = Math.Pow(1.3f + (240.0 - res) * 3.0 * (1.0 / 240.0), 1.0 - cf * (double)5e-05f);
                        float twoq = q + q;
                        double hi = spacing * cf;
                        if (21000.0 < hi) hi = 21000.0;
                        Brf(c, 0, cf, twoq);
                        Brf(c, 5, cf / spacing, twoq);
                        Brf(c, 10, hi, twoq);
                        break;
                    }
            }
        }

        // rbjLPF with a gain: the original stores alpha and inv as floats here.
        static void LpfGain(float[] c, int o, double fc, double twoQ, double gain)
        {
            double w = fc * Pi * OmegaScale;
            float cs = (float)Math.Cos(w);
            double alpha = Math.Sin(w) / twoQ;
            float af = (float)alpha;
            float inv = (float)(1.0 / (1.0 + alpha));
            double t = (1.0 - cs) * inv * gain;
            float b0 = (float)(t * 0.5);
            c[o] = b0; c[o + 1] = (float)t; c[o + 2] = b0;
            c[o + 3] = (float)(inv * (double)cs * -2.0);
            c[o + 4] = (float)((1.0 - af) * inv);
        }

        // rbjLPF without gain: alpha and inv stay unrounded.
        static void Lpf(float[] c, int o, double fc, double twoQ)
        {
            double w = fc * Pi * OmegaScale;
            float cs = (float)Math.Cos(w);
            double alpha = Math.Sin(w) / twoQ;
            double inv = 1.0 / (1.0 + alpha);
            double t = (1.0 - cs) * inv;
            float b0 = (float)(t * 0.5);
            c[o] = b0; c[o + 1] = (float)t; c[o + 2] = b0;
            c[o + 3] = (float)(inv * cs * -2.0);
            c[o + 4] = (float)((1.0 - alpha) * inv);
        }

        static void Brf(float[] c, int o, double fc, float twoQ)
        {
            double w = fc * Pi * OmegaScale;
            float cs = (float)Math.Cos(w);
            double alpha = Math.Sin(w) / twoQ;
            double inv = 1.0 / (1.0 + alpha);
            float b1 = (float)(inv * cs * -2.0);
            c[o] = (float)inv; c[o + 1] = b1; c[o + 2] = (float)inv;
            c[o + 3] = b1; c[o + 4] = (float)((1.0 - alpha) * inv);
        }

        // rbjLPF as in 6L Separated's first section: sin() of the float-rounded
        // omega, cos() of the unrounded one, alpha and inv unrounded, float gain.
        static void LpfSeparated(float[] c, int o, double fc, double twoQ, double gain)
        {
            double w = fc * Pi * OmegaScale;
            float wf = (float)w;
            float cs = (float)Math.Cos(w);
            double alpha = Math.Sin(wf) / twoQ;
            double inv = 1.0 / (1.0 + alpha);
            double t = (1.0 - cs) * inv * gain;
            float b0 = (float)(t * 0.5);
            c[o] = b0; c[o + 1] = (float)t; c[o + 2] = b0;
            c[o + 3] = (float)(inv * cs * -2.0);
            c[o + 4] = (float)((1.0 - alpha) * inv);
        }

        // Zoelzer parametric EQ, as most sections compute it. arg = fc*pi/44100.
        static void Peq(float[] c, int o, double arg, double q, double v)
        {
            double kr = Math.Tan(arg * 0.5);
            float k = (float)kr;
            float kk = (float)(kr * k);
            double vkdq = v * k / q;
            double kdq = k / q;
            float kdqF = (float)kdq;
            float oda0 = (float)(1.0 / (kdq + kk + 1.0));
            float b1 = (float)((kk - 1.0) * 2.0 * oda0);
            c[o] = (float)((vkdq + kk + 1.0) * oda0);
            c[o + 1] = b1;
            c[o + 2] = (float)((1.0 - vkdq + kk) * oda0);
            c[o + 3] = b1;
            c[o + 4] = (float)((1.0 - kdqF + kk) * oda0);
        }

        // 2L FrontFlt's first EQ (q and v are the same value): tan and 1/a0 stay
        // unrounded, v*k/q is rounded to float.
        static void PeqFront(float[] c, int o, double arg, double q)
        {
            double k = Math.Tan(arg * 0.5);
            float kk = (float)(k * k);
            float vkdq = (float)(q * k / q);
            double kdq = k / q;
            double oda0 = 1.0 / (kdq + kk + 1.0);
            double b1 = (kk - 1.0) * 2.0 * oda0;
            c[o] = (float)(((double)vkdq + kk + 1.0) * oda0);
            c[o + 1] = (float)b1;
            c[o + 2] = (float)((1.0 - vkdq + kk) * oda0);
            c[o + 3] = (float)b1;
            c[o + 4] = (float)((1.0 - kdq + kk) * oda0);
        }
    }
}
