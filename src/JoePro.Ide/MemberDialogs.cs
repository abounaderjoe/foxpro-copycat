using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace JoePro.Ide;

/// <summary>
/// The Class and Form Designer dialogs: New Property / New Method, Edit Property/Method and Class Info.
/// They only collect input; the designer's methods do the work (and are what tests drive).
/// </summary>
public static class MemberDialogs
{
    private static readonly string[] Visibilities = ["Public", "Protected", "Hidden"];

    private static string? VisibilityValue(int index) => index switch { 1 => "PROTECTED", 2 => "HIDDEN", _ => null };
    private static int VisibilityIndex(string? v) => v switch { "PROTECTED" => 1, "HIDDEN" => 2, _ => 0 };

    private static Window Dialog(string title, Control content, double width = 420) => new()
    {
        Title = Strings.T(title),
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = new Border { Padding = new Thickness(12), Child = content },
    };

    private static void Show(FormDesigner designer, Window dialog)
    {
        if (TopLevel.GetTopLevel(designer) is Window owner) _ = dialog.ShowDialog(owner);
        else dialog.Show();
    }

    private static Control Row(string label, Control editor) =>
        new DockPanel { Margin = new Thickness(0, 3), Children = { new TextBlock { Text = Strings.T(label), Width = 110, VerticalAlignment = VerticalAlignment.Center }, editor } };

    /// <summary>New Property or New Method: Add keeps the dialog open for the next one, as in VFP.</summary>
    public static void NewMember(FormDesigner designer, bool isMethod)
    {
        var name = new TextBox();
        var visibility = new ComboBox { ItemsSource = Visibilities, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var initial = new TextBox { Text = ".F." };
        var access = new CheckBox { Content = Strings.T("Access method") };
        var assign = new CheckBox { Content = Strings.T("Assign method") };
        var description = new TextBox { AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
        var message = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var add = new Button { Content = Strings.T("Add"), IsDefault = true };
        var close = new Button { Content = Strings.T("Close"), IsCancel = true };
        var panel = new StackPanel
        {
            Children =
            {
                Row("Name", name),
                Row("Visibility", visibility),
            },
        };
        if (!isMethod)
        {
            panel.Children.Add(Row("Initial value", initial));
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(110, 3, 0, 3), Children = { access, assign } });
        }
        panel.Children.Add(Row("Description", description));
        panel.Children.Add(message);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { add, close } });
        var dialog = Dialog(isMethod ? "New Method" : "New Property", panel);
        add.Click += (_, _) =>
        {
            try
            {
                var n = name.Text?.Trim() ?? "";
                if (isMethod) designer.NewMethod(n, VisibilityValue(visibility.SelectedIndex), description.Text);
                else designer.NewProperty(n, (initial.Text ?? "").Trim().Length == 0 ? ".F." : initial.Text!.Trim(), VisibilityValue(visibility.SelectedIndex),
                    description.Text, access.IsChecked == true, assign.IsChecked == true);
                message.Text = "";
                name.Text = "";
                description.Text = "";
                access.IsChecked = assign.IsChecked = false;
                if (isMethod) dialog.Close();
                else name.Focus();
            }
            catch (ArgumentException ex) { message.Text = ex.Message; }
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => name.Focus();
        Show(designer, dialog);
    }

    /// <summary>Edit Property/Method: visibility, description and removal of the class's own members; inherited ones are listed read-only.</summary>
    public static void EditMembers(FormDesigner designer)
    {
        var list = new ListBox { Height = 220 };
        var visibility = new ComboBox { ItemsSource = Visibilities, HorizontalAlignment = HorizontalAlignment.Stretch };
        var description = new TextBox { AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
        var info = new TextBlock { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var apply = new Button { Content = Strings.T("Apply") };
        var remove = new Button { Content = Strings.T("Remove") };
        var code = new Button { Content = Strings.T("Edit code") };
        var close = new Button { Content = Strings.T("Close"), IsCancel = true };
        void Fill()
        {
            list.Items.Clear();
            foreach (var m in designer.CustomMembers())
                list.Items.Add(new ListBoxItem
                {
                    Content = $"{m.Name}   ({m.Kind.ToLowerInvariant()}{(m.Visibility != null ? ", " + m.Visibility.ToLowerInvariant() : "")}{(m.InheritedFrom != null ? ", from " + m.InheritedFrom : "")})",
                    Tag = m,
                    Opacity = m.InheritedFrom != null ? 0.6 : 1,
                });
        }
        FormDesigner.CustomMember? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as FormDesigner.CustomMember;
        list.SelectionChanged += (_, _) =>
        {
            var m = Selected();
            var own = m is { InheritedFrom: null };
            visibility.SelectedIndex = VisibilityIndex(m?.Visibility);
            description.Text = m?.Description ?? "";
            visibility.IsEnabled = description.IsEnabled = apply.IsEnabled = remove.IsEnabled = own;
            code.IsEnabled = m?.Kind == "Method";
            info.Text = m == null ? "" : m.InheritedFrom != null ? Strings.F("Defined in {0}.", m.InheritedFrom) : "";
        };
        apply.Click += (_, _) =>
        {
            if (Selected() is not { InheritedFrom: null } m) return;
            designer.Session.Transaction($"Edit {m.Name}", () =>
            {
                designer.SetMemberVisibility(m.Name, VisibilityValue(visibility.SelectedIndex));
                designer.SetMemberDescription(m.Name, description.Text);
            });
            Fill();
        };
        remove.Click += (_, _) =>
        {
            if (Selected() is not { InheritedFrom: null } m) return;
            designer.RemoveMember(m.Name);
            Fill();
        };
        var dialog = Dialog("Edit Property/Method", new StackPanel
        {
            Children =
            {
                list,
                Row("Visibility", visibility),
                Row("Description", description),
                info,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { code, apply, remove, close } },
            },
        }, 480);
        code.Click += (_, _) =>
        {
            if (Selected() is { Kind: "Method" } m) { designer.OpenMethod("", m.Name); dialog.Close(); }
        };
        close.Click += (_, _) => dialog.Close();
        Fill();
        Show(designer, dialog);
    }

    /// <summary>Class Info: description, toolbar and container icons, OLE public.</summary>
    public static void ClassInfo(FormDesigner designer)
    {
        var cls = designer.Session.Class;
        var description = new TextBox { AcceptsReturn = true, Height = 70, TextWrapping = TextWrapping.Wrap, Text = cls.Description ?? "" };
        var icon = new TextBox { Text = cls.Icon ?? "" };
        var containerIcon = new TextBox { Text = cls.ContainerIcon ?? "" };
        var olePublic = new CheckBox { Content = Strings.T("OLE public"), IsChecked = cls.OlePublic };
        var ok = new Button { Content = Strings.T("OK"), IsDefault = true };
        var cancel = new Button { Content = Strings.T("Cancel"), IsCancel = true };
        var panel = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = Strings.F("{0}  ·  based on {1}", cls.Name, cls.ParentClass + (cls.ParentLibrary != null ? Strings.F(" of {0}", cls.ParentLibrary) : "")), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) },
                Row("Description", description),
                Row("Toolbar icon", icon),
                Row("Container icon", containerIcon),
                olePublic,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { ok, cancel } },
            },
        };
        var dialog = Dialog("Class Info", panel);
        ok.Click += (_, _) =>
        {
            designer.SetClassInfo(description.Text, icon.Text, containerIcon.Text, olePublic.IsChecked == true);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        Show(designer, dialog);
    }
}
