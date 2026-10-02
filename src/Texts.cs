using System.ServiceProcess;

namespace ServiceWatchdog
{
    static class Texts
    {
        public static string Status(ServiceControllerStatus? status)
        {
            if (status == null)
                return "Не найдена";
            switch (status.Value)
            {
                case ServiceControllerStatus.Running: return "Выполняется";
                case ServiceControllerStatus.Stopped: return "Остановлена";
                case ServiceControllerStatus.Paused: return "Приостановлена";
                case ServiceControllerStatus.StartPending: return "Запускается";
                case ServiceControllerStatus.StopPending: return "Останавливается";
                case ServiceControllerStatus.PausePending: return "Приостанавливается";
                case ServiceControllerStatus.ContinuePending: return "Возобновляется";
                default: return status.Value.ToString();
            }
        }

        public static string StartType(ServiceStartMode? mode)
        {
            if (mode == null)
                return "";
            switch (mode.Value)
            {
                case ServiceStartMode.Automatic: return "Автоматически";
                case ServiceStartMode.Manual: return "Вручную";
                case ServiceStartMode.Disabled: return "Отключена";
                case ServiceStartMode.Boot: return "Загрузка";
                case ServiceStartMode.System: return "Система";
                default: return mode.Value.ToString();
            }
        }
    }
}
