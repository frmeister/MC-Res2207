// ConnectTogether.App/Services/RussianText.cs

using System.Globalization;

namespace ConnectTogether.App.Services
{
    /// <summary>Склонения и даты для текстов интерфейса.</summary>
    public static class RussianText
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        /// <summary>Форма слова для числа: 1 игрок, 2 игрока, 5 игроков.</summary>
        public static string Plural(int n, string one, string few, string many)
        {
            int mod100 = Math.Abs(n) % 100, mod10 = mod100 % 10;
            if (mod100 is >= 11 and <= 14) return many;
            return mod10 switch
            {
                1 => one,
                >= 2 and <= 4 => few,
                _ => many,
            };
        }

        /// <summary>
        /// Родительный падеж имени для «Комната Лёши», «комната Марины».
        /// Работает для обычных русских имён; ники и латиницу оставляет как есть.
        /// </summary>
        public static string Genitive(string name)
        {
            if (name.Length < 2 || !name.All(c => c is >= 'А' and <= 'я' or 'ё' or 'Ё')) return name;

            char last = char.ToLowerInvariant(name[^1]);
            char prev = char.ToLowerInvariant(name[^2]);
            string stem = name[..^1];
            return last switch
            {
                'а' when "гкхжчшщ".Contains(prev) => stem + "и",
                'а' => stem + "ы",
                'я' => stem + "и",
                'й' or 'ь' => stem + "я",
                _ when "бвгдзклмнпрстфхжчшщ".Contains(last) => name + "а",
                _ => name,
            };
        }

        /// <summary>«сегодня, 22:10», «вчера, 22:10», «2 дня назад», «28 сентября».</summary>
        public static string RelativeTime(DateTime time, DateTime now)
        {
            int days = (now.Date - time.Date).Days;
            return days switch
            {
                <= 0 => $"сегодня, {time:HH:mm}",
                1 => $"вчера, {time:HH:mm}",
                < 7 => $"{days} {Plural(days, "день", "дня", "дней")} назад",
                _ => time.ToString(time.Year == now.Year ? "d MMMM" : "d MMMM yyyy", Ru),
            };
        }

        /// <summary>«1 ч 12 мин», «5 мин», «меньше минуты».</summary>
        public static string Duration(TimeSpan span)
        {
            if (span.TotalMinutes < 1) return "меньше минуты";
            int hours = (int)span.TotalHours;
            return hours > 0 ? $"{hours} ч {span.Minutes} мин" : $"{span.Minutes} мин";
        }
    }
}
