using System.Globalization;
using System.Resources;
using System.Threading;

namespace MobilePackageGen.GUI
{
    public static class Lang
    {
        private static ResourceManager? _rm;
        private static string _currentLanguage = "zh-cn";

        public static readonly (string Code, string DisplayName)[] SupportedLanguages =
        {
            ("zh-cn", "简体中文"),
            ("zh-tw", "繁體中文"),
            ("en-us", "English (US)"),
            ("ja-jp", "日本語"),
            ("ru-ru", "Русский"),
            ("ko-kr", "한국어")
        };

        public static string CurrentLanguage => _currentLanguage;

        public static void Init()
        {
            _rm = new ResourceManager("MobilePackageGen.GUI.Strings", typeof(Lang).Assembly);
            SetLanguage(_currentLanguage);
        }

        public static void SetLanguage(string code)
        {
            _currentLanguage = code;
            try
            {
                var culture = new CultureInfo(code.Replace("-", "-"));
                Thread.CurrentThread.CurrentUICulture = culture;
                Thread.CurrentThread.CurrentCulture = culture;
            }
            catch { }
        }

        public static string GetString(string key)
        {
            if (_rm == null) Init();
            try
            {
                var val = _rm!.GetString(key);
                return val ?? key;
            }
            catch
            {
                return key;
            }
        }

        public static string GetLanguageDisplayName(string code)
        {
            foreach (var (c, name) in SupportedLanguages)
            {
                if (c.Equals(code, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
            return code;
        }
    }
}
