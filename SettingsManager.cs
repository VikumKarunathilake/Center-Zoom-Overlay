using System;
using System.IO;
using System.Xml.Serialization;

namespace Center_Zoom_Overlay
{
    public class AppSettings
    {
        // Scope Settings
        public int ZoomFactor { get; set; } = 2;
        public double ScopeSize { get; set; } = 100;
        public bool ShowBorder { get; set; } = true;

        // Crosshair Settings
        public string CrosshairStyle { get; set; } = "Dot"; // Dot, Circle, Cross, DotCircle, None
        public double DotSize { get; set; } = 4;
        public int CrosshairColorR { get; set; } = 255;
        public int CrosshairColorG { get; set; } = 0;
        public int CrosshairColorB { get; set; } = 0;

        // System Settings
        public bool ExcludeFromCapture { get; set; } = true;
    }

    public static class SettingsManager
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CenterZoomOverlay");
        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.xml");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
                    using (StreamReader reader = new StreamReader(SettingsFile))
                    {
                        return (AppSettings)serializer.Deserialize(reader);
                    }
                }
            }
            catch
            {
                // Return defaults on failure
            }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                {
                    Directory.CreateDirectory(SettingsFolder);
                }
                XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
                using (StreamWriter writer = new StreamWriter(SettingsFile))
                {
                    serializer.Serialize(writer, settings);
                }
            }
            catch
            {
                // Ignore save errors in background
            }
        }
    }
}
