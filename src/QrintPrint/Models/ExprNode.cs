using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace QrintPrint.Models;

/// <summary>数学表达式节点种类</summary>
public enum ExprKind
{
    Literal, Placeholder, BinaryOp, Fraction, Sqrt, Power, Abs, FuncCall, Neg, Sequence, Paren,
}

/// <summary>
/// 结构化数学表达式节点（GeoGebra 风格占位输入的统一数据模型）。
/// 用 Children 列表统一表达各类型的子节点,便于编辑时替换占位。
/// </summary>
public sealed class ExprNode : INotifyPropertyChanged
{
    private ExprKind _kind;
    private string _text = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ExprKind Kind
    {
        get => _kind;
        set { if (_kind != value) { _kind = value; OnPropertyChanged(); } }
    }
    /// <summary>Literal: 文本; BinaryOp: 运算符; FuncCall: 函数名</summary>
    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; OnPropertyChanged(); } }
    }
    public ObservableCollection<ExprNode> Children { get; } = new();
    public ExprNode? Parent { get; set; }
    public int IndexInParent { get; set; }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── 工厂方法 ──────────────────────────────────────────
    public static ExprNode Literal(string text) => new() { Kind = ExprKind.Literal, Text = text };
    public static ExprNode Placeholder(string text = "") => new() { Kind = ExprKind.Placeholder, Text = text };

    public static ExprNode BinaryOp(char op, ExprNode l, ExprNode r)
    {
        var n = new ExprNode { Kind = ExprKind.BinaryOp, Text = op.ToString() };
        n.Add(l); n.Add(r); return n;
    }
    public static ExprNode Fraction(ExprNode num, ExprNode den)
    {
        var n = new ExprNode { Kind = ExprKind.Fraction };
        n.Add(num); n.Add(den); return n;
    }
    public static ExprNode Sqrt(ExprNode arg)
    {
        var n = new ExprNode { Kind = ExprKind.Sqrt };
        n.Add(arg); return n;
    }
    public static ExprNode Power(ExprNode b, ExprNode e)
    {
        var n = new ExprNode { Kind = ExprKind.Power };
        n.Add(b); n.Add(e); return n;
    }
    public static ExprNode Abs(ExprNode arg)
    {
        var n = new ExprNode { Kind = ExprKind.Abs };
        n.Add(arg); return n;
    }
    public static ExprNode FuncCall(string name, ExprNode arg)
    {
        var n = new ExprNode { Kind = ExprKind.FuncCall, Text = name };
        n.Add(arg); return n;
    }
    public static ExprNode Neg(ExprNode arg)
    {
        var n = new ExprNode { Kind = ExprKind.Neg };
        n.Add(arg); return n;
    }

    /// <summary>有序兄弟序列(任意多个子节点,如 "2+√3(5)" 顶层与结构参数槽)</summary>
    public static ExprNode Sequence(params ExprNode[] children)
    {
        var n = new ExprNode { Kind = ExprKind.Sequence };
        foreach (var c in children) n.Add(c);
        return n;
    }

    /// <summary>括号容器(ggb 的 ArrayNode 单槽版): 渲染 "(" 内容 ")"</summary>
    public static ExprNode Paren(ExprNode arg)
    {
        var n = new ExprNode { Kind = ExprKind.Paren };
        n.Add(arg); return n;
    }

    private void Add(ExprNode c)
    {
        c.Parent = this;
        c.IndexInParent = Children.Count;
        Children.Add(c);
    }

    /// <summary>深度复制本节点及其子树</summary>
    public ExprNode Clone()
    {
        var n = new ExprNode { Kind = Kind, Text = Text };
        foreach (var c in Children) n.Add(c.Clone());
        return n;
    }

    /// <summary>在父节点中用 newNode 替换本节点</summary>
    public void ReplaceWith(ExprNode newNode)
    {
        var p = Parent;
        if (p is null) { newNode.Parent = null; newNode.IndexInParent = 0; return; }
        newNode.Parent = p;
        newNode.IndexInParent = IndexInParent;
        p.Children[IndexInParent] = newNode;
        Parent = null;
        IndexInParent = 0;
    }

    /// <summary>从本节点沿 Parent 链到根的子索引路径(重渲染后用于恢复光标定位)</summary>
    public List<int> PathFromRoot()
    {
        var path = new List<int>();
        var cur = this;
        while (cur.Parent is not null)
        {
            path.Insert(0, cur.IndexInParent);
            cur = cur.Parent;
        }
        return path;
    }

    /// <summary>返回本节点所在树的根节点</summary>
    public ExprNode RootOwner()
    {
        var cur = this;
        while (cur.Parent is not null) cur = cur.Parent;
        return cur;
    }

    /// <summary>按路径取节点</summary>
    public static ExprNode? NodeAt(ExprNode root, IReadOnlyList<int> path)
    {
        var cur = root;
        foreach (int i in path)
        {
            if (i < 0 || i >= cur.Children.Count) return null;
            cur = cur.Children[i];
        }
        return cur;
    }

    // ── 求值 ──────────────────────────────────────────────
    public double Eval(double x)
    {
        switch (Kind)
        {
            case ExprKind.Literal:
            case ExprKind.Placeholder:
                return EvalLiteral(Text, x);
            case ExprKind.BinaryOp:
                double a = Children[0].Eval(x), b = Children[1].Eval(x);
                return Text switch
                {
                    "+" => a + b,
                    "-" => a - b,
                    "*" => a * b,
                    "/" => a / b,
                    _ => double.NaN,
                };
            case ExprKind.Fraction:
                return Children[0].Eval(x) / Children[1].Eval(x);
            case ExprKind.Sqrt:
                return Math.Sqrt(Children[0].Eval(x));
            case ExprKind.Power:
                return Math.Pow(Children[0].Eval(x), Children[1].Eval(x));
            case ExprKind.Abs:
                return Math.Abs(Children[0].Eval(x));
            case ExprKind.FuncCall:
                return EvalFunc(Text, Children[0].Eval(x));
            case ExprKind.Neg:
                return -Children[0].Eval(x);
            case ExprKind.Sequence:
                // 序列语义 = 子 raw 拼接后的整体表达式
                string raw0 = string.Concat(Children.Select(c => c.ToRaw()));
                if (FunctionEvaluator.TryCompile(raw0, out var fn0, out _) && fn0 is not null)
                    return fn0(x);
                return double.NaN;
            case ExprKind.Paren:
                return Children[0].Eval(x);
        }
        return double.NaN;
    }

    private static double EvalLiteral(string t, double x)
    {
        if (string.IsNullOrWhiteSpace(t)) return double.NaN;
        if (t == "x") return x;
        if (t == "pi" || t == "π") return Math.PI;
        if (t == "e") return Math.E;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
        return double.NaN;
    }

    private static double EvalFunc(string name, double v) => name switch
    {
        "sin" => Math.Sin(v),
        "cos" => Math.Cos(v),
        "tan" => Math.Tan(v),
        "asin" => Math.Asin(v),
        "acos" => Math.Acos(v),
        "atan" => Math.Atan(v),
        "sinh" => Math.Sinh(v),
        "cosh" => Math.Cosh(v),
        "tanh" => Math.Tanh(v),
        "exp" => Math.Exp(v),
        "ln" => Math.Log(v),
        "log" => Math.Log10(v),
        "log2" => Math.Log(v, 2.0),
        "sqrt" => Math.Sqrt(v),
        "cbrt" => Math.Cbrt(v),
        "abs" => Math.Abs(v),
        "sign" => Math.Sign(v),
        "floor" => Math.Floor(v),
        "ceil" => Math.Ceiling(v),
        "round" => Math.Round(v),
        _ => double.NaN,
    };

    // ── 文本表示 ──────────────────────────────────────────

    /// <summary>可被 FunctionEvaluator 解析的原始文本(用于 API/求值复用)</summary>
    public string ToRaw() => Kind switch
    {
        ExprKind.Literal => Text,
        ExprKind.Placeholder => Text,
        ExprKind.BinaryOp => Children[0].ToRaw() + Text + Children[1].ToRaw(),
        ExprKind.Fraction => "(" + Children[0].ToRaw() + ")/(" + Children[1].ToRaw() + ")",
        ExprKind.Sqrt => "sqrt(" + Children[0].ToRaw() + ")",
        ExprKind.Power => "(" + Children[0].ToRaw() + ")^(" + Children[1].ToRaw() + ")",
        ExprKind.Abs => "abs(" + Children[0].ToRaw() + ")",
        ExprKind.FuncCall => Text + "(" + Children[0].ToRaw() + ")",
        ExprKind.Neg => "-(" + Children[0].ToRaw() + ")",
        ExprKind.Sequence => string.Concat(Children.Select(c => c.ToRaw())),
        ExprKind.Paren => "(" + Children[0].ToRaw() + ")",
        _ => "",
    };

    /// <summary>规范显示文本(用于图例)</summary>
    public string ToDisplay() => Kind switch
    {
        ExprKind.Literal => Text,
        ExprKind.Placeholder => string.IsNullOrEmpty(Text) ? "▢" : Text,
        ExprKind.BinaryOp => Children[0].ToDisplay() + Text + Children[1].ToDisplay(),
        ExprKind.Fraction => "(" + Children[0].ToDisplay() + ")/(" + Children[1].ToDisplay() + ")",
        ExprKind.Sqrt => "√(" + Children[0].ToDisplay() + ")",
        ExprKind.Power => Children[0].ToDisplay() + "^(" + Children[1].ToDisplay() + ")",
        ExprKind.Abs => "|" + Children[0].ToDisplay() + "|",
        ExprKind.FuncCall => Text + "(" + Children[0].ToDisplay() + ")",
        ExprKind.Neg => "-" + Children[0].ToDisplay(),
        ExprKind.Sequence => string.Concat(Children.Select(c => c.ToDisplay())),
        ExprKind.Paren => "(" + Children[0].ToDisplay() + ")",
        _ => "",
    };

    /// <summary>深度优先枚举所有占位节点</summary>
    public IEnumerable<ExprNode> EnumeratePlaceholders()
    {
        if (Kind == ExprKind.Placeholder) yield return this;
        foreach (var c in Children)
            foreach (var p in c.EnumeratePlaceholders()) yield return p;
    }

    /// <summary>是否含未填占位(用于错误提示)</summary>
    public bool HasEmptyPlaceholder()
    {
        foreach (var p in EnumeratePlaceholders())
            if (string.IsNullOrWhiteSpace(p.Text)) return true;
        return false;
    }
}