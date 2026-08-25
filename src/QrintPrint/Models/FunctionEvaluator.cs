using System.Globalization;
using System.Text;

namespace QrintPrint.Models;

/// <summary>
/// 轻量数学表达式解析器（GeoGebra 风格宽松语法、零依赖）。
/// 支持:
///   - 隐式乘法: 2x、3sin(x)、x(x+1)、(x+1)(x-1)、2pi
///   - 省略括号函数调用: sin x、cos 2x、√2
///   - Unicode 数学字符: ² ³ √ π × ÷ · − –
///   - 幂 ^ 或 **;一元正负号;括号;常量 pi/e;变量 x
///   - 函数: sin cos tan asin acos atan sinh cosh tanh exp ln log log2
///     sqrt cbrt abs sign floor ceil round
///   - "f(x)=..." / "y=..." 形式的等号定义前缀(左部被忽略,只编译右部)
/// 解析一次编译为求值函数,绘制曲线时反复求值无需重新解析。
/// </summary>
public static class FunctionEvaluator
{
    /// <summary>解析表达式并编译为求值函数;失败时 func 为 null、error 给出原因(含位置)。</summary>
    public static bool TryCompile(string expr, out Func<double, double>? func, out string? error)
    {
        func = null;
        error = null;
        if (string.IsNullOrWhiteSpace(expr))
        {
            error = "表达式为空";
            return false;
        }

        string body = expr.Trim();

        // 等号定义前缀: f(x)=... / y=... / g=... → 取等号右侧
        int eq = body.IndexOf('=');
        if (eq >= 0)
        {
            string lhs = body[..eq].Trim();
            if (!IsValidDefinitionLhs(lhs))
            {
                error = $"等号左侧“{lhs}”应为函数名(如 f、f(x)、y)";
                return false;
            }
            body = body[(eq + 1)..].Trim();
            if (string.IsNullOrEmpty(body))
            {
                error = "等号右侧表达式为空";
                return false;
            }
        }

        try
        {
            var parser = new Parser(body);
            INode node = parser.ParseExpression();
            parser.ExpectEnd();
            func = node.Eval;
            return true;
        }
        catch (ParseException ex)
        {
            error = ex.Position >= 0
                ? $"位置 {ex.Position + 1}：{ex.Message}"
                : ex.Message;
            return false;
        }
    }

    /// <summary>等号左侧合法形式: y、f、f(x)、g2(x) 等纯标识符或带 (x) 后缀</summary>
    private static bool IsValidDefinitionLhs(string lhs)
    {
        if (string.IsNullOrEmpty(lhs)) return false;
        if (lhs == "y") return true;
        // 形如 name 或 name(x)
        int lp = lhs.IndexOf('(');
        if (lp < 0) return IsIdent(lhs);
        int rp = lhs.LastIndexOf(')');
        if (rp != lhs.Length - 1) return false;
        string name = lhs[..lp];
        string arg = lhs[(lp + 1)..rp];
        return IsIdent(name) && (arg == "x" || arg.Length == 0);
    }

    private static bool IsIdent(string s)
    {
        if (s.Length == 0) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        foreach (char c in s)
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    private sealed class ParseException : InvalidOperationException
    {
        public int Position { get; }
        public ParseException(string message, int position = -1) : base(message)
        {
            Position = position;
        }
    }

    // ── 表达式节点 ────────────────────────────────────────

    private interface INode
    {
        double Eval(double x);
    }

    private sealed class ConstNode : INode
    {
        private readonly double _v;
        public ConstNode(double v) => _v = v;
        public double Eval(double x) => _v;
    }

    private sealed class XNode : INode
    {
        public double Eval(double x) => x;
    }

    private sealed class UnaryNode : INode
    {
        private readonly Func<double, double> _op;
        private readonly INode _arg;
        public UnaryNode(Func<double, double> op, INode arg)
        {
            _op = op;
            _arg = arg;
        }
        public double Eval(double x) => _op(_arg.Eval(x));
    }

    private sealed class BinaryNode : INode
    {
        private readonly char _op;
        private readonly INode _lhs;
        private readonly INode _rhs;
        public BinaryNode(char op, INode lhs, INode rhs)
        {
            _op = op;
            _lhs = lhs;
            _rhs = rhs;
        }
        public double Eval(double x)
        {
            double a = _lhs.Eval(x);
            double b = _rhs.Eval(x);
            return _op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => a / b,
                '%' => a % b,
                '^' => Math.Pow(a, b),
                _ => double.NaN,
            };
        }
    }

