using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace YabeMac
{
    /// <summary>Maps an avares:// icon uri (see MainWindow.IconGlyph) to the original image-list bitmap.</summary>
    public class IconConverter : IValueConverter
    {
        public static readonly IconConverter Instance = new IconConverter();
        static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is string uri) || uri.Length == 0) return null;
            if (!_cache.TryGetValue(uri, out var bmp))
                _cache[uri] = bmp = new Bitmap(AssetLoader.Open(new Uri(uri)));
            return bmp;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
