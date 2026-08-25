using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QrintPrint.Models;

namespace QrintPrint.Views.Controls;

/// <summary>
/// GeoGebra 风格结构化数学编辑器（照抄 editor-base 的 InputController/CursorController 语义）。
/// Root 恒为 Sequence；光标 = (当前 Sequence, offset)；每个字符都是独立节点。
/// 字符输入直接操作数学树：占位被吞噬、/→分数、^→上标、√→根号、|→绝对值。
/// </summary>
public partial class ExpressionEditor : UserControl
{
    public static readonly DependencyProperty RootProperty =
        DependencyProperty.Register(nameof(Root), typeof(ExprNode), typeof(ExpressionEditor),
            new PropertyMetadata(null, OnRootChanged));
    public ExprNode? Root
    {
        get => (ExprNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public event EventHandler? ExpressionChanged;
    public event EventHandler? EditorGotFocus;
    public event EventHandler? EnterPressed;

    private ExprNode _seq = null!;
    private int _offset;

    private static readonly SolidColorBrush CaretBrush = new(Color.FromRgb(0x4B, 0x3F, 0xE3));
    private static readonly SolidColorBrush SlotBrush = new(Color.FromRgb(0xE3, 0xE7, 0xF3));
    private static readonly SolidColorBrush SlotHotBrush = new(Color.FromRgb(0xC9, 0xD2, 0xFA));
    private static readonly SolidColorBrush InkBrush = new(Color.FromRgb(0x2B, 0x2B, 0x2B));

    public ExpressionEditor() => InitializeComponent();

    private static void OnRootChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ed = (ExpressionEditor)d;
        ed.EnsureRootSequence();
        ed._seq = ed.Root!;
        ed._offset = ed.Root!.Children.Count;
        ed.Rebuild();
    }

    private void EnsureRootSequence()
    {
        if (Root is null) { Root = ExprNode.Sequence(ExprNode.Placeholder()); return; }
        if (Root.Kind != ExprKind.Sequence) Root = ExprNode.Sequence(Root);
    }

    private ExprNode Cur() => _seq ?? Root!;

    private void Rebuild()
    {
        RootHost.Content = null;
        RootHost.Content = Render(Root!);
        ExpressionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── 树操作 ────────────────────────────────────────────

    private static void Attach(ExprNode parent, ExprNode child, int index)
    {
        child.Parent = parent;
        child.IndexInParent = index;
        if (index >= parent.Children.Count) parent.Children.Add(child);
        else parent.Children.Insert(index, child);
        FixIndices(parent, index + 1);
    }

    private static void Detach(ExprNode parent, int index)
    {
        parent.Children.RemoveAt(index);
        FixIndices(parent, index);
    }

    private static void FixIndices(ExprNode parent, int from)
    {
        for (int k = from; k < parent.Children.Count; k++)
        {
            parent.Children[k].IndexInParent = k;
            parent.Children[k].Parent = parent;
        }
    }

    /// <summary>新结构的参数槽：有左项则包住它，否则放一个占位</summary>
    private static ExprNode Slot(ExprNode? left) =>
        left is null ? ExprNode.Sequence(ExprNode.Placeholder()) : ExprNode.Sequence(left);

    // ── 输入（照抄 InputController.handleChar）──────────────

    /// <summary>单字符输入：'/' '^' '√' '|' '(' ')' 走结构键，其余直接建字</summary>
    public void InsertChar(string token)
    {
        if (token.Length != 1) return;
        char ch = token[0];
        switch (ch)
        {
            case '/': InsertStructure("frac"); return;
            case '^': InsertStructure("power"); return;
            case '\u221A': InsertStructure("sqrt"); return;
            case '|': InsertStructure("abs"); return;
            case '\u00D7': InsertChar("*"); return;
            case '\u00F7': InsertStructure("frac"); return;
            case '\u00B7': InsertChar("*"); return;
            case '\u2212': InsertChar("-"); return;
        }
        if (ch == '(') { InsertParen(); return; }
        if (ch == ')') { TryExitContainer(); return; }

        var seq = Cur();
        int o = _offset;
        ConsumePlaceholders(seq, ref o);
        var lit = ExprNode.Literal(token);
        Attach(seq, lit, o);
        _offset = o + 1;
        Rebuild();
    }

    /// <summary>把光标前/后的占位移除（吞占位），ggb 语义</summary>
    private void ConsumePlaceholders(ExprNode seq, ref int o)
    {
        if (o < seq.Children.Count && seq.Children[o].Kind == ExprKind.Placeholder)
            Detach(seq, o);
        else if (o > 0 && seq.Children[o - 1].Kind == ExprKind.Placeholder)
        {
            Detach(seq, o - 1);
            o--;
        }
    }

    /// <summary>结构键：frac/sqrt/power/abs/neg/paren</summary>
    public void InsertStructure(string kind)
    {
        var seq = Cur();
        int o = _offset;
        ConsumePlaceholders(seq, ref o);
        ExprNode? left = o > 0 ? seq.Children[o - 1] : null;
        ExprNode newNode;
        int slot;
        switch (kind)
        {
            case "frac":
                newNode = ExprNode.Fraction(Slot(left), ExprNode.Sequence(ExprNode.Placeholder()));
                slot = left is null ? 0 : 1;
                break;
            case "sqrt":
                newNode = ExprNode.Sqrt(Slot(left));
                slot = 0;
                break;
            case "power":
                newNode = ExprNode.Power(Slot(left), ExprNode.Sequence(ExprNode.Placeholder()));
                slot = left is null ? 0 : 1;
                break;
            case "abs":
                newNode = ExprNode.Abs(Slot(left));
                slot = 0;
                break;
            case "neg":
                newNode = ExprNode.Neg(Slot(left));
                slot = 0;
                break;
            case "paren":
                newNode = ExprNode.Paren(Slot(left));
                slot = 0;
                break;
            default: return;
        }
        if (left is not null) Detach(seq, o - 1);
        Attach(seq, newNode, o);
        _seq = newNode.Children[slot];
        _offset = _seq.Children.Count;
        Rebuild();
    }

    /// <summary>函数插入：func:sin 等</summary>
    public void InsertFunc(string name)
    {
        if (name.Length == 0) return;
        var seq = Cur();
        int o = _offset;
        ConsumePlaceholders(seq, ref o);
        var call = ExprNode.FuncCall(name, ExprNode.Sequence(ExprNode.Placeholder()));
        Attach(seq, call, o);
        _seq = call.Children[0];
        _offset = _seq.Children.Count;
        Rebuild();
    }

    /// <summary>'('：光标左侧是函数名则建调用，否则建括号容器</summary>
    private void InsertParen()
    {
        var seq = Cur();
        int o = _offset;
        ConsumePlaceholders(seq, ref o);
        string name = ReadNameLeft(seq, o);
        if (name.Length > 0 && IsFunctionName(name))
        {
            for (int i = 0; i < name.Length; i++) Detach(seq, o - 1);
            o -= name.Length;
            var call = ExprNode.FuncCall(name, ExprNode.Sequence(ExprNode.Placeholder()));
            Attach(seq, call, o);
            _seq = call.Children[0];
            _offset = _seq.Children.Count;
            Rebuild();
            return;
        }
        InsertStructure("paren");
    }

    private static string ReadNameLeft(ExprNode seq, int o)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = o - 1; i >= 0; i--)
        {
            var n = seq.Children[i];
            if (n.Kind == ExprKind.Literal && n.Text.Length == 1 && char.IsLetter(n.Text[0])) sb.Insert(0, n.Text);
            else break;
        }
        return sb.ToString();
    }

