using UnityEngine;

// Where a player's name comes from. ONE place on purpose: today it's whatever they typed on the join screen, and when
// Steamworks goes in for friend invites this becomes SteamFriends.GetPersonaName() and nothing else in the game has
// to change.
//
// Deliberately NOT Steam-only even later. ParrelSync clones all share your Steam account, so a Steam-sourced name
// would report the same string on every test instance - two players both called ZerolesLuka, which makes nameplates
// useless exactly when you're building them. The typed name stays as the development path.
public static class PlayerIdentity
{
    private const string PrefsKey = "rr_display_name";
    private const int MaxLength = 16; //matches the NetworkString<_16> it gets written into, so nothing is silently truncated later

    private static string chosenName;

    //Set from the join screen. Remembered between sessions so nobody retypes it every launch.
    public static string LocalName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(chosenName)) return chosenName;
            //Sanitise on the way OUT as well as in. Only the setter used to run it, so a value already sitting in
            //PlayerPrefs - written by a build from before MaxLength existed, or edited by hand - came straight back
            //out at whatever length it was and went into a NetworkString<_16>, which is the exact silent truncation
            //the constant above claims to prevent.
            chosenName = Sanitise(PlayerPrefs.GetString(PrefsKey, ""));
            return chosenName;
        }
        set
        {
            chosenName = Sanitise(value);
            PlayerPrefs.SetString(PrefsKey, chosenName);
        }
    }

    //The name to actually put on a player. Falls back to something readable rather than an empty nameplate.
    public static string ResolveName(int playerId)
    {
        //WHEN STEAMWORKS LANDS: return SteamFriends.GetPersonaName() here when the client is running, and keep the
        //typed name below as the editor/ParrelSync path. That's the whole change.
        string local = LocalName;
        if (!string.IsNullOrWhiteSpace(local)) return local;
        return $"Robber {playerId}";
    }

    private static string Sanitise(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string trimmed = raw.Trim();
        return trimmed.Length > MaxLength ? trimmed.Substring(0, MaxLength) : trimmed;
    }
}
