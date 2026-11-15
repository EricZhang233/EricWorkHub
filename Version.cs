namespace EricWorkHub
{
    public static class AppVersion
    {
        public const string Version = "1.0.0";
        public static string DisplayVersion
        {
            get
            {
                return Text.F("VersionDisplay", Version);
            }
        }
    }
}
