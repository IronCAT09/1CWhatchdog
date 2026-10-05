namespace OneCWhatchdog
{
    /// <summary>
    /// Редакция сборки. Полная — службы и контроль запуска программ; «только службы»
    /// (собирается с /define:SERVICES_ONLY) — без вкладок и проверки программ.
    /// </summary>
    static class Edition
    {
#if SERVICES_ONLY
        public static readonly bool AppControl = false;
        public static readonly string Suffix = " (только службы)";
#else
        public static readonly bool AppControl = true;
        public static readonly string Suffix = "";
#endif
    }
}
