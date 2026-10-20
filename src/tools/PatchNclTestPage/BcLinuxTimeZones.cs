using System;
using System.Collections.ObjectModel;

/// <summary>
/// Source of the time zone list the "culture" patch gives the virtual table "Time Zone"
/// (2000000164, issue #132).
///
/// BC fills that table from TimeZoneInfo.GetSystemTimeZones(), and the table only reads each
/// zone's Id and DisplayName. On Windows that is the registry's Windows zones; on Linux it is
/// every IANA zone ICU knows (419 on BC 29), so AL code that looks up 'W. Europe Standard Time'
/// found nothing and the numbering matched nothing. PatchCulture swaps the call for
/// WindowsTimeZones() below.
///
/// The list is what .NET's GetSystemTimeZones() returns in mcr.microsoft.com/windows/servercore
/// :ltsc2025, build 10.0.26100.33650 — the OS build of the official BC containers the issue was
/// measured on (BC 27.5 and 29.0 both list these 144 zones, in this order). Each entry is
/// "Id|BaseUtcOffset in minutes|DisplayName", in Windows' order (BaseUtcOffset, then DisplayName
/// ordinal). Windows adds a zone now and then through a cumulative update; when the corpus's
/// Windows run reports a different count, regenerate this list from that build.
///
/// The zones are custom zones with the Windows offset and no daylight rules. They never leave
/// the table's iterator: nothing but Id and DisplayName is read from them. Converting times
/// still goes through FindSystemTimeZoneById, which .NET resolves from a Windows id on Linux too.
///
/// Same rules as BcLinuxCulture.cs: the class is copied into Nav.Ncl.dll as IL, so static
/// methods only, framework calls only, no lambdas, static fields or try/catch.
/// </summary>
internal static class BcLinuxTimeZones
{
    internal static ReadOnlyCollection<TimeZoneInfo> WindowsTimeZones()
    {
        string[] zones = Zones();
        TimeZoneInfo[] result = new TimeZoneInfo[zones.Length];
        for (int i = 0; i < zones.Length; i++)
        {
            string entry = zones[i];
            int first = entry.IndexOf('|');
            int second = entry.IndexOf('|', first + 1);
            string id = entry.Substring(0, first);
            int minutes = int.Parse(entry.Substring(first + 1, second - first - 1), System.Globalization.CultureInfo.InvariantCulture);
            string displayName = entry.Substring(second + 1);
            result[i] = TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromMinutes(minutes), displayName, displayName);
        }
        return new ReadOnlyCollection<TimeZoneInfo>(result);
    }

    private static string[] Zones()
    {
        return new string[]
        {
            "Dateline Standard Time|-720|(UTC-12:00) International Date Line West",
            "UTC-11|-660|(UTC-11:00) Coordinated Universal Time-11",
            "Aleutian Standard Time|-600|(UTC-10:00) Aleutian Islands",
            "Hawaiian Standard Time|-600|(UTC-10:00) Hawaii",
            "Marquesas Standard Time|-570|(UTC-09:30) Marquesas Islands",
            "Alaskan Standard Time|-540|(UTC-09:00) Alaska",
            "UTC-09|-540|(UTC-09:00) Coordinated Universal Time-09",
            "British Columbia Standard Time|-480|(UTC-07:00) Pacific Time (British Columbia)",
            "Pacific Standard Time (Mexico)|-480|(UTC-08:00) Baja California",
            "UTC-08|-480|(UTC-08:00) Coordinated Universal Time-08",
            "Pacific Standard Time|-480|(UTC-08:00) Pacific Time (US & Canada)",
            "Alberta Standard Time|-420|(UTC-06:00) Mountain Time (Alberta)",
            "US Mountain Standard Time|-420|(UTC-07:00) Arizona",
            "Mountain Standard Time (Mexico)|-420|(UTC-07:00) La Paz, Mazatlan",
            "Mountain Standard Time|-420|(UTC-07:00) Mountain Time (US & Canada)",
            "Yukon Standard Time|-420|(UTC-07:00) Yukon",
            "Manitoba Standard Time|-360|(UTC-05:00) Manitoba",
            "Central America Standard Time|-360|(UTC-06:00) Central America",
            "Central Standard Time|-360|(UTC-06:00) Central Time (US & Canada)",
            "Easter Island Standard Time|-360|(UTC-06:00) Easter Island",
            "Central Standard Time (Mexico)|-360|(UTC-06:00) Guadalajara, Mexico City, Monterrey",
            "Canada Central Standard Time|-360|(UTC-06:00) Saskatchewan",
            "SA Pacific Standard Time|-300|(UTC-05:00) Bogota, Lima, Quito, Rio Branco",
            "Eastern Standard Time (Mexico)|-300|(UTC-05:00) Chetumal",
            "Eastern Standard Time|-300|(UTC-05:00) Eastern Time (US & Canada)",
            "Haiti Standard Time|-300|(UTC-05:00) Haiti",
            "Cuba Standard Time|-300|(UTC-05:00) Havana",
            "US Eastern Standard Time|-300|(UTC-05:00) Indiana (East)",
            "Turks And Caicos Standard Time|-300|(UTC-05:00) Turks and Caicos",
            "Atlantic Standard Time|-240|(UTC-04:00) Atlantic Time (Canada)",
            "Venezuela Standard Time|-240|(UTC-04:00) Caracas",
            "Central Brazilian Standard Time|-240|(UTC-04:00) Cuiaba",
            "SA Western Standard Time|-240|(UTC-04:00) Georgetown, La Paz, Manaus, San Juan",
            "Pacific SA Standard Time|-240|(UTC-04:00) Santiago",
            "Newfoundland Standard Time|-210|(UTC-03:30) Newfoundland",
            "Tocantins Standard Time|-180|(UTC-03:00) Araguaina",
            "Paraguay Standard Time|-180|(UTC-03:00) Asuncion",
            "E. South America Standard Time|-180|(UTC-03:00) Brasilia",
            "SA Eastern Standard Time|-180|(UTC-03:00) Cayenne, Fortaleza",
            "Argentina Standard Time|-180|(UTC-03:00) City of Buenos Aires",
            "Montevideo Standard Time|-180|(UTC-03:00) Montevideo",
            "Magallanes Standard Time|-180|(UTC-03:00) Punta Arenas",
            "Saint Pierre Standard Time|-180|(UTC-03:00) Saint Pierre and Miquelon",
            "Bahia Standard Time|-180|(UTC-03:00) Salvador",
            "UTC-02|-120|(UTC-02:00) Coordinated Universal Time-02",
            "Greenland Standard Time|-120|(UTC-02:00) Greenland",
            "Mid-Atlantic Standard Time|-120|(UTC-02:00) Mid-Atlantic - Old",
            "Azores Standard Time|-60|(UTC-01:00) Azores",
            "Cape Verde Standard Time|-60|(UTC-01:00) Cabo Verde Is.",
            "UTC|0|(UTC) Coordinated Universal Time",
            "Morocco Standard Time|0|(UTC+00:00) Casablanca",
            "GMT Standard Time|0|(UTC+00:00) Dublin, Edinburgh, Lisbon, London",
            "Greenwich Standard Time|0|(UTC+00:00) Monrovia, Reykjavik",
            "Sao Tome Standard Time|0|(UTC+00:00) Sao Tome",
            "W. Europe Standard Time|60|(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna",
            "Central Europe Standard Time|60|(UTC+01:00) Belgrade, Bratislava, Budapest, Ljubljana, Prague",
            "Romance Standard Time|60|(UTC+01:00) Brussels, Copenhagen, Madrid, Paris",
            "Central European Standard Time|60|(UTC+01:00) Sarajevo, Skopje, Warsaw, Zagreb",
            "W. Central Africa Standard Time|60|(UTC+01:00) West Central Africa",
            "GTB Standard Time|120|(UTC+02:00) Athens, Bucharest",
            "Middle East Standard Time|120|(UTC+02:00) Beirut",
            "Egypt Standard Time|120|(UTC+02:00) Cairo",
            "E. Europe Standard Time|120|(UTC+02:00) Chisinau",
            "West Bank Standard Time|120|(UTC+02:00) Gaza, Hebron",
            "South Africa Standard Time|120|(UTC+02:00) Harare, Pretoria",
            "FLE Standard Time|120|(UTC+02:00) Helsinki, Kyiv, Riga, Sofia, Tallinn, Vilnius",
            "Israel Standard Time|120|(UTC+02:00) Jerusalem",
            "South Sudan Standard Time|120|(UTC+02:00) Juba",
            "Kaliningrad Standard Time|120|(UTC+02:00) Kaliningrad",
            "Sudan Standard Time|120|(UTC+02:00) Khartoum",
            "Libya Standard Time|120|(UTC+02:00) Tripoli",
            "Namibia Standard Time|120|(UTC+02:00) Windhoek",
            "Jordan Standard Time|180|(UTC+03:00) Amman",
            "Arabic Standard Time|180|(UTC+03:00) Baghdad",
            "Syria Standard Time|180|(UTC+03:00) Damascus",
            "Turkey Standard Time|180|(UTC+03:00) Istanbul",
            "Arab Standard Time|180|(UTC+03:00) Kuwait, Riyadh",
            "Belarus Standard Time|180|(UTC+03:00) Minsk",
            "Russian Standard Time|180|(UTC+03:00) Moscow, St. Petersburg",
            "E. Africa Standard Time|180|(UTC+03:00) Nairobi",
            "Volgograd Standard Time|180|(UTC+03:00) Volgograd",
            "Iran Standard Time|210|(UTC+03:30) Tehran",
            "Arabian Standard Time|240|(UTC+04:00) Abu Dhabi, Muscat",
            "Astrakhan Standard Time|240|(UTC+04:00) Astrakhan, Ulyanovsk",
            "Azerbaijan Standard Time|240|(UTC+04:00) Baku",
            "Russia Time Zone 3|240|(UTC+04:00) Izhevsk, Samara",
            "Mauritius Standard Time|240|(UTC+04:00) Port Louis",
            "Saratov Standard Time|240|(UTC+04:00) Saratov",
            "Georgian Standard Time|240|(UTC+04:00) Tbilisi",
            "Caucasus Standard Time|240|(UTC+04:00) Yerevan",
            "Afghanistan Standard Time|270|(UTC+04:30) Kabul",
            "West Asia Standard Time|300|(UTC+05:00) Ashgabat, Tashkent",
            "Qyzylorda Standard Time|300|(UTC+05:00) Astana",
            "Ekaterinburg Standard Time|300|(UTC+05:00) Ekaterinburg",
            "Pakistan Standard Time|300|(UTC+05:00) Islamabad, Karachi",
            "India Standard Time|330|(UTC+05:30) Chennai, Kolkata, Mumbai, New Delhi",
            "Sri Lanka Standard Time|330|(UTC+05:30) Sri Jayawardenepura",
            "Nepal Standard Time|345|(UTC+05:45) Kathmandu",
            "Central Asia Standard Time|360|(UTC+06:00) Bishkek",
            "Bangladesh Standard Time|360|(UTC+06:00) Dhaka",
            "Omsk Standard Time|360|(UTC+06:00) Omsk",
            "Myanmar Standard Time|390|(UTC+06:30) Yangon (Rangoon)",
            "SE Asia Standard Time|420|(UTC+07:00) Bangkok, Hanoi, Jakarta",
            "Altai Standard Time|420|(UTC+07:00) Barnaul, Gorno-Altaysk",
            "W. Mongolia Standard Time|420|(UTC+07:00) Hovd",
            "North Asia Standard Time|420|(UTC+07:00) Krasnoyarsk",
            "N. Central Asia Standard Time|420|(UTC+07:00) Novosibirsk",
            "Tomsk Standard Time|420|(UTC+07:00) Tomsk",
            "China Standard Time|480|(UTC+08:00) Beijing, Chongqing, Hong Kong, Urumqi",
            "North Asia East Standard Time|480|(UTC+08:00) Irkutsk",
            "Singapore Standard Time|480|(UTC+08:00) Kuala Lumpur, Singapore",
            "W. Australia Standard Time|480|(UTC+08:00) Perth",
            "Taipei Standard Time|480|(UTC+08:00) Taipei",
            "Ulaanbaatar Standard Time|480|(UTC+08:00) Ulaanbaatar",
            "Aus Central W. Standard Time|525|(UTC+08:45) Eucla",
            "Transbaikal Standard Time|540|(UTC+09:00) Chita",
            "Tokyo Standard Time|540|(UTC+09:00) Osaka, Sapporo, Tokyo",
            "North Korea Standard Time|540|(UTC+09:00) Pyongyang",
            "Korea Standard Time|540|(UTC+09:00) Seoul",
            "Yakutsk Standard Time|540|(UTC+09:00) Yakutsk",
            "Cen. Australia Standard Time|570|(UTC+09:30) Adelaide",
            "AUS Central Standard Time|570|(UTC+09:30) Darwin",
            "E. Australia Standard Time|600|(UTC+10:00) Brisbane",
            "AUS Eastern Standard Time|600|(UTC+10:00) Canberra, Melbourne, Sydney",
            "West Pacific Standard Time|600|(UTC+10:00) Guam, Port Moresby",
            "Tasmania Standard Time|600|(UTC+10:00) Hobart",
            "Vladivostok Standard Time|600|(UTC+10:00) Vladivostok",
            "Lord Howe Standard Time|630|(UTC+10:30) Lord Howe Island",
            "Bougainville Standard Time|660|(UTC+11:00) Bougainville Island",
            "Russia Time Zone 10|660|(UTC+11:00) Chokurdakh",
            "Magadan Standard Time|660|(UTC+11:00) Magadan",
            "Norfolk Standard Time|660|(UTC+11:00) Norfolk Island",
            "Sakhalin Standard Time|660|(UTC+11:00) Sakhalin",
            "Central Pacific Standard Time|660|(UTC+11:00) Solomon Is., New Caledonia",
            "Russia Time Zone 11|720|(UTC+12:00) Anadyr, Petropavlovsk-Kamchatsky",
            "New Zealand Standard Time|720|(UTC+12:00) Auckland, Wellington",
            "UTC+12|720|(UTC+12:00) Coordinated Universal Time+12",
            "Fiji Standard Time|720|(UTC+12:00) Fiji",
            "Kamchatka Standard Time|720|(UTC+12:00) Petropavlovsk-Kamchatsky - Old",
            "Chatham Islands Standard Time|765|(UTC+12:45) Chatham Islands",
            "UTC+13|780|(UTC+13:00) Coordinated Universal Time+13",
            "Tonga Standard Time|780|(UTC+13:00) Nuku'alofa",
            "Samoa Standard Time|780|(UTC+13:00) Samoa",
            "Line Islands Standard Time|840|(UTC+14:00) Kiritimati Island",
        };
    }
}
