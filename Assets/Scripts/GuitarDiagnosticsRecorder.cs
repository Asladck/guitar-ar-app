using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// For developers: records the hand joints of every processed camera frame and the events
    /// (strikes, starts, stops, calibration) to a CSV file, to replay or tune the strike logic
    /// later. Off unless switched on in the settings (under Диагностика); never records video
    /// or images. Files go to the app's Documents/GuitarDiagnostics (Xcode: Devices and
    /// Simulators, the app, Download Container).
    /// Rows: "hands,time,count,strum,chord" then per hand "chirality" and 21 x "x,y,confidence"
    /// (screen pixels, origin bottom left); "event,time,text". Time is the camera clock in seconds.
    /// </summary>
    public static class GuitarDiagnosticsRecorder
    {
        static StreamWriter s_Writer;
        static readonly StringBuilder s_Line = new StringBuilder();
        static int s_Rows;

        public static bool IsRecording => s_Writer != null;

        /// <summary>The file being written, or the last one.</summary>
        public static string FilePath { get; private set; }

        public static void Start()
        {
            if (s_Writer != null)
                return;
            try
            {
                string folder = Path.Combine(Application.persistentDataPath, "GuitarDiagnostics");
                Directory.CreateDirectory(folder);
                FilePath = Path.Combine(folder, System.DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
                s_Writer = new StreamWriter(FilePath, false, Encoding.UTF8);
                s_Writer.WriteLine("# GuitarAR hand recording: hands,time,count,strum,chord,[chirality,21*(x,y,c)]... | event,time,text");
                s_Rows = 0;
                Application.quitting += Stop;
                Debug.Log("[GuitarAR] Recording diagnostics to " + FilePath);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[GuitarAR] Can't record diagnostics: " + e.Message);
                s_Writer = null;
            }
        }

        public static void Stop()
        {
            if (s_Writer == null)
                return;
            try
            {
                s_Writer.Flush();
                s_Writer.Dispose();
            }
            catch (System.Exception)
            {
            }
            s_Writer = null;
            Application.quitting -= Stop;
        }

        public static void Hands(double time, int count, GuitarHandPose[] poses, int[] chirality, int strum, int chord)
        {
            if (s_Writer == null)
                return;
            s_Line.Clear();
            s_Line.Append("hands,").Append(time.ToString("0.0000", CultureInfo.InvariantCulture))
                .Append(',').Append(count).Append(',').Append(strum).Append(',').Append(chord);
            for (int h = 0; h < count; h++)
            {
                s_Line.Append(',').Append(chirality[h]);
                var pose = poses[h];
                for (int j = 0; j < GuitarHandPose.JointCount; j++)
                {
                    s_Line.Append(',').Append(pose.X[j].ToString("0.0", CultureInfo.InvariantCulture))
                        .Append(',').Append(pose.Y[j].ToString("0.0", CultureInfo.InvariantCulture))
                        .Append(',').Append(pose.Confidence[j].ToString("0.00", CultureInfo.InvariantCulture));
                }
            }
            Write(s_Line.ToString());
        }

        public static void Event(double time, string text)
        {
            if (s_Writer != null)
                Write("event," + time.ToString("0.0000", CultureInfo.InvariantCulture) + "," + text.Replace(',', ';'));
        }

        static void Write(string line)
        {
            try
            {
                s_Writer.WriteLine(line);
                if (++s_Rows % 60 == 0)
                    s_Writer.Flush();
            }
            catch (System.Exception)
            {
                Stop();
            }
        }
    }
}
