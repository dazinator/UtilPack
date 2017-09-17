using Microsoft.Build.Framework;
using System;
using System.Reflection;

namespace Repro.Task.Tasks
{
    public abstract class AbstractTask : ITask
    {
        public IBuildEngine BuildEngine { get; set; }
        public ITaskHost HostObject { get; set; }

        public bool Execute()
        {
            try
            {
                LogProperties();
                return ExecuteTask();
            }
            catch (Exception ex)
            {
                LogError(this.GetType().Name + ex.GetType().Name.GetHashCode(), ex.Message);
                LogError(this.GetType().Name + ex.GetType().Name.GetHashCode(), ex.ToString());
                return false;              
            }

        }

        public abstract bool ExecuteTask();

        protected void LogMessage(string message, MessageImportance importance = MessageImportance.High)
        {
            if (BuildEngine != null)
            {
                BuildEngine.LogMessageEvent(new BuildMessageEventArgs(this.GetType().Name + ": " + message, this.GetType().Name, this.GetType().Name, importance));
            }
        }

        protected void LogWarning(string code, string message)
        {
            if (BuildEngine != null)
            {
                BuildEngine.LogWarningEvent(new BuildWarningEventArgs(this.GetType().Name, code, null, 0, 0, 0, 0, message, this.GetType().Name, this.GetType().Name));
            }
        }

        protected void LogError(string code, string message)
        {
            if (BuildEngine != null)
            {
                BuildEngine.LogErrorEvent(new BuildErrorEventArgs(this.GetType().Name, code, null, 0, 0, 0, 0, message, this.GetType().Name, this.GetType().Name));
            }
        }

        protected void LogProperties()
        {
          
#if GETPROPERTIES
            LogMessage("---Properties---", MessageImportance.Low);          
            foreach (var prop in this.GetType().GetProperties())
            {
                var propValue = prop.GetValue(this, null);
                string propValueToLog = "--EMPTY--";
                if (propValue != null)
                {
                    propValueToLog = propValue.ToString();
                }
                LogMessage(string.Format("{0} = {1}", prop.Name, propValueToLog), MessageImportance.Low);
            }
#endif
        }
    }
}
