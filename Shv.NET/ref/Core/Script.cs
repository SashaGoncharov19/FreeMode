using System;
using System.IO;
using System.Windows.Forms;

namespace GTA
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class RequireScript : Attribute
    {
        internal Type _dependency;

        public RequireScript(Type dependency)
        {
            _dependency = dependency;
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class ScriptAttributes : Attribute
    {
        public string Author { get; set; }
        public string SupportURL { get; set; }
    }

    /// <summary>A base class for all user scripts to inherit (mirrors Shv.NET/source/core/Script.hpp).</summary>
    public abstract class Script
    {
        internal int _interval;
        internal bool _running;
        internal string _filename;
        internal ScriptDomain _scriptdomain;
        internal ScriptSettings _settings;

        public Script()
        {
        }

        public event EventHandler Present;
        public event EventHandler Tick;
        public event KeyEventHandler KeyUp;
        public event KeyEventHandler KeyDown;
        public event EventHandler Aborted;

        public static void Wait(int ms)
        {
            throw Stub.NotAvailable();
        }

        public static void Yield()
        {
            throw Stub.NotAvailable();
        }

        public string Name
        {
            get { return GetType().FullName; }
        }

        public string Filename
        {
            get { return _filename; }
        }

        public ScriptSettings Settings
        {
            get { throw Stub.NotAvailable(); }
        }

        public string BaseDirectory
        {
            get { return Path.GetDirectoryName(_filename); }
        }

        public void Abort()
        {
            throw Stub.NotAvailable();
        }

        public override string ToString()
        {
            return Name;
        }

        protected int Interval
        {
            get { return _interval; }
            set { _interval = value; }
        }

        protected void AttachD3DHook()
        {
            throw Stub.NotAvailable();
        }

        internal void MainLoop()
        {
            throw Stub.NotAvailable();
        }

        internal unsafe void D3DHook(void* swapchain)
        {
            throw Stub.NotAvailable();
        }
    }
}
