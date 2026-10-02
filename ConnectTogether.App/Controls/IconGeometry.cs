// ConnectTogether.App/Controls/IconGeometry.cs

using System.Collections.Generic;
using System.Windows.Media;

namespace ConnectTogether.App.Controls
{
    /// <summary>
    /// Контурные иконки интерфейса: сетка 24×24, линия 1,8, скруглённые концы.
    /// Пути перенесены из макета (CT Icon) без изменений.
    /// </summary>
    public static class IconGeometry
    {
        private const string Circle = "M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0z";

        private static readonly Dictionary<string, string> Paths = new()
        {
            ["cube"] = "M21 7.5 12 3 3 7.5v9L12 21l9-4.5z M3 7.5 12 12l9-4.5 M12 12v9",
            ["axe"] = "m14 12-8.5 8.5a2.1 2.1 0 1 1-3-3L11 9 M15 13 9 7l4-4 6 6h3a8 8 0 0 1-7 7z",
            ["plus"] = "M12 5v14 M5 12h14",
            ["enter"] = "M14 3h5a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-5 M9 17l5-5-5-5 M14 12H3",
            ["copy"] = "M9 8h10a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H9a1 1 0 0 1-1-1V9a1 1 0 0 1 1-1z M16 8V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3",
            ["check"] = "M20 6 9 17l-5-5",
            ["checkCircle"] = Circle + " M8 12.5l2.8 2.8L16 9.5",
            ["alert"] = "M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z M12 9v4 M12 17h.01",
            ["xCircle"] = Circle + " M15 9l-6 6 M9 9l6 6",
            ["info"] = Circle + " M12 16v-4 M12 8h.01",
            ["home"] = "M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z",
            ["puzzle"] = "M10 5V4a2 2 0 1 1 4 0v1h4a1 1 0 0 1 1 1v4h-1a2 2 0 1 0 0 4h1v4a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1v-4h1a2 2 0 1 0 0-4H5V6a1 1 0 0 1 1-1z",
            ["sliders"] = "M4 6h9 M17 6h3 M4 12h3 M11 12h9 M4 18h11 M19 18h1 M15 4v4 M9 10v4 M17 16v4",
            ["send"] = "M21 3 10 14 M21 3l-6.5 18-4.5-7-7-4.5z",
            ["users"] = "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z M22 21v-2a4 4 0 0 0-3-3.9 M16 3.1a4 4 0 0 1 0 7.8",
            ["userPlus"] = "M15 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M8.5 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z M19 8v6 M16 11h6",
            ["refresh"] = "M21 12a9 9 0 1 1-2.6-6.4L21 8 M21 3v5h-5",
            ["chevronDown"] = "M6 9l6 6 6-6",
            ["chevronRight"] = "M9 6l6 6-6 6",
            ["arrowLeft"] = "M19 12H5 M12 19l-7-7 7-7",
            ["arrowRight"] = "M5 12h14 M12 5l7 7-7 7",
            ["folder"] = "M3 6.5a1.5 1.5 0 0 1 1.5-1.5H9l2 2.5h8.5A1.5 1.5 0 0 1 21 9v9.5a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5z",
            ["clipboard"] = "M9 3h6v4H9z M9 5H6a1 1 0 0 0-1 1v14a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V6a1 1 0 0 0-1-1h-3",
            ["play"] = "M7 4.5v15l12.5-7.5z",
            ["clock"] = Circle + " M12 7v5l3 2",
            ["message"] = "M21 12a8.5 8.5 0 0 1-12.3 7.6L3 21l1.4-5.4A8.5 8.5 0 1 1 21 12z",
            ["wifiOff"] = "M3 3l18 18 M8.5 16.4a5 5 0 0 1 7 0 M5 12.9a10 10 0 0 1 5-2.7 M19 12.9a10 10 0 0 0-2.4-1.6 M2 8.8a15 15 0 0 1 4.3-2.7 M22 8.8A15 15 0 0 0 10.8 5 M12 20h.01",
            ["server"] = "M4 4h16a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1z M4 14h16a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1z M7 7h.01 M7 17h.01",
            ["phone"] = "M8 2h8a1 1 0 0 1 1 1v18a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1z M11 18h2",
            ["router"] = "M3 14h18v6H3z M7 17h.01 M11 17h.01 M8 10a6 6 0 0 1 8 0 M5 7a10 10 0 0 1 14 0",
            ["file"] = "M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8z M14 3v5h5 M12 12v6 M9 15h6",
            ["sun"] = "M12 3V1.5 M12 22.5V21 M3 12H1.5 M22.5 12H21 M5.6 5.6 4.6 4.6 M19.4 19.4l-1-1 M5.6 18.4l-1 1 M19.4 4.6l-1 1 M12 16.5a4.5 4.5 0 1 0 0-9 4.5 4.5 0 0 0 0 9z",
            ["moon"] = "M20.5 14A8.5 8.5 0 1 1 10 3.5a6.8 6.8 0 0 0 10.5 10.5z",
            ["monitor"] = "M3 4h18v12H3z M8 21h8 M12 16v5",
            ["logout"] = "M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4 M16 17l5-5-5-5 M21 12H9",
            ["link"] = "M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7 M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7",
            ["min"] = "M6 12h12",
            ["max"] = "M6 6h12v12H6z",
            ["restore"] = "M8 8h10v10H8z M6 15.5V6h9.5",
            ["close"] = "M6 6l12 12 M18 6 6 18",
            ["more"] = "M5 12h.01 M12 12h.01 M19 12h.01",
            ["keyboard"] = "M3 6h18v12H3z M7 10h.01 M11 10h.01 M15 10h.01 M7 14h10",
            ["hourglass"] = "M6 2h12 M6 22h12 M7 2v4l5 6 5-6V2 M7 22v-4l5-6 5 6v4",
            ["search"] = "M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14z M21 21l-5-5",
            ["external"] = "M14 4h6v6 M20 4l-9 9 M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5",
            ["dot"] = "M12 12h.01",
        };

        private static readonly Dictionary<string, Geometry> Cache = new();

        /// <summary>Геометрия иконки по имени; для неизвестного имени — точка.</summary>
        public static Geometry Get(string? name)
        {
            name ??= "dot";
            if (Cache.TryGetValue(name, out var geometry)) return geometry;

            geometry = Geometry.Parse(Paths.TryGetValue(name, out var path) ? path : Paths["dot"]);
            geometry.Freeze();
            Cache[name] = geometry;
            return geometry;
        }
    }
}
