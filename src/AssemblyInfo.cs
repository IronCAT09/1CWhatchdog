using System.Reflection;

[assembly: AssemblyTitle("1CWhatchdog")]
#if SERVICES_ONLY
[assembly: AssemblyProduct("1CWhatchdog (только службы)")]
[assembly: AssemblyDescription("Перезапуск служб Windows")]
#else
[assembly: AssemblyProduct("1CWhatchdog")]
[assembly: AssemblyDescription("Перезапуск служб Windows и контроль запуска программ")]
#endif
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]