    private static bool IsFunctionName(string name) =>
        name is "sin" or "cos" or "tan" or "ln" or "log" or "asin" or "acos" or "atan"
            or "exp" or "sqrt" or "cbrt" or "abs" or "floor" or "ceil" or "round" or "sign";

    /// <summary>')'：在容器末端时跳出到外层</summary>
    private void TryExitContainer()
    {
        ExitContainer();
    }

    private void ExitContainer()
    {
        var seq = Cur();
        var parent = seq.Parent;
        if (parent is null) return;
        var outer = parent.Parent;
        if (outer is not null && outer.Kind == ExprKind.Sequence)
        {
            _seq = outer;
            _offset = parent.IndexInParent + 1;
            Rebuild();
        }
        else
        {
            var lit = ExprNode.Literal(")");
            Attach(seq, lit, _offset);
            _offset++;
            Rebuild();
        }
    }

    // ── 光标移动（照抄 CursorController）──────────────────

    public void MoveLeft()
    {
        var seq = Cur();
        int o = _offset;
        if (o > 0)
        {
            var node = seq.Children[o - 1];
            if (node.Children.Count > 0 && node.Children[^1].Kind == ExprKind.Sequence)
            {
                _seq = node.Children[^1];
                _offset = _seq.Children.Count;
            }
            else _offset = o - 1;
        }
        else if (!PrevField(seq)) return;
        Rebuild();
    }

