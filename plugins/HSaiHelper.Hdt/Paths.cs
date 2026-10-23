using System;
using System.IO;

namespace HSaiHelper.Hdt
{
	/// <summary>Папки и простой лог плагинов: %APPDATA%\HSaiHelper\.</summary>
	internal static class Paths
	{
		/// <summary>Версия плагинов — одна на сборку (из &lt;Version&gt; в .csproj). Показывается в настройках HDT и на панели оверлея.</summary>
		public static Version Version => typeof(Paths).Assembly.GetName().Version;

		public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";

		public static string Root => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HSaiHelper"));
		public static string Recordings => Ensure(Path.Combine(Root, "recordings"));
		public static string Cache => Ensure(Path.Combine(Root, "cache"));

		private static readonly object LogLock = new object();

		public static void Log(string message)
		{
			try
			{
				lock(LogLock)
					File.AppendAllText(Path.Combine(Root, "plugin.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
			}
			catch
			{
				// Лог не должен ронять HDT.
			}
		}

		private static string Ensure(string dir)
		{
			Directory.CreateDirectory(dir);
			return dir;
		}
	}
}
