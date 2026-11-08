// Optional, user-enabled logon start: a clearly named Task Scheduler task "HeatSync-ArrowFix"
// (created only when the user ticks the box in the settings window; never touches other tasks).
// Uses the Windows Task Scheduler 2.0 COM API (Schedule.Service) - no external tools.
using System;
using System.Reflection;
using System.Security.Principal;

namespace HeatSyncArrowFix
{
    static class Autostart
    {
        public const string TaskName = "HeatSync-ArrowFix";
        static string Exe { get { return Assembly.GetExecutingAssembly().Location; } }

        static dynamic Folder()
        {
            dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            svc.Connect();
            return svc.GetFolder("\\");
        }

        public static bool IsEnabled()
        {
            try
            {
                dynamic task = Folder().GetTask(TaskName);
                foreach (dynamic a in task.Definition.Actions)
                    if (string.Equals((string)a.Path, Exe, StringComparison.OrdinalIgnoreCase)) return (bool)task.Enabled;
                return false;
            }
            catch { return false; }
        }

        /// returns null on success, otherwise an error message
        public static string Set(bool enable)
        {
            try
            {
                dynamic folder = Folder();
                if (!enable)
                {
                    try { folder.DeleteTask(TaskName, 0); } catch { }
                    Program.LogPublic("Autostart disabled by user (task removed)");
                    return null;
                }
                string user = WindowsIdentity.GetCurrent().Name;
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                svc.Connect();
                dynamic def = svc.NewTask(0);
                def.RegistrationInfo.Description = "HeatSync-ArrowFix: GAMEPOWER HeatSync LCD (enabled by the user in HeatSync-ArrowFix settings)";
                def.RegistrationInfo.Author = user;
                def.Principal.UserId = user;
                def.Principal.LogonType = 3;          // TASK_LOGON_INTERACTIVE_TOKEN
                def.Principal.RunLevel = 1;           // TASK_RUNLEVEL_HIGHEST (HWiNFO sensors need admin, same as the original)
                def.Settings.DisallowStartIfOnBatteries = false;
                def.Settings.StopIfGoingOnBatteries = false;
                def.Settings.ExecutionTimeLimit = "PT0S";
                def.Settings.MultipleInstances = 2;   // TASK_INSTANCES_IGNORE_NEW
                dynamic trig = def.Triggers.Create(9); // TASK_TRIGGER_LOGON
                trig.UserId = user;
                trig.Delay = "PT15S";                  // give the USB LCD time to enumerate
                dynamic act = def.Actions.Create(0);   // TASK_ACTION_EXEC
                act.Path = Exe;
                act.Arguments = "-auto";
                act.WorkingDirectory = System.IO.Path.GetDirectoryName(Exe);
                folder.RegisterTaskDefinition(TaskName, def, 6 /*CREATE_OR_UPDATE*/, null, null, 3, null);
                Program.LogPublic("Autostart enabled by user: task '" + TaskName + "' -> " + Exe + " -auto");
                return null;
            }
            catch (Exception ex)
            {
                Program.LogPublic("Autostart change failed: " + ex);
                return ex.Message;
            }
        }
    }
}
