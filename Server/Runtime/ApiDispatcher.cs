using System;
using GTANetworkServer.Managers;
using GTANetworkServer.Constant;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using GTANetworkShared;

namespace GTANetworkServer.Runtime
{
    /// <summary>
    /// Resolves a "call" frame to a member of <see cref="API"/> by reflection (once per name, overloads by arity) and converts
    /// the arguments from the wire shapes the Bun runtime sends (numbers, strings, booleans, arrays, maps) into CLR types:
    /// entities and players by handle, <see cref="Vector3"/> from [x, y, z] or {x, y, z}, enums from their number or name.
    /// Results travel back the other way (entities as their handle, Vector3 as {x, y, z}).
    /// </summary>
    internal sealed class ApiDispatcher
    {
        private readonly Dictionary<string, MethodInfo[]> _methods;
        private readonly Dictionary<string, PropertyInfo> _properties;

        public ApiDispatcher()
        {
            var api = typeof(API);
            _methods = api.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .GroupBy(m => m.Name)
                .ToDictionary(g => g.Key, g => g.OrderBy(m => m.GetParameters().Length).ToArray(), StringComparer.Ordinal);
            _properties = api.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .ToDictionary(p => p.Name, StringComparer.Ordinal);
        }

        public int MemberCount => _methods.Count + _properties.Count;

        /// <summary>Invokes API.name(args) on the resource's API instance and returns the result in wire form.</summary>
        public object Invoke(API api, string name, object[] args)
        {
            args = args ?? Array.Empty<object>();
            if (_methods.TryGetValue(name, out var overloads))
            {
                string lastError = null;
                foreach (var method in overloads)
                {
                    if (!TryBind(api, method, args, out var bound, out var error)) { lastError = error; continue; }
                    var result = method.Invoke(api, bound);
                    return result is System.Threading.Tasks.Task ? result : ToWire(result); // a Task is finished by the bridge (CompleteLater)
                }
                throw new ArgumentException("no overload of API." + name + " takes " + Describe(args) + (lastError != null ? " (" + lastError + ")" : ""));
            }
            if (_properties.TryGetValue(name, out var property))
            {
                if (args.Length == 0) return ToWire(property.GetValue(api));
                property.SetValue(api, Convert(args[0], property.PropertyType, api));
                return null;
            }
            throw new MissingMethodException("API has no member " + name);
        }

        private bool TryBind(API api, MethodInfo method, object[] args, out object[] bound, out string error)
        {
            var parameters = method.GetParameters();
            bound = new object[parameters.Length];
            error = null;
            var isParams = parameters.Length > 0 && parameters[^1].GetCustomAttribute<ParamArrayAttribute>() != null;
            var fixedCount = isParams ? parameters.Length - 1 : parameters.Length;
            if (args.Length > fixedCount && !isParams) { error = "too many arguments"; return false; }
            try
            {
                for (var i = 0; i < fixedCount; i++)
                {
                    if (i < args.Length) bound[i] = Convert(args[i], parameters[i].ParameterType, api);
                    else if (parameters[i].IsOptional) bound[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : Type.Missing;
                    else { error = "missing argument " + parameters[i].Name; return false; }
                }
                if (isParams)
                {
                    var elementType = parameters[^1].ParameterType.GetElementType()!;
                    var rest = args.Length > fixedCount ? args.Skip(fixedCount).ToArray() : Array.Empty<object>();
                    // a single array argument for a params parameter is spread, as C# would
                    if (rest.Length == 1 && rest[0] is object[] inner && elementType == typeof(object)) rest = inner;
                    var array = Array.CreateInstance(elementType, rest.Length);
                    for (var i = 0; i < rest.Length; i++) array.SetValue(Convert(rest[i], elementType, api), i);
                    bound[^1] = array;
                }
                return true;
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string Describe(object[] args) => "(" + string.Join(", ", args.Select(a => a == null ? "null" : a is object[] arr ? "array[" + arr.Length + "]" : a.GetType().Name)) + ")";

        /// <summary>A wire value as the CLR type a parameter or property wants.</summary>
        public static object Convert(object wire, Type target, API api)
        {
            if (target == typeof(object)) return Normalize(wire);
            var underlying = Nullable.GetUnderlyingType(target);
            if (underlying != null)
            {
                if (wire == null) return null;
                target = underlying;
            }
            if (wire == null)
            {
                if (target == typeof(string) || !target.IsValueType) return null;
                if (target == typeof(NetHandle)) return new NetHandle(0);
                throw new InvalidCastException("null for " + target.Name);
            }
            if (target.IsInstanceOfType(wire)) return wire;
            if (target == typeof(string)) return wire is object[] || wire is IDictionary ? throw new InvalidCastException("string expected") : System.Convert.ToString(wire, CultureInfo.InvariantCulture);
            if (target == typeof(bool)) return wire is bool b ? b : System.Convert.ToDouble(wire, CultureInfo.InvariantCulture) != 0;
            if (target.IsEnum)
            {
                if (wire is string s) return Enum.Parse(target, s, true);
                return Enum.ToObject(target, System.Convert.ToInt64(wire, CultureInfo.InvariantCulture));
            }
            if (target.IsPrimitive || target == typeof(decimal))
            {
                if (wire is string || wire is object[] || wire is IDictionary) throw new InvalidCastException(target.Name + " expected");
                return System.Convert.ChangeType(wire, target, CultureInfo.InvariantCulture);
            }
            if (target == typeof(NetHandle)) return new NetHandle(HandleOf(wire));
            if (target == typeof(Client))
            {
                var handle = HandleOf(wire);
                var client = Program.ServerInstance.Clients.FirstOrDefault(c => c.handle.Value == handle);
                return client ?? throw new ArgumentException("no player with handle " + handle);
            }
            if (typeof(Entity).IsAssignableFrom(target))
            {
                var ctor = target.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(API), typeof(NetHandle) }, null)
                           ?? throw new InvalidCastException(target.Name + " has no (API, NetHandle) constructor");
                return ctor.Invoke(new object[] { api, new NetHandle(HandleOf(wire)) });
            }
            if (target == typeof(Vector3)) return ToVector3(wire);
            if (target == typeof(Color))
            {
                var parts = Components(wire, "r", "g", "b", "a");
                return parts.Length >= 4 && parts[3].HasValue ? new Color((int)parts[0].Value, (int)parts[1].Value, (int)parts[2].Value, (int)parts[3].Value) : new Color((int)parts[0].Value, (int)parts[1].Value, (int)parts[2].Value);
            }
            if (target.IsArray)
            {
                var items = wire as object[] ?? new[] { wire };
                var elementType = target.GetElementType()!;
                var array = Array.CreateInstance(elementType, items.Length);
                for (var i = 0; i < items.Length; i++) array.SetValue(Convert(items[i], elementType, api), i);
                return array;
            }
            if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(List<>))
            {
                var items = wire as object[] ?? new[] { wire };
                var elementType = target.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(target)!;
                foreach (var item in items) list.Add(Convert(item, elementType, api));
                return list;
            }
            if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(Dictionary<,>) && wire is IDictionary<string, object> map)
            {
                var valueType = target.GetGenericArguments()[1];
                var dict = (IDictionary)Activator.CreateInstance(target)!;
                foreach (var kv in map) dict.Add(kv.Key, Convert(kv.Value, valueType, api));
                return dict;
            }
            if (typeof(Delegate).IsAssignableFrom(target)) throw new InvalidCastException("callbacks cannot cross the bridge; use events");
            throw new InvalidCastException("cannot convert " + wire.GetType().Name + " to " + target.Name);
        }

