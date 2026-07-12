using System.IO;
using System.Xml.Linq;

namespace VoltManager.Tests;

public class MainWindowXamlTests
{
    [Fact]
    public void ScheduleTraySubmenuContainsOnlyMenuItems()
    {
        string root = FindRepositoryRoot();
        var xaml = XDocument.Load(Path.Combine(root, "src", "VoltManager", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var scheduleMenu = xaml.Descendants(presentation + "MenuItem")
            .Single(item => (string?)item.Attribute(x + "Name") == "TraySchedulePowerItem");

        Assert.All(scheduleMenu.Elements(), item => Assert.Equal("MenuItem", item.Name.LocalName));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VoltManager.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("VoltManager.sln non trovato.");
    }
}
