using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace GTA
{
    internal interface IScriptTask
    {
        void Run();
    }

    /// <summary>Mirrors Shv.NET/source/core/ScriptDomain.hpp (a "private ref class", i.e. internal).</summary>
    internal sealed class ScriptDomain : MarshalByRefObject
    {
        private static ScriptDomain sCurrentDomain;
        private Script _executingScript;
        private List<Script> _runningScripts = new List<Script>();

        public ScriptDomain()
        {
        }

        public unsafe void DoD3DCall(void* swapchain)
        {
            throw Stub.NotAvailable();
        }

        public static Script ExecutingScript
        {
            get { return sCurrentDomain == null ? null : sCurrentDomain._executingScript; }
        }

        public static ScriptDomain CurrentDomain
        {
            get { return sCurrentDomain; }
        }

        public static ScriptDomain Load(string path)
        {
            throw Stub.NotAvailable();
        }

        public static void Unload(ref ScriptDomain domain)
        {
            throw Stub.NotAvailable();
        }

        public string Name
        {
            get { throw Stub.NotAvailable(); }
        }

        public AppDomain AppDomain
        {
            get { throw Stub.NotAvailable(); }
        }

        public Script[] RunningScripts
        {
            get { return _runningScripts.ToArray(); }
        }

        public void HookD3DScript(Script script)
        {
            throw Stub.NotAvailable();
        }

        public void Start()
        {
            throw Stub.NotAvailable();
        }

        public void Abort()
        {
            throw Stub.NotAvailable();
        }

        public static void AbortScript(Script script)
        {
            throw Stub.NotAvailable();
        }

        public void DoTick()
        {
            throw Stub.NotAvailable();
        }

        public void DoKeyboardMessage(Keys key, bool status, bool statusCtrl, bool statusShift, bool statusAlt)
        {
            throw Stub.NotAvailable();
        }

        public void PauseKeyboardEvents(bool pause)
        {
            throw Stub.NotAvailable();
        }

        public void ExecuteTask(IScriptTask task)
        {
            throw Stub.NotAvailable();
        }

        public IntPtr PinString(string str)
        {
            throw Stub.NotAvailable();
        }

        public bool IsKeyPressed(Keys key)
        {
            throw Stub.NotAvailable();
        }

        public string LookupScriptFilename(Script script)
        {
            return LookupScriptFilename(script.GetType());
        }

        public string LookupScriptFilename(Type scripttype)
        {
            throw Stub.NotAvailable();
        }

        public override object InitializeLifetimeService()
        {
            return null;
        }
    }
}
