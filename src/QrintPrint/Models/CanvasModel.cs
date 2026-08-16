// CanvasModel.cs
//
// 自定义画布的文档模型。
//
// 坐标单位一律是**打印点**(1 点 = 1/8 mm),画布固定 384 点宽 —— 和打印头一致,
// 屏幕上再按 显示宽度/384 缩放显示。所有几何量都存点数,
// 这样合成时不用做任何单位换算,拖拽产生的屏幕位移在视图层除以缩放比即可。
//
// 翻译自 QringPrint/entry/src/main/ets/model/CanvasModel.ets
// @ObservedV2/@Trace → INotifyPropertyChanged + CallerMemberName

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using QrintPrint.Bluetooth;

namespace QrintPrint.Models;

public enum ElementKind
{
    TEXT = 0,
    IMAGE = 1,
    CODE = 2,
    FORMULA = 3,
    TABLE = 4,
}

public static class CanvasModelConstants
{
    public const int CANVAS_BOTTOM_PAD = 16;
    public const int MIN_LENGTH_FLOOR = 80;
    public const int MIN_LENGTH_CEIL = 1200;
    public const int MAX_CANVAS_HEIGHT = 4000;
    public const int MIN_ELEMENT_SIZE = 24;
    public const int DEFAULT_IMAGE_WIDTH = 240;
    public const int DEFAULT_CODE_2D_SIZE = 160;
    public const int DEFAULT_CODE_1D_WIDTH = 280;
    public const int CODE_GEN_SIZE = 384;
    public const int ONE_D_NATURAL_HEIGHT = 140;

    /// <summary>一维码「自然」宽高比(w/h)。生成后压扁到 140 高,显示时按它推导</summary>
    public static double CodeOneDAspect() => (double)CODE_GEN_SIZE / ONE_D_NATURAL_HEIGHT;

    /// <summary>新元素的默认落点:横向居中</summary>
    public static int CenteredX(int dotW) => Math.Max(0, (int)Math.Round((QringProtocol.WIDTH_DOTS - dotW) / 2.0));
}

/// <summary>
/// 画布元素。
/// 几何量用 DotX/DotY/DotW/DotH 而不是 X/Y/Width/Height —— 标明单位是打印点。
/// </summary>
public sealed class CanvasElement : INotifyPropertyChanged
{
    private static int s_nextId = 1;

    /// <summary>JSON 反序列化用的无参构造</summary>
    public CanvasElement() : this(ElementKind.TEXT) { }

    public CanvasElement(ElementKind kind)
    {
        Kind = kind;
        Id = $"el_{s_nextId++}";
    }

    public string Id { get; set; } = string.Empty;
    public ElementKind Kind { get; set; }

    private double _dotX, _dotY, _dotW, _dotH;
    private double _rotation;
    private double _aspect = 1;
    private bool _geometryLocked;
    private bool _locked;
    private bool _invert;
    private bool _rendering;

    // 文字元素
    private string _text = string.Empty;
    private RasterEncoder.TextRenderOptions _textOptions = RasterEncoder.DefaultTextOptions;
    private TextEnhanceMode _enhanceMode = TextEnhanceMode.NONE;

    // 图片元素
    private string _imageUri = string.Empty;
    private DitherMode _ditherMode = DitherMode.FLOYD_STEINBERG;
    private int _imageThreshold = 128;

    // 公式元素
    private string _formulaLatex = string.Empty;

    // 条码元素
    private string _codeContent = string.Empty;
    private int _codeTypeIndex;

    // 表格元素
    private int _tableRows = 2;
    private int _tableCols = 3;
    private string _tableData = "表头1,表头2,表头3\n数据1,数据2,数据3";
    private string _tableColWeights = string.Empty;
    private int _tableFontSize = 14;

    /// <summary>合成用的二值位图,1 = 黑,长度 DotW * DotH。null 表示还没渲染出来</summary>
    [JsonIgnore]
    public byte[]? Binary { get; set; }

    /// <summary>解码后的灰度缓存(384 宽)。缩放和换抖动算法都从它重算</summary>
    [JsonIgnore]
    public GrayImage? SourceGray { get; set; }

    /// <summary>屏幕显示用的位图</summary>
    [JsonIgnore]
    public System.Windows.Media.Imaging.BitmapSource? Preview { get; set; }

    public double DotX { get => _dotX; set => SetField(ref _dotX, value); }
    public double DotY { get => _dotY; set => SetField(ref _dotY, value); }
    public double DotW { get => _dotW; set => SetField(ref _dotW, value); }
    public double DotH { get => _dotH; set => SetField(ref _dotH, value); }

