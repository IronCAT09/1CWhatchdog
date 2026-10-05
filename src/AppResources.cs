using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace OneCWhatchdog
{
    /// <summary>Иконки и картинки, встроенные в exe (см. /resource в build.cmd).</summary>
    static class AppResources
    {
        const string IconResource = "OneCWhatchdog.app.ico";
        const string LogoResource = "OneCWhatchdog.about.png";

        /// <summary>Иконка окна и панели задач.</summary>
        public static Icon WindowIcon
        {
            get { return LoadIcon(SystemInformation.IconSize); }
        }

        /// <summary>Иконка для трея — размер под текущий масштаб экрана.</summary>
        public static Icon TrayIcon
        {
            get { return LoadIcon(SystemInformation.SmallIconSize); }
        }

        /// <summary>Логотип для вкладки «О программе».</summary>
        public static Image Logo
        {
            get
            {
                using (var stream = Open(LogoResource))
                using (var image = Image.FromStream(stream))
                    return new Bitmap(image); // копия: Image.FromStream требует живой поток
            }
        }

        static Icon LoadIcon(Size size)
        {
            using (var stream = Open(IconResource))
                return new Icon(stream, size);
        }

        static Stream Open(string name)
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null)
                throw new FileNotFoundException("Ресурс не встроен в exe: " + name);
            return stream;
        }
    }
}