        /// <summary>Wire numbers arrive as long or double; an integral value in an object slot is handed over as int when it fits.</summary>
        private static object Normalize(object wire)
        {
            switch (wire)
            {
                case long l when l >= int.MinValue && l <= int.MaxValue: return (int)l;
                case double d when Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < int.MaxValue: return (int)d;
                case object[] arr: return arr.Select(Normalize).ToList();
                case IDictionary<string, object> map when map.Count == 3 && map.ContainsKey("x") && map.ContainsKey("y") && map.ContainsKey("z"): return ToVector3(map);
                default: return wire;
            }
        }

        private static int HandleOf(object wire)
        {
            switch (wire)
            {
                case NetHandle h: return h.Value;
                case Entity e: return e.handle.Value;
                case Client c: return c.handle.Value;
                case IDictionary<string, object> map when map.TryGetValue("handle", out var hv): return System.Convert.ToInt32(hv, CultureInfo.InvariantCulture);
                case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed): return parsed;
                case null: return 0;
                default: return System.Convert.ToInt32(wire, CultureInfo.InvariantCulture);
            }
        }

        private static Vector3 ToVector3(object wire)
        {
            var c = Components(wire, "x", "y", "z");
            return new Vector3((float)c[0].GetValueOrDefault(), (float)c[1].GetValueOrDefault(), (float)c[2].GetValueOrDefault());
        }

        /// <summary>Numeric components from an array or a map (keys case-insensitive).</summary>
        private static double?[] Components(object wire, params string[] keys)
        {
            var result = new double?[Math.Max(keys.Length, 4)];
            switch (wire)
            {
                case object[] arr:
                    for (var i = 0; i < arr.Length && i < result.Length; i++) result[i] = arr[i] == null ? null : System.Convert.ToDouble(arr[i], CultureInfo.InvariantCulture);
                    return result;
                case IDictionary<string, object> map:
                    for (var i = 0; i < keys.Length; i++)
                    {
                        var hit = map.FirstOrDefault(kv => string.Equals(kv.Key, keys[i], StringComparison.OrdinalIgnoreCase));
                        if (hit.Key != null && hit.Value != null) result[i] = System.Convert.ToDouble(hit.Value, CultureInfo.InvariantCulture);
                    }
                    return result;
                default:
                    throw new InvalidCastException("array or map expected for " + string.Join("/", keys));
            }
        }

        /// <summary>A CLR value in the shape the runtime understands.</summary>
        public static object ToWire(object value)
        {
            switch (value)
            {
                case null: return null;
                case string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal: return value;
                case Enum e: return System.Convert.ToInt64(e);
                case NetHandle h: return h.Value;
                case Client c: return c.handle.Value;
                case Entity e: return e.handle.Value;
                case ColShape shape: return shape.handle;
                case Vector3 v: return new Dictionary<string, object> { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };
                case Color color: return new Dictionary<string, object> { ["r"] = color.red, ["g"] = color.green, ["b"] = color.blue, ["a"] = color.alpha };
                case XmlGroup xml: return xml.ToString();
                case Newtonsoft.Json.Linq.JToken json: return ToWire(RpcJson.ToPlain(json)); // RPC answers (callClient)
                case IDictionary dict:
                {
                    var map = new Dictionary<string, object>();
                    foreach (DictionaryEntry kv in dict) map[System.Convert.ToString(kv.Key, CultureInfo.InvariantCulture) ?? ""] = ToWire(kv.Value);
                    return map;
                }
                case IEnumerable list:
                {
                    var items = new List<object>();
                    foreach (var item in list) items.Add(ToWire(item));
                    return items;
                }
                default: return value.ToString();
            }
        }
    }
}
