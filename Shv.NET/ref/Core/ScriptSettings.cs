using System;
using System.Collections.Generic;

namespace GTA
{
    /// <summary>Mirrors Shv.NET/source/core/Settings.hpp.</summary>
    public sealed class ScriptSettings
    {
        private string _filename;
        private Dictionary<string, string> _values = new Dictionary<string, string>();

        private ScriptSettings(string filename)
        {
            _filename = filename;
        }

        public static ScriptSettings Load(string filename)
        {
            throw Stub.NotAvailable();
        }

        public bool Save()
        {
            throw Stub.NotAvailable();
        }

        public T GetValue<T>(string section, string name, T defaultvalue)
        {
            throw Stub.NotAvailable();
        }

        public string GetValue(string section, string name)
        {
            throw Stub.NotAvailable();
        }

        public string GetValue(string section, string name, string defaultvalue)
        {
            throw Stub.NotAvailable();
        }

        public string[] GetAllValues(string section, string name)
        {
            throw Stub.NotAvailable();
        }

        public void SetValue<T>(string section, string name, T value)
        {
            throw Stub.NotAvailable();
        }

        public void SetValue(string section, string name, string value)
        {
            throw Stub.NotAvailable();
        }
    }
}