    /// <summary>
    /// 旋转角度（度，绕元素中心，顺时针）。0 = 不旋转。
    /// 旧模板 JSON 没有该字段，反序列化默认 0，向后兼容。
    /// </summary>
    public double Rotation { get => _rotation; set => SetField(ref _rotation, value); }

    public double Aspect { get => _aspect; set => SetField(ref _aspect, value); }
    public bool GeometryLocked { get => _geometryLocked; set => SetField(ref _geometryLocked, value); }

    /// <summary>锁定：画布上不可选中拖动/缩放/旋转（属性面板仍可编辑）</summary>
    public bool Locked { get => _locked; set => SetField(ref _locked, value); }

    /// <summary>反色：打印时黑白色互换（黑底白字效果）</summary>
    public bool Invert { get => _invert; set => SetField(ref _invert, value); }

    public bool Rendering { get => _rendering; set => SetField(ref _rendering, value); }

    public string Text { get => _text; set => SetField(ref _text, value); }
    public RasterEncoder.TextRenderOptions TextOptions
    {
        get => _textOptions;
        set => SetField(ref _textOptions, value);
    }

    /// <summary>文字增强算法（浓度指令不生效的机器靠它提清晰度）</summary>
    public TextEnhanceMode EnhanceMode { get => _enhanceMode; set => SetField(ref _enhanceMode, value); }

    public string ImageUri { get => _imageUri; set => SetField(ref _imageUri, value); }
    public DitherMode DitherMode { get => _ditherMode; set => SetField(ref _ditherMode, value); }

    /// <summary>图片二值化阈值（仅"无"抖动模式生效，0~255）</summary>
    public int ImageThreshold { get => _imageThreshold; set => SetField(ref _imageThreshold, value); }

    public string FormulaLatex { get => _formulaLatex; set => SetField(ref _formulaLatex, value); }

    public string CodeContent { get => _codeContent; set => SetField(ref _codeContent, value); }
    public int CodeTypeIndex { get => _codeTypeIndex; set => SetField(ref _codeTypeIndex, value); }

    public int TableRows { get => _tableRows; set => SetField(ref _tableRows, value); }
    public int TableCols { get => _tableCols; set => SetField(ref _tableCols, value); }

    /// <summary>表格数据：\n 分行、逗号分列</summary>
    public string TableData { get => _tableData; set => SetField(ref _tableData, value); }

    /// <summary>列宽权重：逗号分隔正数，留空自动均分</summary>
    public string TableColWeights { get => _tableColWeights; set => SetField(ref _tableColWeights, value); }
    public int TableFontSize { get => _tableFontSize; set => SetField(ref _tableFontSize, value); }

    public CodeType CodeType()
    {
        if (CodeTypeIndex >= 0 && CodeTypeIndex < BarcodeModel.CodeTypes.Length)
        {
            return BarcodeModel.CodeTypes[CodeTypeIndex];
        }
        return BarcodeModel.CodeTypes[0];
    }

    /// <summary>换掉预览图并把旧的放掉</summary>
    public void SetPreview(System.Windows.Media.Imaging.BitmapSource? next)
    {
        Preview = next;
        OnPropertyChanged(nameof(Preview));
    }

