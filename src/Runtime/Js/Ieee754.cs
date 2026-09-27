// Port of V8's src/base/ieee754.cc (see THIRD_PARTY_NOTICES.md), whose notice reads:
//
// The following is adapted from fdlibm (http://www.netlib.org/fdlibm).
//
// ====================================================
// Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
//
// Developed at SunSoft, a Sun Microsystems, Inc. business.
// Permission to use, copy, modify, and distribute this
// software is freely granted, provided that this notice
// is preserved.
// ====================================================
//
// The original source code covered by the above license above has been
// modified significantly by Google Inc.
// Copyright 2016 the V8 project authors. All rights reserved.
//
// Why this exists: the terrain generator is a pure function of its seed, and the original runs on V8.
// .NET's Math.Sin/Cos/Pow/Atan2/Exp/Log come from the C runtime and differ from V8 in the last ulp for a
// few percent of all inputs. A single flipped ulp can flip a threshold, and a flipped threshold cascades
// through flood fills into a different world. Every transcendental call in ported domain code therefore
// goes through this file (via JsMath), which reproduces V8 bit for bit.
using System;
using System.Runtime.CompilerServices;

namespace Fluitown.Runtime;

public static class Ieee754
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HighWord(double d) => (int)(BitConverter.DoubleToInt64Bits(d) >>> 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LowWord(double d) => (uint)BitConverter.DoubleToInt64Bits(d);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double FromWords(int hi, uint lo) =>
        BitConverter.Int64BitsToDouble((long)(((ulong)(uint)hi << 32) | lo));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double WithHighWord(double d, int hi) =>
        BitConverter.Int64BitsToDouble(
            (long)(((ulong)BitConverter.DoubleToInt64Bits(d) & 0x0000_0000_FFFF_FFFFUL) | ((ulong)(uint)hi << 32)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double WithLowWord(double d, uint lo) =>
        BitConverter.Int64BitsToDouble(
            (long)(((ulong)BitConverter.DoubleToInt64Bits(d) & 0xFFFF_FFFF_0000_0000UL) | lo));

    private static double Scalbn(double x, int n) => Math.ScaleB(x, n);

    // ── Argument reduction ─────────────────────────────────────────────────────────────────────────────

    private static readonly int[] TwoOverPi =
    {
        0xA2F983, 0x6E4E44, 0x1529FC, 0x2757D1, 0xF534DD, 0xC0DB62, 0x95993C,
        0x439041, 0xFE5163, 0xABDEBB, 0xC561B7, 0x246E3A, 0x424DD2, 0xE00649,
        0x2EEA09, 0xD1921C, 0xFE1DEB, 0x1CB129, 0xA73EE8, 0x8235F5, 0x2EBB44,
        0x84E99C, 0x7026B4, 0x5F7E41, 0x3991D6, 0x398353, 0x39F49C, 0x845F8B,
        0xBDF928, 0x3B1FF8, 0x97FFDE, 0x05980F, 0xEF2F11, 0x8B5A0A, 0x6D1F6D,
        0x367ECF, 0x27CB09, 0xB74F46, 0x3F669E, 0x5FEA2D, 0x7527BA, 0xC7EBE5,
        0xF17B3D, 0x0739F7, 0x8A5292, 0xEA6BFB, 0x5FB11F, 0x8D5D08, 0x560330,
        0x46FC7B, 0x6BABF0, 0xCFBC20, 0x9AF436, 0x1DA9E3, 0x91615E, 0xE61B08,
        0x659985, 0x5F14A0, 0x68408D, 0xFFD880, 0x4D7327, 0x310606, 0x1556CA,
        0x73A8C9, 0x60E27B, 0xC08C6B,
    };

    private static readonly int[] NPio2Hw =
    {
        0x3FF921FB, 0x400921FB, 0x4012D97C, 0x401921FB, 0x401F6A7A, 0x4022D97C,
        0x4025FDBB, 0x402921FB, 0x402C463A, 0x402F6A7A, 0x4031475C, 0x4032D97C,
        0x40346B9C, 0x4035FDBB, 0x40378FDB, 0x403921FB, 0x403AB41B, 0x403C463A,
        0x403DD85A, 0x403F6A7A, 0x40407E4C, 0x4041475C, 0x4042106C, 0x4042D97C,
        0x4043A28C, 0x40446B9C, 0x404534AC, 0x4045FDBB, 0x4046C6CB, 0x40478FDB,
        0x404858EB, 0x404921FB,
    };

    private static int RemPio2(double x, out double y0, out double y1)
    {
        const double zero = 0.0, half = 0.5, two24 = 1.67772160000000000000e+07;
        const double invpio2 = 6.36619772367581382433e-01;
        const double pio2_1 = 1.57079632673412561417e+00, pio2_1t = 6.07710050650619224932e-11;
        const double pio2_2 = 6.07710050630396597660e-11, pio2_2t = 2.02226624879595063154e-21;
        const double pio2_3 = 2.02226624871116645580e-21, pio2_3t = 8.47842766036889956997e-32;

        double z = 0, w, t, r, fn;
        int e0, i, j, nx, n, ix, hx;

        hx = HighWord(x);
        ix = hx & 0x7FFFFFFF;
        if (ix <= 0x3FE921FB)
        {
            y0 = x;
            y1 = 0;
            return 0;
        }
        if (ix < 0x4002D97C)
        {
            if (hx > 0)
            {
                z = x - pio2_1;
                if (ix != 0x3FF921FB)
                {
                    y0 = z - pio2_1t;
                    y1 = (z - y0) - pio2_1t;
                }
                else
                {
                    z -= pio2_2;
                    y0 = z - pio2_2t;
                    y1 = (z - y0) - pio2_2t;
                }
                return 1;
            }
            else
            {
                z = x + pio2_1;
                if (ix != 0x3FF921FB)
                {
                    y0 = z + pio2_1t;
                    y1 = (z - y0) + pio2_1t;
                }
                else
                {
                    z += pio2_2;
                    y0 = z + pio2_2t;
                    y1 = (z - y0) + pio2_2t;
                }
                return -1;
            }
        }
        if (ix <= 0x413921FB)
        {
            t = Math.Abs(x);
            n = (int)(t * invpio2 + half);
            fn = n;
            r = t - fn * pio2_1;
            w = fn * pio2_1t;
            if (n < 32 && ix != NPio2Hw[n - 1])
            {
                y0 = r - w;
            }
            else
            {
                j = ix >> 20;
                y0 = r - w;
                uint high = (uint)HighWord(y0);
                i = j - (int)((high >> 20) & 0x7FF);
                if (i > 16)
                {
                    t = r;
                    w = fn * pio2_2;
                    r = t - w;
                    w = fn * pio2_2t - ((t - r) - w);
                    y0 = r - w;
                    high = (uint)HighWord(y0);
                    i = j - (int)((high >> 20) & 0x7FF);
                    if (i > 49)
                    {
                        t = r;
                        w = fn * pio2_3;
                        r = t - w;
                        w = fn * pio2_3t - ((t - r) - w);
                        y0 = r - w;
                    }
                }
            }
            y1 = (r - y0) - w;
            if (hx < 0)
            {
                y0 = -y0;
                y1 = -y1;
                return -n;
            }
            return n;
        }
        if (ix >= 0x7FF00000)
        {
            y0 = y1 = x - x;
            return 0;
        }
        uint low = LowWord(x);
        z = WithLowWord(z, low);
        e0 = (ix >> 20) - 1046;
        z = WithHighWord(z, ix - (int)((uint)e0 << 20));
        var tx = new double[3];
        for (i = 0; i < 2; i++)
        {
            tx[i] = (int)z;
            z = (z - tx[i]) * two24;
        }
        tx[2] = z;
        nx = 3;
        while (tx[nx - 1] == zero) nx--;
        var y = new double[2];
        n = KernelRemPio2(tx, y, e0, nx, 2, TwoOverPi);
        if (hx < 0)
        {
            y0 = -y[0];
            y1 = -y[1];
            return -n;
        }
        y0 = y[0];
        y1 = y[1];
        return n;
    }

    private static readonly int[] InitJk = { 2, 3, 4, 6 };

    private static readonly double[] PIo2 =
    {
        1.57079625129699707031e+00, 7.54978941586159635335e-08, 5.39030252995776476554e-15,
        3.28200341580791294123e-22, 1.27065575308067607349e-29, 1.22933308981111328932e-36,
        2.73370053816464559624e-44, 2.16741683877804819444e-51,
    };

    private static int KernelRemPio2(double[] x, double[] y, int e0, int nx, int prec, int[] ipio2)
    {
        const double zero = 0.0, one = 1.0, two24 = 1.67772160000000000000e+07, twon24 = 5.96046447753906250000e-08;
        int jz, jx, jv, jp, jk, carry, n, i, j, k, m, q0, ih;
        var iq = new int[20];
        double z, fw;
        var f = new double[20];
        var fq = new double[20];
        var q = new double[20];

        jk = InitJk[prec];
        jp = jk;
        jx = nx - 1;
        jv = (e0 - 3) / 24;
        if (jv < 0) jv = 0;
        q0 = e0 - 24 * (jv + 1);

        j = jv - jx;
        m = jx + jk;
        for (i = 0; i <= m; i++, j++) f[i] = j < 0 ? zero : ipio2[j];

        for (i = 0; i <= jk; i++)
        {
            for (j = 0, fw = 0.0; j <= jx; j++) fw += x[j] * f[jx + i - j];
            q[i] = fw;
        }

        jz = jk;
    recompute:
        for (i = 0, j = jz, z = q[jz]; j > 0; i++, j--)
        {
            fw = (int)(twon24 * z);
            iq[i] = (int)(z - two24 * fw);
            z = q[j - 1] + fw;
        }

        z = Scalbn(z, q0);
        z -= 8.0 * Math.Floor(z * 0.125);
        n = (int)z;
        z -= n;
        ih = 0;
        if (q0 > 0)
        {
            i = iq[jz - 1] >> (24 - q0);
            n += i;
            iq[jz - 1] -= i << (24 - q0);
            ih = iq[jz - 1] >> (23 - q0);
        }
        else if (q0 == 0)
        {
            ih = iq[jz - 1] >> 23;
        }
        else if (z >= 0.5)
        {
            ih = 2;
        }

        if (ih > 0)
        {
            n += 1;
            carry = 0;
            for (i = 0; i < jz; i++)
            {
                j = iq[i];
                if (carry == 0)
                {
                    if (j != 0)
                    {
                        carry = 1;
                        iq[i] = 0x1000000 - j;
                    }
                }
                else
                {
                    iq[i] = 0xFFFFFF - j;
                }
            }
            if (q0 > 0)
            {
                switch (q0)
                {
                    case 1:
                        iq[jz - 1] &= 0x7FFFFF;
                        break;
                    case 2:
                        iq[jz - 1] &= 0x3FFFFF;
                        break;
                }
            }
            if (ih == 2)
            {
                z = one - z;
                if (carry != 0) z -= Scalbn(one, q0);
            }
        }

        if (z == zero)
        {
            j = 0;
            for (i = jz - 1; i >= jk; i--) j |= iq[i];
            if (j == 0)
            {
                for (k = 1; jk >= k && iq[jk - k] == 0; k++)
                {
                }
                for (i = jz + 1; i <= jz + k; i++)
                {
                    f[jx + i] = ipio2[jv + i];
                    for (j = 0, fw = 0.0; j <= jx; j++) fw += x[j] * f[jx + i - j];
                    q[i] = fw;
                }
                jz += k;
                goto recompute;
            }
        }

        if (z == 0.0)
        {
            jz -= 1;
            q0 -= 24;
            while (iq[jz] == 0)
            {
                jz--;
                q0 -= 24;
            }
        }
        else
        {
            z = Scalbn(z, -q0);
            if (z >= two24)
            {
                fw = (int)(twon24 * z);
                iq[jz] = (int)(z - two24 * fw);
                jz += 1;
                q0 += 24;
                iq[jz] = (int)fw;
            }
            else
            {
                iq[jz] = (int)z;
            }
        }

        fw = Scalbn(one, q0);
        for (i = jz; i >= 0; i--)
        {
            q[i] = fw * iq[i];
            fw *= twon24;
        }

        for (i = jz; i >= 0; i--)
        {
            for (fw = 0.0, k = 0; k <= jp && k <= jz - i; k++) fw += PIo2[k] * q[i + k];
            fq[jz - i] = fw;
        }

        switch (prec)
        {
            case 0:
                fw = 0.0;
                for (i = jz; i >= 0; i--) fw += fq[i];
                y[0] = ih == 0 ? fw : -fw;
                break;
            case 1:
            case 2:
                fw = 0.0;
                for (i = jz; i >= 0; i--) fw += fq[i];
                y[0] = ih == 0 ? fw : -fw;
                fw = fq[0] - fw;
                for (i = 1; i <= jz; i++) fw += fq[i];
                y[1] = ih == 0 ? fw : -fw;
                break;
            case 3:
                for (i = jz; i > 0; i--)
                {
                    fw = fq[i - 1] + fq[i];
                    fq[i] += fq[i - 1] - fw;
                    fq[i - 1] = fw;
                }
                for (i = jz; i > 1; i--)
                {
                    fw = fq[i - 1] + fq[i];
                    fq[i] += fq[i - 1] - fw;
                    fq[i - 1] = fw;
                }
                for (fw = 0.0, i = jz; i >= 2; i--) fw += fq[i];
                if (ih == 0)
                {
                    y[0] = fq[0];
                    y[1] = fq[1];
                    y[2] = fw;
                }
                else
                {
                    y[0] = -fq[0];
                    y[1] = -fq[1];
                    y[2] = -fw;
                }
                break;
        }
        return n & 7;
    }

    // ── Kernels ────────────────────────────────────────────────────────────────────────────────────────

    private static double KernelCos(double x, double y)
    {
        const double one = 1.0;
        const double C1 = 4.16666666666666019037e-02, C2 = -1.38888888888741095749e-03;
        const double C3 = 2.48015872894767294178e-05, C4 = -2.75573143513906633035e-07;
        const double C5 = 2.08757232129817482790e-09, C6 = -1.13596475577881948265e-11;
        double a, iz, z, r, qx;
        int ix = HighWord(x) & 0x7FFFFFFF;
        if (ix < 0x3E400000)
        {
            if ((int)x == 0) return one;
        }
        z = x * x;
        r = z * (C1 + z * (C2 + z * (C3 + z * (C4 + z * (C5 + z * C6)))));
        if (ix < 0x3FD33333) return one - (0.5 * z - (z * r - x * y));
        if (ix > 0x3FE90000) qx = 0.28125;
        else qx = FromWords(ix - 0x00200000, 0);
        iz = 0.5 * z - qx;
        a = one - qx;
        return a - (iz - (z * r - x * y));
    }

    private static double KernelSin(double x, double y, int iy)
    {
        const double half = 0.5;
        const double S1 = -1.66666666666666324348e-01, S2 = 8.33333333332248946124e-03;
        const double S3 = -1.98412698298579493134e-04, S4 = 2.75573137070700676789e-06;
        const double S5 = -2.50507602534068634195e-08, S6 = 1.58969099521155010221e-10;
        double z, r, v;
        int ix = HighWord(x) & 0x7FFFFFFF;
        if (ix < 0x3E400000)
        {
            if ((int)x == 0) return x;
        }
        z = x * x;
        v = z * x;
        r = S2 + z * (S3 + z * (S4 + z * (S5 + z * S6)));
        if (iy == 0) return x + v * (S1 + z * r);
        return x - ((z * (half * y - v * r) - y) - v * S1);
    }

    private static readonly double[] TanT =
    {
        3.33333333333334091986e-01, 1.33333333333201242699e-01, 5.39682539762260521377e-02,
        2.18694882948595424599e-02, 8.86323982359930005737e-03, 3.59207910759131235356e-03,
        1.45620945432529025516e-03, 5.88041240820264096874e-04, 2.46463134818469906812e-04,
        7.81794442939557092300e-05, 7.14072491382608190305e-05, -1.85586374855275456654e-05,
        2.59073051863633712884e-05,
    };

    private static double KernelTan(double x, double y, int iy)
    {
        const double one = 1.0, pio4 = 7.85398163397448278999e-01, pio4lo = 3.06161699786838301793e-17;
        var T = TanT;
        double z, r, v, w, s;
        int hx = HighWord(x);
        int ix = hx & 0x7FFFFFFF;
        if (ix < 0x3E300000)
        {
            if ((int)x == 0)
            {
                uint low = LowWord(x);
                if (((ix | (int)low) | (iy + 1)) == 0) return one / Math.Abs(x);
                if (iy == 1) return x;
                double a2, t2;
                z = w = x + y;
                z = WithLowWord(z, 0);
                v = y - (z - x);
                t2 = a2 = -one / w;
                t2 = WithLowWord(t2, 0);
                s = one + t2 * z;
                return t2 + a2 * (s + t2 * v);
            }
        }
        if (ix >= 0x3FE59428)
        {
            if (hx < 0)
            {
                x = -x;
                y = -y;
            }
            z = pio4 - x;
            w = pio4lo - y;
            x = z + w;
            y = 0.0;
        }
        z = x * x;
        w = z * z;
        r = T[1] + w * (T[3] + w * (T[5] + w * (T[7] + w * (T[9] + w * T[11]))));
        v = z * (T[2] + w * (T[4] + w * (T[6] + w * (T[8] + w * (T[10] + w * T[12])))));
        s = z * x;
        r = y + z * (s * (r + v) + y);
        r += T[0] * s;
        w = x + r;
        if (ix >= 0x3FE59428)
        {
            v = iy;
            return (1 - ((hx >> 30) & 2)) * (v - 2.0 * (x - (w * w / (w + v) - r)));
        }
        if (iy == 1) return w;
        double a, t;
        z = w;
        z = WithLowWord(z, 0);
        v = r - (z - x);
        t = a = -1.0 / w;
        t = WithLowWord(t, 0);
        s = 1.0 + t * z;
        return t + a * (s + t * v);
    }

    // ── Public functions ───────────────────────────────────────────────────────────────────────────────

    private static readonly double[] AtanHi =
    {
        4.63647609000806093515e-01, 7.85398163397448278999e-01, 9.82793723247329054082e-01,
        1.57079632679489655800e+00,
    };

    private static readonly double[] AtanLo =
    {
        2.26987774529616870924e-17, 3.06161699786838301793e-17, 1.39033110312309984516e-17,
        6.12323399573676603587e-17,
    };

    private static readonly double[] AtanT =
    {
        3.33333333333329318027e-01, -1.99999999998764832476e-01, 1.42857142725034663711e-01,
        -1.11111104054623557880e-01, 9.09088713343650656196e-02, -7.69187620504482999495e-02,
        6.66107313738753120669e-02, -5.83357013379057348645e-02, 4.97687799461593236017e-02,
        -3.65315727442169155270e-02, 1.62858201153657823623e-02,
    };

    public static double Atan(double x)
    {
        const double one = 1.0, huge = 1.0e300;
        var aT = AtanT;
        double w, s1, s2, z;
        int ix, hx, id;
        hx = HighWord(x);
        ix = hx & 0x7FFFFFFF;
        if (ix >= 0x44100000)
        {
            uint low = LowWord(x);
            if (ix > 0x7FF00000 || (ix == 0x7FF00000 && low != 0)) return x + x;
            if (hx > 0) return AtanHi[3] + AtanLo[3];
            return -AtanHi[3] - AtanLo[3];
        }
        if (ix < 0x3FDC0000)
        {
            if (ix < 0x3E400000)
            {
                if (huge + x > one) return x;
            }
            id = -1;
        }
        else
        {
            x = Math.Abs(x);
            if (ix < 0x3FF30000)
            {
                if (ix < 0x3FE60000)
                {
                    id = 0;
                    x = (2.0 * x - one) / (2.0 + x);
                }
                else
                {
                    id = 1;
                    x = (x - one) / (x + one);
                }
            }
            else
            {
                if (ix < 0x40038000)
                {
                    id = 2;
                    x = (x - 1.5) / (one + 1.5 * x);
                }
                else
                {
                    id = 3;
                    x = -1.0 / x;
                }
            }
        }
        z = x * x;
        w = z * z;
        s1 = z * (aT[0] + w * (aT[2] + w * (aT[4] + w * (aT[6] + w * (aT[8] + w * aT[10])))));
        s2 = w * (aT[1] + w * (aT[3] + w * (aT[5] + w * (aT[7] + w * aT[9]))));
        if (id < 0) return x - x * (s1 + s2);
        z = AtanHi[id] - ((x * (s1 + s2) - AtanLo[id]) - x);
        return hx < 0 ? -z : z;
    }

    public static double Atan2(double y, double x)
    {
        const double tiny = 1.0e-300, zero = 0.0;
        const double pi_o_4 = 7.8539816339744827900E-01, pi_o_2 = 1.5707963267948965580E+00;
        const double pi = 3.1415926535897931160E+00, pi_lo = 1.2246467991473531772E-16;
        double z;
        int k, m, hx, hy, ix, iy;
        uint lx, ly;
        hx = HighWord(x);
        lx = LowWord(x);
        ix = hx & 0x7FFFFFFF;
        hy = HighWord(y);
        ly = LowWord(y);
        iy = hy & 0x7FFFFFFF;
        if ((uint)(ix | (int)((lx | unchecked((uint)(-(int)lx))) >> 31)) > 0x7FF00000u ||
            (uint)(iy | (int)((ly | unchecked((uint)(-(int)ly))) >> 31)) > 0x7FF00000u)
        {
            return x + y;
        }
        if ((unchecked(hx - 0x3FF00000) | (int)lx) == 0) return Atan(y);
        m = ((hy >> 31) & 1) | ((hx >> 30) & 2);

        if ((iy | (int)ly) == 0)
        {
            switch (m)
            {
                case 0:
                case 1:
                    return y;
                case 2:
                    return pi + tiny;
                case 3:
                    return -pi - tiny;
            }
        }
        if ((ix | (int)lx) == 0) return hy < 0 ? -pi_o_2 - tiny : pi_o_2 + tiny;

        if (ix == 0x7FF00000)
        {
            if (iy == 0x7FF00000)
            {
                switch (m)
                {
                    case 0: return pi_o_4 + tiny;
                    case 1: return -pi_o_4 - tiny;
                    case 2: return 3.0 * pi_o_4 + tiny;
                    case 3: return -3.0 * pi_o_4 - tiny;
                }
            }
            else
            {
                switch (m)
                {
                    case 0: return zero;
                    case 1: return -zero;
                    case 2: return pi + tiny;
                    case 3: return -pi - tiny;
                }
            }
        }
        if (iy == 0x7FF00000) return hy < 0 ? -pi_o_2 - tiny : pi_o_2 + tiny;

        k = (iy - ix) >> 20;
        if (k > 60)
        {
            z = pi_o_2 + 0.5 * pi_lo;
            m &= 1;
        }
        else if (hx < 0 && k < -60)
        {
            z = 0.0;
        }
        else
        {
            z = Atan(Math.Abs(y / x));
        }
        switch (m)
        {
            case 0: return z;
            case 1: return -z;
            case 2: return pi - (z - pi_lo);
            default: return (z - pi_lo) - pi;
        }
    }

    public static double Cos(double x)
    {
        int ix = HighWord(x) & 0x7FFFFFFF;
        if (ix <= 0x3FE921FB) return KernelCos(x, 0.0);
        if (ix >= 0x7FF00000) return x - x;
        int n = RemPio2(x, out double y0, out double y1);
        switch (n & 3)
        {
            case 0: return KernelCos(y0, y1);
            case 1: return -KernelSin(y0, y1, 1);
            case 2: return -KernelCos(y0, y1);
            default: return KernelSin(y0, y1, 1);
        }
    }

    public static double Sin(double x)
    {
        int ix = HighWord(x) & 0x7FFFFFFF;
        if (ix <= 0x3FE921FB) return KernelSin(x, 0.0, 0);
        if (ix >= 0x7FF00000) return x - x;
        int n = RemPio2(x, out double y0, out double y1);
        switch (n & 3)
        {
            case 0: return KernelSin(y0, y1, 1);
            case 1: return KernelCos(y0, y1);
            case 2: return -KernelSin(y0, y1, 1);
            default: return -KernelCos(y0, y1);
        }
    }

    public static double Tan(double x)
    {
        int ix = HighWord(x) & 0x7FFFFFFF;
        if (ix <= 0x3FE921FB) return KernelTan(x, 0.0, 1);
        if (ix >= 0x7FF00000) return x - x;
        int n = RemPio2(x, out double y0, out double y1);
        return KernelTan(y0, y1, 1 - ((n & 1) << 1));
    }

    public static double Exp(double x)
    {
        const double one = 1.0;
        const double o_threshold = 7.09782712893383973096e+02, u_threshold = -7.45133219101941108420e+02;
        const double invln2 = 1.44269504088896338700e+00;
        const double P1 = 1.66666666666666019037e-01, P2 = -2.77777777770155933842e-03;
        const double P3 = 6.61375632143793436117e-05, P4 = -1.65339022054652515390e-06;
        const double P5 = 4.13813679705723846039e-08, E = 2.718281828459045;
        const double huge = 1.0e+300, twom1000 = 9.33263618503218878990e-302, two1023 = 8.988465674311579539e307;
        double y, hi = 0.0, lo = 0.0, c, t, twopk;
        int k = 0, xsb;
        uint hx = (uint)HighWord(x);
        xsb = (int)((hx >> 31) & 1);
        hx &= 0x7FFFFFFF;

        if (hx >= 0x40862E42)
        {
            if (hx >= 0x7FF00000)
            {
                uint lx = LowWord(x);
                if (((hx & 0xFFFFF) | lx) != 0) return x + x;
                return xsb == 0 ? x : 0.0;
            }
            if (x > o_threshold) return huge * huge;
            if (x < u_threshold) return twom1000 * twom1000;
        }

        if (hx > 0x3FD62E42)
        {
            if (hx < 0x3FF0A2B2)
            {
                if (x == 1.0) return E;
                hi = x - (xsb == 0 ? 6.93147180369123816490e-01 : -6.93147180369123816490e-01);
                lo = xsb == 0 ? 1.90821492927058770002e-10 : -1.90821492927058770002e-10;
                k = 1 - xsb - xsb;
            }
            else
            {
                k = (int)(invln2 * x + (xsb == 0 ? 0.5 : -0.5));
                t = k;
                hi = x - t * 6.93147180369123816490e-01;
                lo = t * 1.90821492927058770002e-10;
            }
            x = hi - lo;
        }
        else if (hx < 0x3E300000)
        {
            if (huge + x > one) return one + x;
        }
        else
        {
            k = 0;
        }

        t = x * x;
        if (k >= -1021) twopk = FromWords(0x3FF00000 + (int)((uint)k << 20), 0);
        else twopk = FromWords(0x3FF00000 + (int)((uint)(k + 1000) << 20), 0);
        c = x - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
        if (k == 0) return one - ((x * c) / (c - 2.0) - x);
        y = one - ((lo - (x * c) / (2.0 - c)) - hi);
        if (k >= -1021)
        {
            if (k == 1024) return y * 2.0 * two1023;
            return y * twopk;
        }
        return y * twopk * twom1000;
    }

    public static double Log(double x)
    {
        const double ln2_hi = 6.93147180369123816490e-01, ln2_lo = 1.90821492927058770002e-10;
        const double two54 = 1.80143985094819840000e+16;
        const double Lg1 = 6.666666666666735130e-01, Lg2 = 3.999999999940941908e-01;
        const double Lg3 = 2.857142874366239149e-01, Lg4 = 2.222219843214978396e-01;
        const double Lg5 = 1.818357216161805012e-01, Lg6 = 1.531383769920937332e-01;
        const double Lg7 = 1.479819860511658591e-01;
        const double zero = 0.0;
        double hfsq, f, s, z, R, w, t1, t2, dk;
        int k, hx, i, j;
        uint lx;
        hx = HighWord(x);
        lx = LowWord(x);
        k = 0;
        if (hx < 0x00100000)
        {
            if (((hx & 0x7FFFFFFF) | (int)lx) == 0) return double.NegativeInfinity;
            if (hx < 0) return double.NaN;
            k -= 54;
            x *= two54;
            hx = HighWord(x);
        }
        if (hx >= 0x7FF00000) return x + x;
        k += (hx >> 20) - 1023;
        hx &= 0x000FFFFF;
        i = (hx + 0x95F64) & 0x100000;
        x = WithHighWord(x, hx | (i ^ 0x3FF00000));
        k += i >> 20;
        f = x - 1.0;
        if ((0x000FFFFF & (2 + hx)) < 3)
        {
            if (f == zero)
            {
                if (k == 0) return zero;
                dk = k;
                return dk * ln2_hi + dk * ln2_lo;
            }
            R = f * f * (0.5 - 0.33333333333333333 * f);
            if (k == 0) return f - R;
            dk = k;
            return dk * ln2_hi - ((R - dk * ln2_lo) - f);
        }
        s = f / (2.0 + f);
        dk = k;
        z = s * s;
        i = hx - 0x6147A;
        w = z * z;
        j = 0x6B851 - hx;
        t1 = w * (Lg2 + w * (Lg4 + w * Lg6));
        t2 = z * (Lg1 + w * (Lg3 + w * (Lg5 + w * Lg7)));
        i |= j;
        R = t2 + t1;
        if (i > 0)
        {
            hfsq = 0.5 * f * f;
            if (k == 0) return f - (hfsq - s * (hfsq + R));
            return dk * ln2_hi - ((hfsq - (s * (hfsq + R) + dk * ln2_lo)) - f);
        }
        if (k == 0) return f - s * (f - R);
        return dk * ln2_hi - ((s * (f - R) - dk * ln2_lo) - f);
    }
}