    private sealed class FnNode : INode
    {
        private readonly Func<double, double> _fn;
        private readonly INode _arg;
        public FnNode(Func<double, double> fn, INode arg)
        {
            _fn = fn;
            _arg = arg;
        }
        public double Eval(double x) => _fn(_arg.Eval(x));
    }

    // ── 词法 / 语法分析 ──────────────────────────────────

    private enum TokKind
    {
        Number, Ident, Plus, Minus, Star, Slash, Percent, Caret, LParen, RParen, Bar, End,
    }

    private readonly struct Token
    {
        public TokKind Kind { get; }
        public double Number { get; }
        public string Text { get; }
        public int Offset { get; }

        public Token(TokKind kind, string text, double number, int offset)
        {
            Kind = kind;
            Text = text;
            Number = number;
            Offset = offset;
        }
    }

    private sealed class Parser
    {
        private readonly List<Token> _tokens = new();
        private int _pos;

        public Parser(string text)
        {
            Lex(text);
        }

        /// <summary>把 Unicode 数学字符归一化后再切 token</summary>
        private static string Normalize(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\u2212': // − MINUS SIGN
                    case '\u2013': // – EN DASH
                    case '\u2010': // ‐ HYPHEN
                        sb.Append('-'); break;
                    case '\u00D7': // × MULTIPLICATION SIGN
                    case '\u00B7': // · MIDDLE DOT
                    case '\u22C5': // ⋅ DOT OPERATOR
                        sb.Append('*'); break;
                    case '\u00F7': // ÷ DIVISION SIGN
                        sb.Append('/'); break;
                    case '\u03C0': // π
                        sb.Append("pi"); break;
                    case '\u221A': // √ SQUARE ROOT
                        sb.Append("sqrt"); break;
                    case '\u00B2': // ² SUPERSCRIPT TWO
                        sb.Append("^2"); break;
                    case '\u00B3': // ³ SUPERSCRIPT THREE
                        sb.Append("^3"); break;
                    default:
                        sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private void Lex(string text)
        {
            string s = Normalize(text);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '+') { _tokens.Add(new Token(TokKind.Plus, "+", 0, i)); i++; continue; }
                if (c == '-') { _tokens.Add(new Token(TokKind.Minus, "-", 0, i)); i++; continue; }
                if (c == '*')
                {
                    if (i + 1 < s.Length && s[i + 1] == '*') { i++; }
                    _tokens.Add(new Token(TokKind.Star, "*", 0, i)); i++; continue;
                }
                if (c == '/') { _tokens.Add(new Token(TokKind.Slash, "/", 0, i)); i++; continue; }
                if (c == '%') { _tokens.Add(new Token(TokKind.Percent, "%", 0, i)); i++; continue; }
                if (c == '^') { _tokens.Add(new Token(TokKind.Caret, "^", 0, i)); i++; continue; }
                if (c == '(') { _tokens.Add(new Token(TokKind.LParen, "(", 0, i)); i++; continue; }
                if (c == ')') { _tokens.Add(new Token(TokKind.RParen, ")", 0, i)); i++; continue; }
                if (c == '|') { _tokens.Add(new Token(TokKind.Bar, "|", 0, i)); i++; continue; }

                if (char.IsDigit(c) || c == '.')
                {
                    int start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                    {
                        int save = i;
                        int j = i + 1;
                        if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++;
                        if (j < s.Length && char.IsDigit(s[j]))
                        {
                            i = j;
                            while (i < s.Length && char.IsDigit(s[i])) i++;
                        }
                        else
                        {
                            i = save;
                        }
                    }
                    string num = s[start..i];
                    if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        throw new ParseException($"无效的数字 '{num}'", start);
                    _tokens.Add(new Token(TokKind.Number, num, v, start));
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    _tokens.Add(new Token(TokKind.Ident, s[start..i], 0, start));
                    continue;
                }

                throw new ParseException($"无法识别的字符 '{c}'", i);
            }
            _tokens.Add(new Token(TokKind.End, "", 0, s.Length));
        }

        private Token Peek() => _tokens[_pos];
        private Token Next() => _tokens[_pos++];

        private bool Match(TokKind kind)
        {
            if (Peek().Kind == kind) { _pos++; return true; }
            return false;
        }

        private void Expect(TokKind kind, string what)
        {
            var t = Peek();
            if (!Match(kind)) throw new ParseException($"缺少 {what}", t.Offset);
        }

        public void ExpectEnd()
        {
            var t = Peek();
            if (t.Kind != TokKind.End)
                throw new ParseException($"多余的内容 '{t.Text}'", t.Offset);
        }

