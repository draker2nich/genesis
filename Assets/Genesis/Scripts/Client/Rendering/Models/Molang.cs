using System;
using System.Collections.Generic;
using System.Globalization;

namespace Genesis.Client.Rendering.Models
{
    /// <summary>
    /// Маленький вычислитель выражений Molang из анимаций Blockbench: числа, + - * / %, скобки, унарный минус,
    /// query.anim_time / q.anim_time, query.life_time, функции math.sin/cos (градусы), abs, min, max, clamp, lerp,
    /// sqrt, floor, ceil, round, pi. Неизвестное считается нулём. Выражение компилируется один раз в дерево.
    /// </summary>
    public abstract class Molang
    {
        public abstract float Eval(float animTime, float lifeTime);

        public static Molang Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new Const(0);
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return new Const(v);
            try
            {
                var p = new Parser(text.ToLowerInvariant());
                var e = p.Expr();
                return e;
            }
            catch (Exception)
            {
                return new Const(0);
            }
        }

        private sealed class Const : Molang
        {
            private readonly float _v;
            public Const(float v) => _v = v;
            public override float Eval(float a, float l) => _v;
        }

        private sealed class Var : Molang
        {
            private readonly int _kind; // 0 — anim_time, 1 — life_time
            public Var(int kind) => _kind = kind;
            public override float Eval(float a, float l) => _kind == 0 ? a : l;
        }

        private sealed class Unary : Molang
        {
            private readonly Molang _x;
            public Unary(Molang x) => _x = x;
            public override float Eval(float a, float l) => -_x.Eval(a, l);
        }

        private sealed class Binary : Molang
        {
            private readonly Molang _a, _b;
            private readonly char _op;
            public Binary(char op, Molang a, Molang b)
            {
                _op = op;
                _a = a;
                _b = b;
            }

            public override float Eval(float t, float l)
            {
                float x = _a.Eval(t, l), y = _b.Eval(t, l);
                switch (_op)
                {
                    case '+': return x + y;
                    case '-': return x - y;
                    case '*': return x * y;
                    case '/': return y == 0 ? 0 : x / y;
                    case '%': return y == 0 ? 0 : x % y;
                    default: return 0;
                }
            }
        }

        private sealed class Call : Molang
        {
            private readonly string _fn;
            private readonly Molang[] _args;
            public Call(string fn, Molang[] args)
            {
                _fn = fn;
                _args = args;
            }

            private float A(int i, float t, float l) => i < _args.Length ? _args[i].Eval(t, l) : 0;

            public override float Eval(float t, float l)
            {
                const float Deg = (float)(Math.PI / 180.0);
                switch (_fn)
                {
                    case "sin": return (float)Math.Sin(A(0, t, l) * Deg);
                    case "cos": return (float)Math.Cos(A(0, t, l) * Deg);
                    case "abs": return Math.Abs(A(0, t, l));
                    case "min": return Math.Min(A(0, t, l), A(1, t, l));
                    case "max": return Math.Max(A(0, t, l), A(1, t, l));
                    case "clamp": return Math.Max(A(1, t, l), Math.Min(A(2, t, l), A(0, t, l)));
                    case "lerp":
                    {
                        float a = A(0, t, l), b = A(1, t, l);
                        return a + (b - a) * A(2, t, l);
                    }
                    case "sqrt": return (float)Math.Sqrt(Math.Max(0, A(0, t, l)));
                    case "floor": return (float)Math.Floor(A(0, t, l));
                    case "ceil": return (float)Math.Ceiling(A(0, t, l));
                    case "round": return (float)Math.Round(A(0, t, l));
                    default: return 0;
                }
            }
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) => _s = s;

            private void Skip()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private bool Eat(char c)
            {
                Skip();
                if (_i < _s.Length && _s[_i] == c)
                {
                    _i++;
                    return true;
                }
                return false;
            }

            public Molang Expr()
            {
                var left = Term();
                while (true)
                {
                    if (Eat('+')) left = new Binary('+', left, Term());
                    else if (Eat('-')) left = new Binary('-', left, Term());
                    else return left;
                }
            }

            private Molang Term()
            {
                var left = Factor();
                while (true)
                {
                    if (Eat('*')) left = new Binary('*', left, Factor());
                    else if (Eat('/')) left = new Binary('/', left, Factor());
                    else if (Eat('%')) left = new Binary('%', left, Factor());
                    else return left;
                }
            }

            private Molang Factor()
            {
                if (Eat('-')) return new Unary(Factor());
                if (Eat('+')) return Factor();
                if (Eat('('))
                {
                    var e = Expr();
                    Eat(')');
                    return e;
                }
                Skip();
                int start = _i;
                if (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.'))
                {
                    while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                    return new Const(float.Parse(_s.Substring(start, _i - start), CultureInfo.InvariantCulture));
                }
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.')) _i++;
                string name = _s.Substring(start, _i - start);
                if (name.Length == 0) throw new FormatException("molang");
                if (name.StartsWith("math.", StringComparison.Ordinal)) name = name.Substring(5);
                if (name == "pi") return new Const((float)Math.PI);
                if (Eat('('))
                {
                    var args = new List<Molang>();
                    if (!Eat(')'))
                    {
                        do args.Add(Expr());
                        while (Eat(','));
                        Eat(')');
                    }
                    return new Call(name, args.ToArray());
                }
                if (name.EndsWith("anim_time", StringComparison.Ordinal)) return new Var(0);
                if (name.EndsWith("life_time", StringComparison.Ordinal)) return new Var(1);
                return new Const(0);
            }
        }
    }
}