    public void Release()
    {
        SetPreview(null);
        Binary = null;
        SourceGray = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>画布文档</summary>
public sealed class CanvasDoc : INotifyPropertyChanged
{
    /// <summary>JSON 反序列化用的无参构造</summary>
    public CanvasDoc() { }

    private List<CanvasElement> _elements = new();
    private int _minLength = 200;
    private string _selectedId = string.Empty;
    private HashSet<string> _selectedIds = new();

    /// <summary>
    /// 坐标版本：2 = 元素 DotX 为「纸面坐标系」（0..纸宽点，含居中偏移）。
    /// 旧模板（无此字段，默认 0）元素 DotX 是「内容区坐标系」（0..384），加载时需转换。
    /// </summary>
    public int CoordVersion { get; set; } = 2;

    /// <summary>增删都整体换数组,保证 PropertyChanged 触发</summary>
    public List<CanvasElement> Elements
    {
        get => _elements;
        set => SetField(ref _elements, value);
    }

    /// <summary>最小长度(点)。内容不足这么长时按它出纸,方便固定长度的标签</summary>
    public int MinLength
    {
        get => _minLength;
        set => SetField(ref _minLength, value);
    }

    /// <summary>当前选中的元素 id,空串表示没选中</summary>
    public string SelectedId
    {
        get => _selectedId;
        set => SetField(ref _selectedId, value);
    }

    /// <summary>多选集合（画布交互用）。SelectedId 是其中"主选中"（属性面板/列表跟随它）</summary>
    [JsonIgnore]
    public IReadOnlyCollection<string> SelectedIds => _selectedIds;

    /// <summary>当前选中的元素列表（按文档顺序）</summary>
    [JsonIgnore]
    public IReadOnlyList<CanvasElement> SelectedElements
    {
        get
        {
            var list = new List<CanvasElement>();
            foreach (var el in _elements)
            {
                if (_selectedIds.Contains(el.Id)) list.Add(el);
            }
            return list;
        }
    }

    /// <summary>单选：清掉旧选择，只选这一个</summary>
    public void Select(string id)
    {
        var next = new HashSet<string> { id };
        bool changed = !_selectedIds.SetEquals(next);
        _selectedIds = next;
        SelectedId = id;
        if (changed) OnPropertyChanged(nameof(SelectedIds));
    }

    /// <summary>切换选择（Ctrl+点击）。取消选中最后一个时 SelectedId 回退到剩余选中或空</summary>
    public void ToggleSelect(string id)
    {
        if (_selectedIds.Contains(id))
        {
            _selectedIds.Remove(id);
            if (SelectedId == id) SelectedId = _selectedIds.LastOrDefault() ?? string.Empty;
        }
        else
        {
            _selectedIds.Add(id);
            SelectedId = id;
        }
        OnPropertyChanged(nameof(SelectedIds));
    }

    /// <summary>批量选中（框选）。主选中取最后一个</summary>
    public void SelectMany(IEnumerable<string> ids)
    {
        _selectedIds = new HashSet<string>(ids);
        SelectedId = _selectedIds.LastOrDefault() ?? string.Empty;
        OnPropertyChanged(nameof(SelectedIds));
    }

    /// <summary>清空选择</summary>
    public void ClearSelection()
    {
        if (_selectedIds.Count == 0 && _selectedId.Length == 0) return;
        _selectedIds = new HashSet<string>();
        SelectedId = string.Empty;
        OnPropertyChanged(nameof(SelectedIds));
    }

    /// <summary>合并当前选择集合变更（拖动中属性变化用），返回是否非空</summary>
    public void NotifySelectionChanged() => OnPropertyChanged(nameof(SelectedIds));

    /// <summary>复制当前选中的元素（偏移 10,10，新 Id）。返回复制个数</summary>
    public int DuplicateSelected()
    {
        var originals = SelectedElements;
        if (originals.Count == 0) return 0;
        var copies = originals.Select(DuplicateElement).ToList();
        var list = new List<CanvasElement>(_elements);
        list.AddRange(copies);
        Elements = list;
        SelectMany(copies.Select(c => c.Id));
        return copies.Count;
    }

    /// <summary>深拷贝一个元素（Binary/SourceGray/Preview 缓存不复制，需重新渲染）</summary>
    private static CanvasElement DuplicateElement(CanvasElement src)
    {
        return new CanvasElement(src.Kind)
        {
            DotX = src.DotX + 10,
            DotY = src.DotY + 10,
            DotW = src.DotW,
            DotH = src.DotH,
            Rotation = src.Rotation,
            Aspect = src.Aspect,
            Locked = false,
            Invert = src.Invert,
            Text = src.Text,
            TextOptions = CopyTextOptions(src.TextOptions),
            EnhanceMode = src.EnhanceMode,
            ImageUri = src.ImageUri,
            DitherMode = src.DitherMode,
            ImageThreshold = src.ImageThreshold,
            FormulaLatex = src.FormulaLatex,
            CodeContent = src.CodeContent,
            CodeTypeIndex = src.CodeTypeIndex,
            TableRows = src.TableRows,
            TableCols = src.TableCols,
            TableData = src.TableData,
            TableColWeights = src.TableColWeights,
            TableFontSize = src.TableFontSize,
        };
    }

    private static RasterEncoder.TextRenderOptions CopyTextOptions(RasterEncoder.TextRenderOptions src) => new()
    {
        FontFamily = src.FontFamily,
        FontSize = src.FontSize,
        Bold = src.Bold,
        Italic = src.Italic,
        Underline = src.Underline,
        FontWeight = src.FontWeight,
        Alignment = src.Alignment,
        Vertical = src.Vertical,
        LetterSpacing = src.LetterSpacing,
        LineSpacing = src.LineSpacing,
        Margin = src.Margin,
    };

    /// <summary>
    /// 深拷贝快照（撤销/重做用）。
    /// Binary/SourceGray/Preview 均被 [JsonIgnore]，恢复后需重新渲染（RenderElement）。
    /// </summary>
    public CanvasDoc Snapshot()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<CanvasDoc>(json) ?? new CanvasDoc();
    }

    public void Add(CanvasElement element)
    {
        Elements = new List<CanvasElement>(_elements) { element };
        Select(element.Id);
    }

    public void Remove(string id)
    {
        var kept = new List<CanvasElement>();
        foreach (var el in _elements)
        {
            if (el.Id == id) el.Release();
            else kept.Add(el);
        }
        Elements = kept;
        _selectedIds.Remove(id);
        if (_selectedId == id) SelectedId = _selectedIds.LastOrDefault() ?? string.Empty;
        OnPropertyChanged(nameof(SelectedIds));
    }

    /// <summary>置顶:移到数组末尾。合成按数组顺序 blit,后面的盖住前面的</summary>
    public void ToTop(string id)
    {
        var list = new List<CanvasElement>(_elements);
        int idx = list.FindIndex(el => el.Id == id);
        if (idx < 0) return;
        var el = list[idx];
        list.RemoveAt(idx);
        list.Add(el);
        Elements = list;
    }

    /// <summary>上移一层：选中元素与紧邻其上的元素交换位置（多选整体移动，保持相对顺序）</summary>
    public void BringForward(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids);
        var list = new List<CanvasElement>(_elements);
        bool changed = false;
        for (int i = list.Count - 2; i >= 0; i--)
        {
            if (set.Contains(list[i].Id) && !set.Contains(list[i + 1].Id))
            {
                (list[i], list[i + 1]) = (list[i + 1], list[i]);
                changed = true;
            }
        }
        if (changed) Elements = list;
    }