    public void MoveRight()
    {
        var seq = Cur();
        int o = _offset;
        if (o < seq.Children.Count)
        {
            var node = seq.Children[o];
            if (node.Children.Count > 0 && node.Children[0].Kind == ExprKind.Sequence)
            {
                _seq = node.Children[0];
                _offset = 0;
            }
            else _offset = o + 1;
        }
        else if (!NextField(seq)) return;
        Rebuild();
    }

    /// <summary>Tab: 在当前序列查找下一个占位</summary>
    public void MoveNext()
    {
        var seq = Cur();
        for (int i = _offset; i < seq.Children.Count; i++)
        {
            if (seq.Children[i].Kind == ExprKind.Placeholder)
            {
                _offset = i;
                Rebuild();
                return;
            }
        }
        for (int i = 0; i < _offset; i++)
        {
            if (seq.Children[i].Kind == ExprKind.Placeholder)
            {
                _offset = i;
                Rebuild();
                return;
            }
        }
        Rebuild();
    }

    public void MoveUp()
    {
        var seq = Cur();
        var p = seq.Parent;
        if (p is { Kind: ExprKind.Fraction } && seq.IndexInParent == 1)
        {
            _seq = p.Children[0];
            _offset = _seq.Children.Count;
            Rebuild();
        }
    }

    public void MoveDown()
    {
        var seq = Cur();
        var p = seq.Parent;
        if (p is { Kind: ExprKind.Fraction } && seq.IndexInParent == 0)
        {
            _seq = p.Children[1];
            _offset = _seq.Children.Count;
            Rebuild();
        }
    }

    public void MoveHome()
    {
        _seq = Root!;
        _offset = 0;
        Rebuild();
    }

    public void MoveEnd()
    {
        _seq = Root!;
        _offset = Root!.Children.Count;
        Rebuild();
    }

    private bool NextField(ExprNode seq)
    {
        var parent = seq.Parent;
        if (parent is null) return false;
        if (parent.Kind == ExprKind.Sequence)
        {
            _seq = parent;
            _offset = seq.IndexInParent + 1;
            return true;
        }
        var outer = parent.Parent;
        if (outer is not null && outer.Kind == ExprKind.Sequence)
        {
            _seq = outer;
            _offset = parent.IndexInParent + 1;
            return true;
        }
        return NextField(outer ?? parent);
    }

    private bool PrevField(ExprNode seq)
    {
        var parent = seq.Parent;
        if (parent is null) return false;
        if (parent.Kind == ExprKind.Sequence)
        {
            _seq = parent;
            _offset = seq.IndexInParent;
            return true;
        }
        var outer = parent.Parent;
        if (outer is not null && outer.Kind == ExprKind.Sequence)
        {
            _seq = outer;
            _offset = parent.IndexInParent;
            return true;
        }
        return PrevField(parent);
    }

    public void MoveToFirst()
    {
        _seq = Root ??= ExprNode.Sequence(ExprNode.Placeholder());
        _offset = _seq.Children.Count;
        Rebuild();
        EditorGotFocus?.Invoke(this, EventArgs.Empty);
    }

    // ── 退格 / 删除 ────────────────────────────────────────

    public void Backspace()
    {
        var seq = Cur();
        int o = _offset;
        if (o > 0)
        {
            var node = seq.Children[o - 1];
            if (node.Kind == ExprKind.Literal && node.Text.Length > 1)
            {
                node.Text = node.Text[..^1];
                Rebuild();
                return;
            }
            Detach(seq, o - 1);
            _offset = o - 1;
            Rebuild();
            return;
        }
        // 空序列在结构内：删掉结构
        var parent = seq.Parent;
        if (parent is not null && parent.Parent is ExprNode outer && outer.Kind == ExprKind.Sequence)
        {
            int idx = parent.IndexInParent;
            bool seqOnly = parent.Children.Count == 1 && parent.Children[0].Kind == ExprKind.Placeholder;
            if (!seqOnly)
            {
                Detach(outer, idx);
                _seq = outer;
                _offset = idx;
                Rebuild();
            }
        }
    }

    public void Delete() { MoveRight(); Backspace(); }

    public void RequestEnter() => EnterPressed?.Invoke(this, EventArgs.Empty);

    // ── 渲染 ──────────────────────────────────────────────

