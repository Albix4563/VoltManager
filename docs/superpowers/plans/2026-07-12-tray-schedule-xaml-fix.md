# Correzione XAML menu tray pianificazione Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminare l'eccezione d'avvio causata dal separatore nel sottomenu di pianificazione del tray.

**Architecture:** Il menu esistente viene mantenuto. Il solo `Separator` nel sottomenu viene eliminato, così `LocalizeTrayMenu()` riceve esclusivamente `MenuItem`, come richiede l'enumerazione tipizzata. Un test di regressione legge il markup e vieta elementi diversi da `MenuItem` in quel sottomenu.

**Tech Stack:** .NET 8, WPF XAML, xUnit.

## Global Constraints

- Modificare soltanto il menu tray pianificazione e il test di regressione.
- Non aggiungere dipendenze, filtri runtime o refactoring.
- Il sottomenu `TraySchedulePowerItem` deve contenere esclusivamente `MenuItem`.

---

### Task 1: Rimuovere il separatore incompatibile

**Files:**
- Modify: `src/VoltManager/MainWindow.xaml:51-77`
- Create: `tests/VoltManager.Tests/MainWindowXamlTests.cs`

**Interfaces:**
- Consumes: `MainWindow.xaml`, dove `TraySchedulePowerItem.Items` viene enumerato come `System.Windows.Controls.MenuItem` in `MainWindow.LocalizeTrayMenu()`.
- Produces: markup del sottomenu senza `Separator`, compatibile con la localizzazione esistente.

- [ ] **Step 1: Scrivere il test fallente**

Creare `tests/VoltManager.Tests/MainWindowXamlTests.cs`:

```csharp
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
```

- [ ] **Step 2: Eseguire il test e verificare il fallimento**

Run: `dotnet test tests\VoltManager.Tests\VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~MainWindowXamlTests`

Expected: FAIL perché `TraySchedulePowerItem` contiene `<Separator />`.

- [ ] **Step 3: Applicare la modifica minima**

In `src/VoltManager/MainWindow.xaml`, rimuovere esclusivamente questa riga dal contenuto di `TraySchedulePowerItem`:

```xml
<Separator />
```

La voce `TrayScheduleCustomItem` deve restare immediatamente dopo il gruppo di preset.

- [ ] **Step 4: Eseguire regressione, suite e build**

Run: `dotnet test -c Release`

Expected: exit code 0, nessun test fallito.

Run: `dotnet build src\VoltManager\VoltManager.csproj -c Release --no-restore`

Expected: exit code 0, 0 errori.

- [ ] **Step 5: Commit**

```powershell
git add src/VoltManager/MainWindow.xaml tests/VoltManager.Tests/MainWindowXamlTests.cs
git commit -m "fix(tray): prevent schedule menu startup crash"
```
