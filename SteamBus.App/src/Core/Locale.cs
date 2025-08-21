

static class Locale
{
    public static string LocaleToSteamCode(string locale)
    {
        return locale switch
        {
            "en-US" => "english",
            "fr-FR" => "french",
            "de-DE" => "german",
            "it-IT" => "italian",
            "ko-KR" => "korean",
            "pl-PL" => "polish",
            "pt-BR" => "portugese",
            "ru-RU" => "russian",
            "zh-CN" => "schinese",
            "zh-TW" => "tchinese",
            "es-ES" => "spanish",
            "ja-JP" => "japanese",
            "ar" => "arabic",
            "bg-BG" => "bulgarian",
            "el-GR" => "greek",
            _ => "english",
        };
    }
}
