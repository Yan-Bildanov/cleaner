using System.Globalization;

namespace Cleaner.Core;

public static class Format
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>1536 → "1,5 КБ", 734003200 → "700 МБ", 1288490188 → "1,2 ГБ".</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        var digits = unit == 0 || value >= 100 ? "0" : unit >= 3 ? "0.##" : "0.#";   // ГБ — до сотых: «1,03 ГБ»
        return value.ToString(digits, Ru) + " " + units[unit];
    }
}
