using System.Windows;

namespace RemoteDeck.App.Controls;

/// <summary>
/// <c>controls:Mark.Invalid="{Binding NameInvalid}"</c> on a field, and the sheet paints its edge in
/// the error colour — the one place the rule "the InfoBar lists the errors, the field shows which"
/// is written.
/// </summary>
/// <remarks>
/// An attached property rather than a style per field: the editors have six fields that can be
/// wrong, and six copies of the same trigger would be six places to drift. The style that reads
/// this property lives in <c>Theme.xaml</c>, derived from WPF-UI's own, so the field keeps its
/// template and only its border changes.
/// </remarks>
public static class Mark
{
    public static readonly DependencyProperty InvalidProperty = DependencyProperty.RegisterAttached(
        "Invalid", typeof(bool), typeof(Mark), new FrameworkPropertyMetadata(false));

    public static bool GetInvalid(DependencyObject element) => (bool)element.GetValue(InvalidProperty);

    public static void SetInvalid(DependencyObject element, bool value) => element.SetValue(InvalidProperty, value);
}
