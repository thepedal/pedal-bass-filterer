#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 thepedal.
"""Differential test: Pedal Bass Filterer's engine against the original FSM Philta DLL.

The original x86 DLL runs under Unicorn (emu_philta.py); the C# engine runs in
tests/Harness on the same scenario file. Needs: python3 -m pip install unicorn keystone-engine numpy

    python3 difftest.py <path to FSM Philta.dll> <path to Harness binary dir>

The DLL is not included in this repository.
"""
import math, os, random, struct, subprocess, sys, tempfile
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from emu_philta import Philta

NONE = dict(cutoff=255, resonance=255, filtype=255, lforate=255, lfodepth=255,
            lfoshape=255, inertia=255, lfophase=255)
ORDER = ['cutoff', 'resonance', 'filtype', 'lforate', 'lfodepth', 'lfoshape', 'inertia', 'lfophase']


class Scenario:
    def __init__(self, name, sr=44100, bpm=126, tpb=4):
        self.name, self.sr, self.bpm, self.tpb = name, sr, bpm, tpb
        self.ops = []

    def tick(self, **kw):
        v = dict(NONE); v.update(kw)
        self.ops.append(('tick', v))

    def thev(self, t):
        self.ops.append(('thev', t))

    def work(self, x):
        self.ops.append(('work', np.asarray(x, dtype=np.float32)))

    def encode(self):
        spt = int((60 * self.sr) / (self.bpm * self.tpb))
        tps = float(np.float32(float(self.sr) / float(spt)))
        b = bytearray(b'\x01' + struct.pack('<if', self.sr, tps))
        for op, v in self.ops:
            if op == 'tick':
                b += b'\x02' + bytes(v[k] for k in ORDER)
            elif op == 'thev':
                b += b'\x03' + struct.pack('<i', v)
            else:
                b += b'\x04' + struct.pack('<i', len(v)) + v.tobytes()
        return bytes(b)

    def run_native(self, dll):
        p = Philta(dll, self.sr, self.bpm, self.tpb)
        out = []
        for op, v in self.ops:
            if op == 'tick':
                p.tick(**v)
            elif op == 'thev':
                p.set_thev(v)
            else:
                _, y = p.work(list(map(float, v)))
                out.extend(y)
        return np.asarray(out, dtype=np.float32)