    /// <summary>置底:移到数组开头,被其他元素盖住</summary>
    public void ToBottom(string id)
    {
        var list = new List<CanvasElement>(_elements);
        int idx = list.FindIndex(el => el.Id == id);
        if (idx < 0) return;
        var el = list[idx];
        list.RemoveAt(idx);
        list.Insert(0, el);
        Elements = list;
    }

    /// <summary>下移一层：选中元素与紧邻其下的元素交换位置（多选整体移动，保持相对顺序）</summary>
    public void SendBackward(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids);
        var list = new List<CanvasElement>(_elements);
        bool changed = false;
        for (int i = 1; i < list.Count; i++)
        {
            if (set.Contains(list[i].Id) && !set.Contains(list[i - 1].Id))
            {
                (list[i], list[i - 1]) = (list[i - 1], list[i]);
                changed = true;
            }
        }
        if (changed) Elements = list;
    }

    public CanvasElement? Find(string id)
    {
        foreach (var el in _elements)
        {
            if (el.Id == id) return el;
        }
        return null;
    }

    public CanvasElement? Selected() =>
        _selectedId.Length > 0 ? Find(_selectedId) : null;

    public void ReleaseAll()
    {
        foreach (var el in _elements) el.Release();
        Elements = new List<CanvasElement>();
        SelectedId = string.Empty;
        _selectedIds = new HashSet<string>();
        OnPropertyChanged(nameof(SelectedIds));
    }

    /// <summary>
    /// 画布当前长度(点)。
    /// 取「最靠下元素的底边 + 留白」和「最小长度」中的大者,所以拖到底部会自动延长。
    /// </summary>
    public int Height()
    {
        int bottom = BottomMost();
        int fitted = bottom > 0 ? bottom + CanvasModelConstants.CANVAS_BOTTOM_PAD : 0;
        return Math.Min(CanvasModelConstants.MAX_CANVAS_HEIGHT, Math.Max(_minLength, fitted));
    }

    /// <summary>
    /// 内容实际高度(点):最靠下元素的底边 + 留白,不含最小长度。
    /// 打印用这个而不是 Height():Height() 会把空白也铺出去,
    /// 内容不满一屏时打印会白白吐出一大段白纸。
    /// </summary>
    public int ContentHeight()
    {
        int bottom = BottomMost();
        return bottom > 0
            ? Math.Min(CanvasModelConstants.MAX_CANVAS_HEIGHT, bottom + CanvasModelConstants.CANVAS_BOTTOM_PAD)
            : 0;
    }

    /// <summary>内容是否已经超出高度上限被截断</summary>
    public bool Overflowed()
    {
        int bottom = BottomMost();
        return bottom + CanvasModelConstants.CANVAS_BOTTOM_PAD > CanvasModelConstants.MAX_CANVAS_HEIGHT;
    }

    private int BottomMost()
    {
        int bottom = 0;
        foreach (var el in _elements)
        {
            int elBottom = (int)(el.DotY + el.DotH);
            if (elBottom > bottom) bottom = elBottom;
        }
        return bottom;
    }

    /// <summary>新元素的默认落点:纵向落在现有内容下方,免得叠在一起</summary>
    public int NextInsertY()
    {
        int bottom = BottomMost();
        return bottom > 0 ? bottom + 8 : 8;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
