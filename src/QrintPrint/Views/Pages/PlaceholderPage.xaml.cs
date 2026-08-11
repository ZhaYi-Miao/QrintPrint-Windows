using System.Windows.Controls;

namespace QrintPrint.Views.Pages;

public partial class PlaceholderPage : UserControl, IPage
{
    public string Title { get; }
    public string Subtitle { get; }

    public PlaceholderPage(string title, string subtitle)
    {
        InitializeComponent();
        Title = title;
        Subtitle = subtitle;
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
    }
}