def bass(n, sr, start, rnd, amp=16000.0, notes=(33, 36, 40, 43, 28, 31)):
    """A stepping saw bass line plus a little noise (Buzz +-32768 scale)."""
    t = np.arange(start, start + n)
    note = np.array(notes)[(t // int(sr * 0.25)) % len(notes)]
    f = 440.0 * 2 ** ((note - 69) / 12.0)
    ph = np.cumsum(f / sr) + rnd.random()
    saw = 2 * (ph % 1.0) - 1
    return (saw * amp + np.array([rnd.gauss(0, 200) for _ in range(n)])).astype(np.float32)


def play(sc, seconds, rnd, ticks=None, chunk=256, random_chunks=False, signal=None, silence_from=None):
    """ReBuzz-style: chunks of <= 256 samples, split at tick boundaries; ticks[k] = params at tick k."""
    sr = sc.sr
    spt = int((60 * sr) / (sc.bpm * sc.tpb))
    total = int(seconds * sr)
    pos = 0; tick = 0
    while pos < total:
        if pos % spt == 0 or tick == 0:
            if ticks and tick in ticks:
                sc.tick(**ticks[tick])
            tick += 1
        left_in_tick = spt - (pos % spt)
        n = min(rnd.randint(1, chunk) if random_chunks else chunk, left_in_tick, total - pos)
        x = signal(n, pos) if signal else bass(n, sr, pos, rnd)
        if silence_from is not None and pos >= silence_from * sr:
            x = np.zeros(n, np.float32)
        sc.work(x)
        pos += n
        if pos % spt == 0:
            pass


def scenarios():
    rnd = random.Random(7)
    out = []

    s = Scenario('song: Philta_Bass settings (HiSquelch, 16-tick sine)')
    s.tick(cutoff=28, resonance=0, filtype=2, lforate=247, lfodepth=80, lfoshape=0, inertia=0, lfophase=0)
    play(s, 8.0, rnd); out.append(s)

    for ft in range(8):
        s = Scenario('type %d sweeps (res, cutoff, inertia)' % ft)
        s.tick(cutoff=60, resonance=0, filtype=ft, lforate=30, lfodepth=0, lfoshape=0, inertia=10, lfophase=0)
        ticks = {}
        for k in range(1, 40):
            ticks[k] = dict(cutoff=rnd.randint(0, 240), resonance=rnd.randint(0, 240))
            if k % 9 == 0: ticks[k]['inertia'] = rnd.randint(0, 60)
        play(s, 1.5, rnd, ticks=ticks, random_chunks=(ft % 2 == 1)); out.append(s)

    for sh in range(17):
        s = Scenario('LFO shape %d' % sh)
        s.tick(cutoff=120, resonance=80, filtype=sh % 8, lforate=200, lfodepth=240, lfoshape=sh, inertia=0, lfophase=0)
        play(s, 0.6, rnd); out.append(s)

    s = Scenario('tick-synced rates, set phase, thev')
    s.thev(13)
    s.tick(cutoff=100, resonance=200, filtype=0, lforate=240, lfodepth=200, lfoshape=4, inertia=5, lfophase=0)
    ticks = {k: dict(lforate=240 + (k % 15)) for k in range(2, 30, 3)}
    ticks.update({k: dict(lfophase=rnd.randint(0, 127)) for k in range(4, 30, 5)})
    play(s, 1.2, rnd, ticks=ticks); out.append(s)

    s = Scenario('filter type changes mid-stream (state reset), random chunks')
    s.tick(cutoff=150, resonance=180, filtype=3, lforate=100, lfodepth=60, lfoshape=1, inertia=20, lfophase=10)
    ticks = {k: dict(filtype=rnd.randint(0, 7)) for k in range(3, 40, 4)}
    play(s, 1.5, rnd, ticks=ticks, random_chunks=True); out.append(s)

    s = Scenario('silence tail (AvoidExceptions), resonant')
    s.tick(cutoff=90, resonance=240, filtype=0, lforate=10, lfodepth=30, lfoshape=0, inertia=0, lfophase=0)
    play(s, 2.0, rnd, silence_from=0.5); out.append(s)

    s = Scenario('48 kHz, 140 BPM, impulses')
    s.sr, s.bpm = 48000, 140
    s.tick(cutoff=170, resonance=150, filtype=5, lforate=243, lfodepth=120, lfoshape=9, inertia=0, lfophase=0)
    imp = lambda n, pos: np.array([30000.0 if (pos + i) % 4800 == 0 else 0.0 for i in range(n)], np.float32)
    play(s, 1.0, rnd, signal=imp); out.append(s)

    s = Scenario('defaults (FMish) on full-scale bass')
    s.tick(**{k: v for k, v in dict(cutoff=120, resonance=120, filtype=6, lforate=30, lfodepth=60,
                                     lfoshape=0, inertia=10, lfophase=0).items()})
    play(s, 1.0, rnd, signal=lambda n, pos: bass(n, 44100, pos, rnd, amp=32000.0)); out.append(s)
    return out


FUNCS = [0x10001030, 0x10001240, 0x10001470, 0x100016a0,
         0x10001900, 0x10001b70, 0x10001e00, 0x10002140]   # CalcCoeffs per filter type (0.1a)


def coeff_check(dll, harness, count=600):
    """Bit-compare every filter type's 15 coefficients on random points."""
    p = Philta(dll)
    T = p.obj + 0x28                      # Tracks[0]
    f32 = lambda x: float(np.float32(x))
    rnd = random.Random(3)
    pts = [(t, f32(rnd.uniform(-90, 330)), f32(rnd.randint(0, 240)), f32(rnd.choice([0, 0.05, 0.25, 1.0])))
           for t in range(8) for _ in range(count)]
    inp = ''.join('%d %r %r %r\n' % q for q in pts)
    out = subprocess.run([harness, '--coeffs'], input=inp, capture_output=True, text=True, check=True).stdout.split()
    bad = 0
    for i, (t, cur, res, thev) in enumerate(pts):
        p.uc.mem_write(T + 0x24, struct.pack('<f', cur))
        p.uc.mem_write(T + 0x04, struct.pack('<f', res))
        p.uc.mem_write(T + 0x9c, struct.pack('<f', thev))
        p._call(FUNCS[t], [], this=T)
        nat = []
        for b in range(3):
            a1, a2, b0, b1, b2 = struct.unpack('<5I', p.uc.mem_read(T + 0x28 + 0x24 * b, 20))
            nat += [b0, b1, b2, a1, a2]
        mine = [int(h, 16) for h in out[15 * i:15 * i + 15]]
        bad += sum(1 for a, b in zip(nat, mine) if a != b)
    print('coefficients: %d points, %d differing values' % (len(pts), bad))
    return bad == 0


def main():
    dll, harness_dir = sys.argv[1], sys.argv[2]
    harness = os.path.join(harness_dir, 'Harness')
    worst = -999.0
    ok = coeff_check(dll, harness)
    for sc in scenarios():
        with tempfile.TemporaryDirectory() as td:
            fin, fout = os.path.join(td, 's.bin'), os.path.join(td, 'o.bin')
            open(fin, 'wb').write(sc.encode())
            subprocess.run([harness, fin, fout], check=True)
            mine = np.fromfile(fout, dtype=np.float32)
        nat = sc.run_native(dll)
        assert len(mine) == len(nat), (len(mine), len(nat))
        peak = max(float(np.max(np.abs(nat))), 1.0)
        err = float(np.max(np.abs(mine.astype(np.float64) - nat)))
        rel = 20 * math.log10(max(err, 1e-30) / peak)
        bit = int(np.sum(mine != nat))
        worst = max(worst, rel)
        verdict = 'ok' if rel < -100 else 'FAIL'
        ok &= verdict == 'ok'
        print('%-60s n=%7d peak=%8.0f maxdiff=%9.3g (%7.1f dB re peak)  differing samples %d  %s'
              % (sc.name, len(nat), peak, err, rel, bit, verdict))
    print('worst %.1f dB' % worst, 'PASS' if ok else 'FAIL')
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
