using System.ComponentModel;
using System.Diagnostics;
using Familiar.App.Pages;
using Familiar.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Familiar.App;

public sealed partial class MainWindow : Window
{
    private const int ErrorCancelled = 1223;

    private static readonly Dictionary<string, PlaceholderInfo> Sections = new()
    {
        ["Source"] = new("Source image", "Choose a WIM, ESD or ISO and the index to service."),
        ["Updates"] = new("Updates", "Download cumulative, checkpoint, .NET and Setup Dynamic Updates, then apply them in order."),
        ["Languages"] = new("Languages", "Add language packs and language features, and set the default UI language."),
        ["Drivers"] = new("Drivers", "Inject driver folders into the image."),
        ["Apps"] = new("Apps", "Remove provisioned apps, read from the image itself."),
        ["Customizations"] = new("Customizations", "Autopilot JSON, registry files, .NET 3.5 and custom scripts."),
        ["ConfigMgr"] = new("Configuration Manager", "Create or update OS image packages and distribute content."),
        ["Build"] = new("Build", "Run the build, export the WIM, and optionally create an ISO."),
    };

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Familiar.ico"));

        ElevationBar.IsOpen = !ProcessElevation.IsElevated;
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag && Sections.TryGetValue(tag, out var section))
        {
            ContentFrame.Navigate(typeof(PlaceholderPage), section);
        }
    }

    private void RestartElevated_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return;
        }

        Application.Current.Exit();
    }
}