        public INode ParseExpression()
        {
            var node = ParseTerm();
            while (true)
            {
                if (Match(TokKind.Plus)) node = new BinaryNode('+', node, ParseTerm());
                else if (Match(TokKind.Minus)) node = new BinaryNode('-', node, ParseTerm());
                else break;
            }
            return node;
        }

        /// <summary>项 = 因子 (('*'|'/'|'%'|隐式) 因子)*。隐式乘法:相邻因子间自动插入乘号</summary>
        private INode ParseTerm()
        {
            var node = ParseUnary();
            while (true)
            {
                if (Match(TokKind.Star)) node = new BinaryNode('*', node, ParseUnary());
                else if (Match(TokKind.Slash)) node = new BinaryNode('/', node, ParseUnary());
                else if (Match(TokKind.Percent)) node = new BinaryNode('%', node, ParseUnary());
                else if (StartsFactor(Peek().Kind)) node = new BinaryNode('*', node, ParseUnary()); // 隐式乘
                else break;
            }
            return node;
        }

        /// <summary>下一个 token 是否能作为一个新因子的开头(用于判定隐式乘法)</summary>
        private static bool StartsFactor(TokKind kind) =>
            kind == TokKind.Number || kind == TokKind.Ident || kind == TokKind.LParen || kind == TokKind.Bar;

        private INode ParseUnary()
        {
            if (Match(TokKind.Minus))
            {
                var arg = ParseUnary();
                return new UnaryNode(v => -v, arg);
            }
            if (Match(TokKind.Plus))
            {
                return ParseUnary();
            }
            return ParsePower();
        }

        private INode ParsePower()
        {
            var baseNode = ParsePrimary();
            if (Match(TokKind.Caret))
            {
                var exp = ParseUnary(); // 右结合,支持 x^-2
                return new BinaryNode('^', baseNode, exp);
            }
            return baseNode;
        }

        private INode ParsePrimary()
        {
            var tok = Next();
            switch (tok.Kind)
            {
                case TokKind.Number:
                    return new ConstNode(tok.Number);
                case TokKind.Ident:
                    return ParseIdent(tok.Text, tok.Offset);
                case TokKind.LParen:
                    var inner = ParseExpression();
                    Expect(TokKind.RParen, ")");
                    return inner;
                case TokKind.Bar:
                    var absArg = ParseExpression();
                    Expect(TokKind.Bar, "|");
                    return new FnNode(Math.Abs, absArg);
                default:
                    throw new ParseException($"此处应为数字、x、函数或 (，实际为 '{tok.Text}'", tok.Offset);
            }
        }

        private INode ParseIdent(string name, int offset)
        {
            switch (name)
            {
                case "x":
                    return new XNode();
                case "pi":
                    return new ConstNode(Math.PI);
                case "e":
                    return new ConstNode(Math.E);
            }

            if (FuncMap.TryGetValue(name, out var fn))
            {
                // 带括号: sin(...)
                if (Peek().Kind == TokKind.LParen)
                {
                    Next();
                    var arg = ParseExpression();
                    Expect(TokKind.RParen, ")");
                    return new FnNode(fn, arg);
                }
                // 省略括号: sin x、√2、cos 2x → 参数取一个不含加减的"项"
                if (StartsFactor(Peek().Kind))
                {
                    var arg = ParseTerm();
                    return new FnNode(fn, arg);
                }
                throw new ParseException($"函数 {name} 后缺少参数", Peek().Offset);
            }

            throw new ParseException($"未知的标识符 '{name}'", offset);
        }
    }

    private static readonly Dictionary<string, Func<double, double>> FuncMap = new()
    {
        ["sin"] = Math.Sin,
        ["cos"] = Math.Cos,
        ["tan"] = Math.Tan,
        ["asin"] = Math.Asin,
        ["acos"] = Math.Acos,
        ["atan"] = Math.Atan,
        ["sinh"] = Math.Sinh,
        ["cosh"] = Math.Cosh,
        ["tanh"] = Math.Tanh,
        ["exp"] = Math.Exp,
        ["ln"] = Math.Log,
        ["log"] = Math.Log10,
        ["log2"] = v => Math.Log(v, 2.0),
        ["sqrt"] = Math.Sqrt,
        ["cbrt"] = Math.Cbrt,
        ["abs"] = Math.Abs,
        ["sign"] = v => Math.Sign(v),
        ["floor"] = Math.Floor,
        ["ceil"] = Math.Ceiling,
        ["round"] = Math.Round,
    };
}