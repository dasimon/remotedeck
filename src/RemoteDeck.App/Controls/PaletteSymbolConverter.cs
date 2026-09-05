using System.Globalization;
using System.Windows.Data;
using RemoteDeck.Core.Search;
using Wpf.Ui.Controls;

namespace RemoteDeck.App.Controls;

/// <summary>
/// The symbol a palette row shows: the one the item names, or one per kind when it names none.
/// </summary>
/// <remarks>
/// <see cref="PaletteItem.Icon"/> is a string because <c>Core</c> does not know the icon font; it is
/// parsed here, and a name the font does not have falls back rather than failing — a typo in a
/// symbol name must cost a generic glyph, not the palette.
/// </remarks>
internal sealed class PaletteSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PaletteItem item)
        {
            return SymbolRegular.Options24;
        }

        if (item.Icon.Length > 0 && Enum.TryParse<SymbolRegular>(item.Icon, ignoreCase: false, out var named))
        {
            return named;
        }

        return item.Kind switch
        {
            PaletteItemKind.Connection => SymbolRegular.Desktop24,
            PaletteItemKind.Session => SymbolRegular.TabDesktop24,
            _ => SymbolRegular.Options24,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A symbol does not turn back into a palette item.");
}
