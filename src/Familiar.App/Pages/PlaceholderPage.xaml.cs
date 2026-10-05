using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Familiar.App.Pages;

public sealed record PlaceholderInfo(string Title, string Description);

public sealed partial class PlaceholderPage : Page
{
    public PlaceholderPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is PlaceholderInfo info)
        {
            TitleText.Text = info.Title;
            DescriptionText.Text = info.Description;
        }
    }
}
