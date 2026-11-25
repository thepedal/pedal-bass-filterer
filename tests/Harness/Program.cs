// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 thepedal.
// Runs PhiltaEngine (one mono state set) over a scenario file written by
// tests/difftest.py and writes the output samples as float32.
//
// Scenario ops (little endian):
//   1 master: int sr, float tps
//   2 tick:   8 bytes (cutoff, res, filter, lforate, lfomod, shape, inertia, phase; 255 = none)
//   3 thev:   int
//   4 work:   int n, n x float32 input
using System;
using System.IO;
using Pedal.BassFilterer;

if (args[0] == "--coeffs")
{
    // stdin lines: type cur res thev (floats); stdout: 15 coefficients as hex bits
    var c = new float[15];
    string? line;
    while ((line = Console.ReadLine()) != null)
    {
        var p = line.Split(' ');
        PhiltaEngine.CalcCoeffs(int.Parse(p[0]), float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture), c);
        Console.WriteLine(string.Join(" ", Array.ConvertAll(c, f => BitConverter.SingleToInt32Bits(f).ToString("x8"))));
    }
    return;
}
var r = new BinaryReader(File.OpenRead(args[0]));
var w = new BinaryWriter(File.Create(args[1]));
var e = new PhiltaEngine(1);
int sr = 44100; float tps = 8.4f;
var inb = new float[1][]; var outb = new float[1][];
while (r.BaseStream.Position < r.BaseStream.Length)
{
    int op = r.ReadByte();
    switch (op)
    {
        case 1: sr = r.ReadInt32(); tps = r.ReadSingle(); break;
        case 2:
            {
                var v = r.ReadBytes(8);
                // native TickTrack order
                if (v[3] != 255) e.SetLfoRate(v[3]);
                if (v[7] != 255) e.SetLfoPhase(v[7]);
                if (v[4] != 255) e.SetLfoDepth(v[4]);
                if (v[6] != 255) e.SetInertia(v[6]);
                if (v[0] != 255) e.SetCutoff(v[0]);
                if (v[1] != 255) e.SetResonance(v[1]);
                if (v[5] != 255) e.SetLfoShape(v[5]);
                if (v[2] != 255) e.SetFilterType(v[2]);
                break;
            }
        case 3: e.SetThev(r.ReadInt32()); break;
        case 4:
            {
                int n = r.ReadInt32();
                var x = new float[n]; var y = new float[n];
                bool silent = true;
                for (int i = 0; i < n; i++) { x[i] = r.ReadSingle(); if (x[i] != 0f) silent = false; }
                inb[0] = x; outb[0] = y;
                e.UpdateTiming(sr, tps);
                if (!e.Process(inb, outb, 1, n, silent)) Array.Clear(y);
                foreach (var f in y) w.Write(f);
                break;
            }
        default: throw new InvalidDataException("op " + op);
    }
}
w.Flush();
