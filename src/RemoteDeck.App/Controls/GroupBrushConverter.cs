using System.Globalization;
using System.Windows.Data;
using RemoteDeck.App.ViewModels;
using RemoteDeck.Core.Model;

namespace RemoteDeck.App.Controls;

/// <summary>
/// A group name to its brush from the sheet's group palette (<c>RdGroup0</c> to <c>RdGroup7</c>), or
/// <c>null</c> for no group. The pane's two built-in headers, Favorites and Ungrouped, are not
/// groups and get no colour either.
/// </summary>
internal sealed class GroupBrushConverter : IValueConverter
{
    /// <summary>How many <c>RdGroupN</c> brushes Theme.xaml defines.</summary>
    private const int PaletteSize = 8;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string;
        if (name == ConnectionListViewModel.FavoritesGroup || name == ConnectionListViewModel.UngroupedGroup) return null;
        return GroupHue.Of(name, PaletteSize) is { } hue ? System.Windows.Application.Current.TryFindResource($"RdGroup{hue}") : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A colour does not turn back into a group.");
}
