using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QrintPrint.VirtualPrinter;

namespace QrintPrint.Views.Pages;

/// <summary>虚拟打印机的排版参数配置页（从首页进入）</summary>
public partial class VirtualPrinterSettingsPage : UserControl, IPage
{
    public string Title => "虚拟打印设置";

    /// <summary>控件初始化完成前不响应值变更事件（避免预置值触发保存）</summary>
    private bool _ready;

    public VirtualPrinterSettingsPage()
    {
        InitializeComponent();
        LoadValues();
        _ready = true;
    }

    /// <summary>把当前配置加载到控件（先于 _ready = true，避免事件触发）</summary>
    private void LoadValues()
    {
        FontSizeSlider.Value = VirtualPrinterPrefs.FontSize;
        LineSpacingSlider.Value = VirtualPrinterPrefs.LineSpacing;
        MarginSlider.Value = VirtualPrinterPrefs.Margin;
        MaxLinesSlider.Value = VirtualPrinterPrefs.MaxLines;
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        if (FontSizeLabel is not null)
            FontSizeLabel.Text = ((int)FontSizeSlider.Value).ToString();
        if (LineSpacingLabel is not null)
            LineSpacingLabel.Text = ((int)LineSpacingSlider.Value).ToString();
        if (MarginLabel is not null)
            MarginLabel.Text = ((int)MarginSlider.Value).ToString();
        if (MaxLinesLabel is not null)
        {
            int v = (int)MaxLinesSlider.Value;
            MaxLinesLabel.Text = v == 0 ? "0（不限制）" : v.ToString();
        }
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        RefreshLabels();
        VirtualPrinterPrefs.FontSize = (int)FontSizeSlider.Value;
        VirtualPrinterPrefs.Save();
    }

    private void LineSpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        RefreshLabels();
        VirtualPrinterPrefs.LineSpacing = (int)LineSpacingSlider.Value;
        VirtualPrinterPrefs.Save();
    }

    private void MarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        RefreshLabels();
        VirtualPrinterPrefs.Margin = (int)MarginSlider.Value;
        VirtualPrinterPrefs.Save();
    }

    private void MaxLinesSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        RefreshLabels();
        VirtualPrinterPrefs.MaxLines = (int)MaxLinesSlider.Value;
        VirtualPrinterPrefs.Save();
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateTo(new HomePage());
    }
}
