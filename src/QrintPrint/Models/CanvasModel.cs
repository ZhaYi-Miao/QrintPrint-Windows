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
using System.Text.Json.Serialization;
using QrintPrint.Bluetooth;

namespace QrintPrint.Models;

public enum ElementKind
{
    TEXT = 0,
    IMAGE = 1,
    CODE = 2,
    FORMULA = 3,
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
    private double _aspect = 1;
    private bool _geometryLocked;
    private bool _rendering;

    // 文字元素
    private string _text = string.Empty;
    private RasterEncoder.TextRenderOptions _textOptions = RasterEncoder.DefaultTextOptions;

    // 图片元素
    private string _imageUri = string.Empty;
    private DitherMode _ditherMode = DitherMode.FLOYD_STEINBERG;

    // 公式元素
    private string _formulaLatex = string.Empty;

    // 条码元素
    private string _codeContent = string.Empty;
    private int _codeTypeIndex;

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
    public double Aspect { get => _aspect; set => SetField(ref _aspect, value); }
    public bool GeometryLocked { get => _geometryLocked; set => SetField(ref _geometryLocked, value); }
    public bool Rendering { get => _rendering; set => SetField(ref _rendering, value); }

    public string Text { get => _text; set => SetField(ref _text, value); }
    public RasterEncoder.TextRenderOptions TextOptions
    {
        get => _textOptions;
        set => SetField(ref _textOptions, value);
    }

    public string ImageUri { get => _imageUri; set => SetField(ref _imageUri, value); }
    public DitherMode DitherMode { get => _ditherMode; set => SetField(ref _ditherMode, value); }

    public string FormulaLatex { get => _formulaLatex; set => SetField(ref _formulaLatex, value); }

    public string CodeContent { get => _codeContent; set => SetField(ref _codeContent, value); }
    public int CodeTypeIndex { get => _codeTypeIndex; set => SetField(ref _codeTypeIndex, value); }

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

    public void Add(CanvasElement element)
    {
        Elements = new List<CanvasElement>(_elements) { element };
        SelectedId = element.Id;
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
        if (_selectedId == id) SelectedId = string.Empty;
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

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