    private FrameworkElement Render(ExprNode n, double fs = 13)
    {
        switch (n.Kind)
        {
            case ExprKind.Sequence:
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                bool caret = ReferenceEquals(n, _seq);
                for (int k = 0; k < n.Children.Count; k++)
                {
                    bool atCaret = caret && k == _offset;
                    if (atCaret && n.Children[k].Kind != ExprKind.Placeholder)
                        sp.Children.Add(CaretEl());
                    sp.Children.Add(Render(n.Children[k]));
                }
                if (caret && _offset >= n.Children.Count) sp.Children.Add(CaretEl());
                return sp;

            case ExprKind.Literal:
                return Text(n.Text, fs);

            case ExprKind.Placeholder:
                // 光标恰好停在占位正前方 → 在占位内部绘制光标
                bool caretInside = ReferenceEquals(_seq, n.Parent) && _offset == n.IndexInParent;
                bool hot = ReferenceEquals(_seq, n.Parent)
                           && (_offset == n.IndexInParent || _offset == n.IndexInParent + 1);
                var b = new Border
                {
                    Background = hot ? SlotHotBrush : SlotBrush,
                    BorderBrush = hot ? CaretBrush : new SolidColorBrush(Color.FromRgb(0xA8, 0xB0, 0xC0)),
                    BorderThickness = new Thickness(hot ? 1.6 : 1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 2),
                    MinWidth = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = System.Windows.Input.Cursors.IBeam,
                    DataContext = n,
                };
                var inner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (caretInside) inner.Children.Add(CaretEl());
                inner.Children.Add(new TextBlock
                {
                    Text = n.Text, FontFamily = Consolas(), FontSize = fs,
                    Foreground = ForegroundBrush(),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                b.Child = inner;
                b.MouseLeftButtonUp += Placeholder_Click;
                return b;

            case ExprKind.Fraction:
                var fr = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
                fr.Children.Add(CenterEl(Render(n.Children[0])));
                fr.Children.Add(new Border
                {
                    BorderBrush = InkBrush, BorderThickness = new Thickness(0, 1, 0, 0),
                    MinWidth = 18, Margin = new Thickness(0, 1, 0, 1),
                });
                fr.Children.Add(CenterEl(Render(n.Children[1])));
                return fr;

            case ExprKind.Sqrt:
                var sq = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
                sq.Children.Add(new TextBlock { Text = "\u221A", FontSize = 16, Foreground = InkBrush, VerticalAlignment = VerticalAlignment.Center });
                sq.Children.Add(new Border
                {
                    BorderBrush = InkBrush, BorderThickness = new Thickness(0, 1, 0, 0),
                    Padding = new Thickness(3, 0, 1, 0), Child = Render(n.Children[0]),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                return sq;

            case ExprKind.Power:
                // 水平布局: 底数居中, 指数小字号并上提 → "x²" 效果
                var pw = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 2, 1, 2) };
                pw.Children.Add(Render(n.Children[0], fs));
                var supBox = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3, -fs * 0.45, 3, 2) };
                supBox.Children.Add(Render(n.Children[1], fs * 0.68));
                pw.Children.Add(supBox);
                return pw;

            case ExprKind.Abs:
                return Row(Text("|"), Render(n.Children[0]), Text("|"));

            case ExprKind.Paren:
                return Row(Text("("), Render(n.Children[0]), Text(")"));

            case ExprKind.Neg:
                return Row(Text("-"), Render(n.Children[0]));

            case ExprKind.FuncCall:
                return Row(Text(n.Text, fs), Text("("), Render(n.Children[0]), Text(")"));

            case ExprKind.BinaryOp:
                return Row(Render(n.Children[0]), Text(n.Text, fs), Render(n.Children[1]));

            default:
                return Text(n.Text == "" ? "\u25A1" : n.Text, fs);
        }
    }

    private void Placeholder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Border b || b.DataContext is not ExprNode ph) return;
        var parent = ph.Parent;
        if (parent is not { Kind: ExprKind.Sequence }) return;
        _seq = parent;
        _offset = ph.IndexInParent;
        Rebuild();
        EditorGotFocus?.Invoke(this, EventArgs.Empty);
    }

    private static FrameworkElement CenterEl(FrameworkElement e)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(e);
        return sp;
    }

    private static StackPanel Row(params FrameworkElement?[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var it in items) if (it is not null) sp.Children.Add(it);
        return sp;
    }

    private static Border CaretEl() => new()
    {
        Width = 2, MinHeight = 18, Background = CaretBrush,
        Margin = new Thickness(1, 2, 1, 2), VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Text(string s, double fs = 13) => new()
    {
        Text = s, FontFamily = Consolas(), FontSize = fs,
        Foreground = InkBrush, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(1, 0, 1, 0),
    };

    private static Brush ForegroundBrush() => InkBrush;

    private static FontFamily Consolas() => new("Consolas");
}