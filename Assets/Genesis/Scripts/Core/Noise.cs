using System;

namespace Genesis.Core
{
    /// <summary>
    /// Градиентный шум Перлина 2D/3D с собственной таблицей перестановок по seed.
    /// Потокобезопасен после конструирования (только чтение).
    /// </summary>
    public sealed class PerlinNoise
    {
        private readonly int[] _perm = new int[512];

        private static readonly double[] Grad2X = { 1, -1, 1, -1, 1, -1, 0, 0 };
        private static readonly double[] Grad2Y = { 1, 1, -1, -1, 0, 0, 1, -1 };

        public PerlinNoise(long seed)
        {
            var p = new int[256];
            for (int i = 0; i < 256; i++) p[i] = i;
            var rng = new Random64(Hash.SplitMix((ulong)seed));
            for (int i = 255; i > 0; i--)
            {
                int j = rng.NextInt(i + 1);
                int t = p[i];
                p[i] = p[j];
                p[j] = t;
            }
            for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
        }

        private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        private static double Lerp(double a, double b, double t) => a + t * (b - a);

        private static int FastFloor(double x)
        {
            int i = (int)x;
            return x < i ? i - 1 : i;
        }

        /// <summary>Значение примерно в [-1, 1].</summary>
        public double Sample(double x, double y)
        {
            int xi = FastFloor(x), yi = FastFloor(y);
            double xf = x - xi, yf = y - yi;
            xi &= 255;
            yi &= 255;

            double u = Fade(xf), v = Fade(yf);

            int aa = _perm[_perm[xi] + yi];
            int ab = _perm[_perm[xi] + yi + 1];
            int ba = _perm[_perm[xi + 1] + yi];
            int bb = _perm[_perm[xi + 1] + yi + 1];

            double x1 = Lerp(G2(aa, xf, yf), G2(ba, xf - 1, yf), u);
            double x2 = Lerp(G2(ab, xf, yf - 1), G2(bb, xf - 1, yf - 1), u);
            return Lerp(x1, x2, v) * 0.7071;
        }

        private static double G2(int hash, double x, double y)
        {
            int h = hash & 7;
            return Grad2X[h] * x + Grad2Y[h] * y;
        }

        public double Sample(double x, double y, double z)
        {
            int xi = FastFloor(x), yi = FastFloor(y), zi = FastFloor(z);
            double xf = x - xi, yf = y - yi, zf = z - zi;
            xi &= 255;
            yi &= 255;
            zi &= 255;

            double u = Fade(xf), v = Fade(yf), w = Fade(zf);

            int a = _perm[xi] + yi, aa = _perm[a] + zi, ab = _perm[a + 1] + zi;
            int b = _perm[xi + 1] + yi, ba = _perm[b] + zi, bb = _perm[b + 1] + zi;

            double r = Lerp(
                Lerp(Lerp(G3(_perm[aa], xf, yf, zf), G3(_perm[ba], xf - 1, yf, zf), u),
                     Lerp(G3(_perm[ab], xf, yf - 1, zf), G3(_perm[bb], xf - 1, yf - 1, zf), u), v),
                Lerp(Lerp(G3(_perm[aa + 1], xf, yf, zf - 1), G3(_perm[ba + 1], xf - 1, yf, zf - 1), u),
                     Lerp(G3(_perm[ab + 1], xf, yf - 1, zf - 1), G3(_perm[bb + 1], xf - 1, yf - 1, zf - 1), u), v),
                w);
            return r;
        }

        private static double G3(int hash, double x, double y, double z)
        {
            int h = hash & 15;
            double u = h < 8 ? x : y;
            double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        /// <summary>Фрактальный шум (fBm), результат примерно в [-1, 1].</summary>
        public double Fbm(double x, double y, int octaves, double lacunarity = 2.0, double gain = 0.5)
        {
            double sum = 0, amp = 1, norm = 0, freq = 1;
            for (int i = 0; i < octaves; i++)
            {
                sum += Sample(x * freq, y * freq) * amp;
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }
            return sum / norm;
        }

        public double Fbm(double x, double y, double z, int octaves, double lacunarity = 2.0, double gain = 0.5)
        {
            double sum = 0, amp = 1, norm = 0, freq = 1;
            for (int i = 0; i < octaves; i++)
            {
                sum += Sample(x * freq, y * freq, z * freq) * amp;
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }
            return sum / norm;
        }

        /// <summary>«Хребтовый» шум: 1 - |n|, в [0, 1].</summary>
        public double Ridged(double x, double y, int octaves)
        {
            double sum = 0, amp = 1, norm = 0, freq = 1;
            for (int i = 0; i < octaves; i++)
            {
                double n = 1.0 - Math.Abs(Sample(x * freq, y * freq));
                sum += n * n * amp;
                norm += amp;
                amp *= 0.5;
                freq *= 2.0;
            }
            return sum / norm;
        }
    }
}
