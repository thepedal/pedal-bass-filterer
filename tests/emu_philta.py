#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 thepedal.
"""Run the original FSM Philta x86 DLL headless under Unicorn (method: Genre spec §8).

Used only as a reference for the differential test of Pedal Bass Filterer.
Not part of the machine. The DLL path is passed in; nothing from it is redistributed.
"""
import struct
from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
from unicorn.x86_const import *
from keystone import Ks, KS_ARCH_X86, KS_MODE_32

PAGE = 0x1000
STUB_BASE = 0x20000000
HEAP_BASE = 0x30000000
STACK_TOP = 0x40000000
DATA_BASE = 0x50000000   # master info, callbacks, buffers
SENTINEL = 0x60000000


def align(x, a=PAGE):
    return (x + a - 1) & ~(a - 1)


class Philta:
    def __init__(self, dll_path, sample_rate=44100, bpm=126, tpb=4):
        self.d = open(dll_path, 'rb').read()
        self.uc = Uc(UC_ARCH_X86, UC_MODE_32)
        self.ks = Ks(KS_ARCH_X86, KS_MODE_32)
        self._load_pe()
        self._map_runtime()
        self._patch_imports()
        self.set_master(sample_rate, bpm, tpb)
        self.obj = self._call(self.exports['CreateMachine'], [], this=None)
        self.uc.mem_write(self.obj + 0x10, struct.pack('<I', self.master))
        self.uc.mem_write(self.obj + 0x14, struct.pack('<I', self.cb))
        self.vt = self.u32(self.obj)
        self.trackvals = self.u32(self.obj + 8)
        self.attrvals = self.u32(self.obj + 0xC)
        self._vcall(1, [0])          # Init(NULL)

    # ── memory helpers ────────────────────────────────────────────────
    def u32(self, a):
        return struct.unpack('<I', self.uc.mem_read(a, 4))[0]

    def f32(self, a, n=1):
        v = struct.unpack('<%df' % n, self.uc.mem_read(a, 4 * n))
        return v if n > 1 else v[0]

    def f64(self, a):
        return struct.unpack('<d', self.uc.mem_read(a, 8))[0]

    # ── loading ───────────────────────────────────────────────────────
    def _load_pe(self):
        d = self.d
        pe = struct.unpack_from('<I', d, 0x3c)[0]
        opt = pe + 24
        self.base = struct.unpack_from('<I', d, opt + 28)[0]
        size = struct.unpack_from('<I', d, opt + 56)[0]
        nsec = struct.unpack_from('<H', d, pe + 6)[0]
        optsz = struct.unpack_from('<H', d, pe + 20)[0]
        self.uc.mem_map(self.base, align(size))
        self.uc.mem_write(self.base, d[:0x1000])
        self.secs = []
        for i in range(nsec):
            o = opt + optsz + 40 * i
            vs, va, rs, ra = struct.unpack_from('<IIII', d, o + 8)
            self.secs.append((va, vs, ra, rs))
            self.uc.mem_write(self.base + va, d[ra:ra + min(rs, max(vs, rs))])
        self.opt = opt
        # exports
        def r2o(rva):
            for va, vs, ra, rs in self.secs:
                if va <= rva < va + max(vs, rs):
                    return ra + rva - va
        self.r2o = r2o
        eo = r2o(struct.unpack_from('<I', d, opt + 96)[0])
        nfun, nnames, af, an, ao = struct.unpack_from('<IIIII', d, eo + 20)
        self.exports = {}
        for k in range(nnames):
            nm = d[r2o(struct.unpack_from('<I', d, r2o(an) + 4 * k)[0]):].split(b'\0')[0].decode()
            ordi = struct.unpack_from('<H', d, r2o(ao) + 2 * k)[0]
            self.exports[nm] = self.base + struct.unpack_from('<I', d, r2o(af) + 4 * ordi)[0]

    def _asm(self, src, addr):
        enc, _ = self.ks.asm(src, addr)
        return bytes(enc)

    def _map_runtime(self):
        uc = self.uc
        uc.mem_map(STUB_BASE, 0x10000)
        uc.mem_map(HEAP_BASE, 0x100000)
        uc.mem_map(STACK_TOP - 0x100000, 0x100000)
        uc.mem_map(DATA_BASE, 0x100000)
        uc.mem_map(SENTINEL, PAGE)
        uc.mem_write(SENTINEL, b'\xf4')          # hlt
        self.heap_ptr_addr = DATA_BASE + 0x0
        uc.mem_write(self.heap_ptr_addr, struct.pack('<I', HEAP_BASE))
        self.zero_dword = DATA_BASE + 0x10
        self.master = DATA_BASE + 0x100
        self.cb = DATA_BASE + 0x200
        self.cb_vt = DATA_BASE + 0x300
        self.aux = DATA_BASE + 0x10000
        self.buf = DATA_BASE + 0x20000
        self.cw_addr = DATA_BASE + 0x20
        uc.mem_write(self.cw_addr, struct.pack('<H', 0x027F))

    def _patch_imports(self):
        d, uc = self.d, self.uc
        stubs = {
            '_CIpow': """fxch st(1); fyl2x; fld st(0); frndint; fsub st(1), st(0); fxch st(1);
                         f2xm1; fld1; faddp st(1), st(0); fscale; fstp st(1); ret""",
            '_CIfmod': """fxch st(1); l: fprem; fnstsw ax; sahf; jp l; fstp st(1); ret""",
            'floor': """sub esp, 8; fnstcw word ptr [esp]; mov ax, word ptr [esp]; and ax, 0xF3FF;
                        or ax, 0x0400; mov word ptr [esp+2], ax; fldcw word ptr [esp+2];
                        fld qword ptr [esp+12]; frndint; fldcw word ptr [esp]; add esp, 8; ret""",
            '_copysign': """fld qword ptr [esp+4]; fabs; test byte ptr [esp+19], 0x80; jz d; fchs; d: ret""",
            '??2@YAPAXI@Z': None, 'malloc': None,
            '??3@YAXPAX@Z': 'ret', 'free': 'ret',
            'sprintf': 'xor eax, eax; ret',
            '_initterm': 'ret',
            'DisableThreadLibraryCalls': 'mov eax, 1; ret 4',
        }
        alloc = ("mov eax, dword ptr [0x%x]; mov ecx, dword ptr [esp+4]; add ecx, 15; and ecx, 0xFFFFFFF0;"
                 " add dword ptr [0x%x], ecx; ret") % (self.heap_ptr_addr, self.heap_ptr_addr)
        imp_rva = struct.unpack_from('<I', d, self.opt + 96 + 8)[0]
        o = self.r2o(imp_rva)
        addr = STUB_BASE
        while True:
            oft, ts, fc, name, ft = struct.unpack_from('<IIIII', d, o)
            if name == 0:
                break
            i = 0
            while True:
                t = struct.unpack_from('<I', d, self.r2o(oft or ft) + 4 * i)[0]
                if t == 0:
                    break
                nm = d[self.r2o(t) + 2:].split(b'\0')[0].decode()
                slot = self.base + ft + 4 * i
                if nm == '_adjust_fdiv':
                    uc.mem_write(slot, struct.pack('<I', self.zero_dword))
                else:
                    src = stubs.get(nm, 'int3')
                    if src is None:
                        src = alloc
                    code = self._asm(src, addr)
                    uc.mem_write(addr, code)
                    uc.mem_write(slot, struct.pack('<I', addr))
                    addr += align(len(code), 0x40)
                i += 1
            o += 20
        # callbacks: every vtable slot returns the aux buffer (only GetAuxBuffer, slot 7, is used)
        code = self._asm('mov eax, 0x%x; ret' % self.aux, addr)
        uc.mem_write(addr, code)
        uc.mem_write(self.cb_vt, struct.pack('<64I', *([addr] * 64)))
        uc.mem_write(self.cb, struct.pack('<I', self.cb_vt))

    # ── host state ────────────────────────────────────────────────────
    def set_master(self, sr, bpm, tpb, pos_in_tick=0):
        spt = int((60 * sr) / (bpm * tpb))
        tps = struct.unpack('<f', struct.pack('<f', float(sr) / float(spt)))[0]
        self.sr, self.spt, self.tps = sr, spt, tps
        self.uc.mem_write(self.master, struct.pack('<5if3i', bpm, tpb, sr, spt, pos_in_tick, tps, 0, 0, 0))

    # ── calling ───────────────────────────────────────────────────────
    def _call(self, addr, args, this):
        uc = self.uc
        sp = STACK_TOP - 0x1000
        frame = struct.pack('<I', SENTINEL) + b''.join(struct.pack('<I', a & 0xFFFFFFFF) for a in args)
        sp -= len(frame)
        uc.mem_write(sp, frame)
        uc.reg_write(UC_X86_REG_ESP, sp)
        uc.reg_write(UC_X86_REG_ECX, this or 0)
        uc.reg_write(UC_X86_REG_FPCW, 0x027F)
        uc.emu_start(addr, SENTINEL, count=50_000_000)
        return uc.reg_read(UC_X86_REG_EAX)

    def _vcall(self, idx, args):
        return self._call(self.u32(self.vt + 4 * idx), args, this=self.obj)

    # ── machine API ───────────────────────────────────────────────────
    NOVAL = 255

    def tick(self, cutoff=255, resonance=255, filtype=255, lforate=255, lfodepth=255,
             lfoshape=255, inertia=255, lfophase=255):
        self.uc.mem_write(self.trackvals, bytes([cutoff, resonance, filtype, lforate,
                                                 lfodepth, lfoshape, inertia, lfophase]))
        self._vcall(2, [])

    def set_thev(self, v):
        self.uc.mem_write(self.attrvals, struct.pack('<i', v))
        self._vcall(7, [])

    def work(self, samples, mode=3):
        """samples: list of mono floats (Buzz scale). Returns (active, output list)."""
        n = len(samples)
        self.uc.mem_write(self.buf, struct.pack('<%df' % n, *samples))
        r = self._vcall(3, [self.buf, n, mode]) & 0xFF
        return bool(r), list(self.f32(self.buf, n)) if n > 1 else [self.f32(self.buf)]

    def track_base(self):
        """Address of Tracks[0] (found by the Tick/Work code as the object offset below)."""
        return self.obj + self.track_off

    track_off = None
