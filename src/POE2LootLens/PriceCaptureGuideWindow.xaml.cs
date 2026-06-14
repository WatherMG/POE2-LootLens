using System.Windows;
using MahApps.Metro.Controls;

namespace Poe2LootLens;

internal partial class PriceCaptureGuideWindow : MetroWindow
{
    public PriceCaptureGuideWindow(string uiLanguage)
    {
        InitializeComponent();
        bool english = UiLanguage.IsEnglish(uiLanguage);
        if (!english)
            return;

        Title = "How to select the scanner area";
        HeaderText.Text = "Price scanner capture area";
        DescriptionText.Text =
            "Select only the light reward table: exclude the book frame, scrollbar and Atlas background.";
        TipsText.Text =
            "The green frame should contain complete rows. The top and bottom edges may follow row separators; do not include a partially visible next row.";
        CloseButton.Content = "Close";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
