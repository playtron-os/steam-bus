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
            "ko-KR" => "koreana",
            "pl-PL" => "polish",
            "pt-BR" => "portuguese",
            "ru-RU" => "russian",
            "zh-CN" => "schinese",
            "zh-TW" => "tchinese",
            "es-ES" => "spanish",
            "ja-JP" => "japanese",

            "arabic" => "ar",
            "bg-BG" => "bulgarian",
            "cs-CZ" => "czech",
            "da-DK" => "danish",
            "nl-NL" => "dutch",
            "fi-FI" => "finnish",
            "el-GR" => "greek",
            "hu-HU" => "hungarian",
            "id-ID" => "indonesian",
            "no-NO" => "norwegian",
            // "pt-BR" => "brazilian",
            "ro-RO" => "romanian",
            "es-419" => "latam",
            "sv-SE" => "swedish",
            "th-TH" => "thai",
            "tr-TR" => "turkish",
            "uk-UA" => "ukrainian",
            "vi-VN" => "vietnamese",
            _ => "english",
        };
    }

    public static string? SteamCodeToLocale(string? steamCode)
    {
        return steamCode switch
        {
            "english" => "en-US",
            "french" => "fr-FR",
            "german" => "de-DE",
            "italian" => "it-IT",
            "koreana" => "ko-KR",
            "polish" => "pl-PL",
            "portuguese" => "pt-BR",
            "brazilian" => "pt-BR",
            "russian" => "ru-RU",
            "schinese" => "zh-CN",
            "tchinese" => "zh-TW",
            "spanish" => "es-ES",
            "japanese" => "ja-JP",

            "arabic" => "ar",
            "bulgarian" => "bg-BG",
            "czech" => "cs-CZ",
            "danish" => "da-DK",
            "dutch" => "nl-NL",
            "finnish" => "fi-FI",
            "greek" => "el-GR",
            "hungarian" => "hu-HU",
            "indonesian" => "id-ID",
            "norwegian" => "no-NO",
            "romanian" => "ro-RO",
            "latam" => "es-419",
            "swedish" => "sv-SE",
            "thai" => "th-TH",
            "turkish" => "tr-TR",
            "ukrainian" => "uk-UA",
            "vietnamese" => "vi-VN",
            _ => null
        };
    }
}
