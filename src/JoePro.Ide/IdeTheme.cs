using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace JoePro.Ide;

/// <summary>
/// The IDE's look: a soft dark theme (charcoal, not black) and a light theme, both with outlined, titled panels so
/// each area of the window stands apart. The chosen theme is remembered between sessions.
/// </summary>
public static class IdeTheme
{
    // Colors shared by both themes' panels. Keys are resolved dynamically, so switching theme repaints everything.
    public const string WindowBackground = "JpWindowBackground";
    public const string PanelBackground = "JpPanelBackground";
    public const string PanelBorder = "JpPanelBorder";
    public const string PanelHeader = "JpPanelHeader";
    public const string PanelHeaderText = "JpPanelHeaderText";

    public static FluentTheme CreateFluent() => new()
    {
        Palettes =
        {
            // Softer than Fluent's default near-black dark theme.
            [ThemeVariant.Dark] = new ColorPaletteResources
            {
                Accent = Color.Parse("#4C9BE8"),
                RegionColor = Color.Parse("#2B2E34"),
                AltHigh = Color.Parse("#2B2E34"),
                AltMediumHigh = Color.Parse("#33363D"),
                ChromeLow = Color.Parse("#3A3E46"),
                ChromeMedium = Color.Parse("#3F434B"),
                ChromeMediumLow = Color.Parse("#383B42"),
                ChromeHigh = Color.Parse("#5A5F69"),
                BaseHigh = Color.Parse("#E6E8EC"),
                BaseMediumHigh = Color.Parse("#C9CDD4"),
                BaseMedium = Color.Parse("#A4A9B2"),
                ListLow = Color.Parse("#3A3E46"),
                ListMedium = Color.Parse("#454A53"),
            },
            [ThemeVariant.Light] = new ColorPaletteResources
            {
                Accent = Color.Parse("#2B6CB0"),
                RegionColor = Color.Parse("#F3F4F6"),
                AltHigh = Color.Parse("#FFFFFF"),
                ChromeLow = Color.Parse("#E9EBEF"),
                ChromeMedium = Color.Parse("#E2E5EA"),
                ChromeMediumLow = Color.Parse("#EEF0F3"),
                BaseHigh = Color.Parse("#1F2328"),
                ListLow = Color.Parse("#EEF1F5"),
                ListMedium = Color.Parse("#E1E6ED"),
            },
        },
    };

    public static void AddResources(Application app)
    {
        var dark = new ResourceDictionary
        {
            [WindowBackground] = new SolidColorBrush(Color.Parse("#25282D")),
            [PanelBackground] = new SolidColorBrush(Color.Parse("#2E3137")),
            [PanelBorder] = new SolidColorBrush(Color.Parse("#474C55")),
            [PanelHeader] = new SolidColorBrush(Color.Parse("#373B42")),
            [PanelHeaderText] = new SolidColorBrush(Color.Parse("#D6D9DE")),
        };
        var light = new ResourceDictionary
        {
            [WindowBackground] = new SolidColorBrush(Color.Parse("#E8EAEE")),
            [PanelBackground] = new SolidColorBrush(Color.Parse("#FFFFFF")),
            [PanelBorder] = new SolidColorBrush(Color.Parse("#C9CED6")),
            [PanelHeader] = new SolidColorBrush(Color.Parse("#F2F4F7")),
            [PanelHeaderText] = new SolidColorBrush(Color.Parse("#333840")),
        };
        app.Resources.ThemeDictionaries[ThemeVariant.Dark] = dark;
        app.Resources.ThemeDictionaries[ThemeVariant.Light] = light;
    }

    /// <summary>An outlined panel with a title strip: how the IDE separates its areas.</summary>
    public static Border Card(string? title, Control content, Control? actions = null) =>
        Card(title == null ? null : new TextBlock { Text = title }, content, actions);

    /// <summary>An outlined panel whose title strip holds a control (a title that changes, say).</summary>
    public static Border Card(TextBlock? titleText, Control content, Control? actions = null)
    {
        var body = new DockPanel();
        if (titleText is { Text: { Length: > 0 } name } && string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(content)))
            Avalonia.Automation.AutomationProperties.SetName(content, name); // a list or editor is announced by its panel's title
        if (titleText != null)
        {
            var text = titleText;
            text.FontWeight = FontWeight.SemiBold;
            text.FontSize = 12;
            text.Margin = new Thickness(0);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(PanelHeaderText));
            var strip = new DockPanel { Children = { text } };
            if (actions != null)
            {
                DockPanel.SetDock(actions, Dock.Right);
                strip.Children.Insert(0, actions);
            }
            var header = new Border { Child = strip, Padding = new Thickness(10, 5), BorderThickness = new Thickness(0, 0, 0, 1) };
            header.Bind(Border.BackgroundProperty, header.GetResourceObservable(PanelHeader));
            header.Bind(Border.BorderBrushProperty, header.GetResourceObservable(PanelBorder));
            DockPanel.SetDock(header, Dock.Top);
            body.Children.Add(header);
        }
        body.Children.Add(content);
        var card = new Border { Child = body, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, Margin = new Thickness(4) };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable(PanelBackground));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable(PanelBorder));
        return card;
    }

    // ---- The remembered choice -----------------------------------------------------------------

    /// <summary>Where the IDE keeps its settings (tests point it elsewhere).</summary>
    public static string SettingsPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Joe Pro", "ide-settings.json");

    /// <summary>The saved theme: Light, Dark, or null to follow the system.</summary>
    public static ThemeVariant LoadVariant()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return ThemeVariant.Default;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return doc.RootElement.TryGetProperty("theme", out var t) ? t.GetString() switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            } : ThemeVariant.Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return ThemeVariant.Default; }
    }

    public static void SaveVariant(ThemeVariant v)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var name = v == ThemeVariant.Light ? "light" : v == ThemeVariant.Dark ? "dark" : "system";
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Dictionary<string, string> { ["theme"] = name }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // the choice still applies to this session
    }
}
