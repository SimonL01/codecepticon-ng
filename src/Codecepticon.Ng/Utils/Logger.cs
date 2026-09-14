using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace Codecepticon.Utils
{
    class Logger
    {
        public static bool IsVerbose = false;

        public static bool IsDebug = false;

        public static string ConsoleLogFile = "";

        /// <summary>
        /// True once anything has been reported through Error(). Program.Main
        /// turns this into the process exit code.
        ///
        /// This is a sound failure signal because Error() is never used for
        /// anything benign - all 100+ call sites report a genuine failure, and
        /// the modules otherwise swallow their errors and return void. Keep it
        /// that way: if you want to tell the user something that is not a
        /// failure, use Warning().
        /// </summary>
        public static bool HasErrors { get; private set; }

        public static void Verbose(string message, bool newLine = true, bool showTime = true)
        {
            if (!IsVerbose)
            {
                return;
            }

            Write(message, newLine, showTime);
        }

        public static void Debug(string message, bool newLine = true, bool showTime = true)
        {
            if (!IsDebug)
            {
                return;
            }

            Console.ForegroundColor = ConsoleColor.Blue;
            Write("[DEBUG] " + message, newLine, showTime);
            Console.ResetColor();
        }

        public static void Error(string message, bool newLine = true, bool showTime = true)
        {
            HasErrors = true;
            Console.ForegroundColor = ConsoleColor.Red;
            Write(message, newLine, showTime);
            Console.ResetColor();
        }
        
        public static void Info(string message, bool newLine = true, bool showTime = true)
        {
            Write(message, newLine, showTime);
        }

        public static void Warning(string message, bool newLine = true, bool showTime = true)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Write(message, newLine, showTime);
            Console.ResetColor();
        }

        public static void Success(string message, bool newLine = true, bool showTime = true)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Write(message, newLine, showTime);
            Console.ResetColor();
        }

        protected static string FormatString(string message)
        {
            return $"[{DateTime.Now:HH:mm:ss}] {message}";
        }

        protected static void Write(string message, bool newLine = true, bool showTime = true)
        {
            message = showTime ? FormatString(message) : message;
            message += newLine ? Environment.NewLine : "";
            Console.Write(message);
            WriteToLogFile(message);
        }

        protected static void WriteToLogFile(string message)
        {
            // Write to file too.
            if (ConsoleLogFile == "")
            {
                // Seventh site of the hardcoded-separator bug AppPaths was added
                // for. On Linux this produced a file literally named
                // "net10.0\codecepticon.log" INSIDE bin/Release/, because the
                // backslash is an ordinary filename character there. InApp also
                // anchors to AppContext.BaseDirectory, which - unlike
                // the executing assembly's Location - stays correct for a
                // single-file publish.
                ConsoleLogFile = AppPaths.InApp("codecepticon.log");
                if (!File.Exists(ConsoleLogFile))
                {
                    File.Create(ConsoleLogFile).Dispose();
                }
            }

            using (StreamWriter w = File.AppendText(ConsoleLogFile))
            {
                w.Write(message);
            }
        }
    }
}
