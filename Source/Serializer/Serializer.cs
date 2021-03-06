﻿using SomaSim.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace SomaSim.Serializer
{
    /// <summary>
    /// Serializes typed structures into JSON and back out into class instances.
    /// If serialized object's type can be inferred from context (from enclosing object)
    /// its type specification will be skipped, otherwise it will be serialized out.
    /// 
    /// This serializer will write out any public member variables, and any properties
    /// that have public getters and setters.
    /// 
    /// Some caveats:
    /// - Structures must be trees (ie. no cycles). 
    /// - Serialized classes *must* have default constructors (without parameters)
    /// </summary>
    public class Serializer : IService
    {
        public string TYPEKEY = "#type";

        /// <summary>
        /// If true, only values different from defaults will be written out
        /// during serialization. Produces much smaller files.
        /// </summary>
        public bool SkipDefaultsDuringSerialization = true;

        /// <summary>
        /// If true, when deserializing a strongly typed class, any key in JSON
        /// that doesn't correspond to a member field in the class instance will 
        /// cause an exception to be thrown.
        /// </summary>
        public bool ThrowErrorOnSpuriousData = true;

        /// <summary>
        /// If true, attempting to serialize null will cause an exception to be thrown.
        /// </summary>
        public bool ThrowErrorOnSerializingNull = false;

        /// <summary>
        /// In case we try to deserialize a scalar value into a field that expects a 
        /// class instance or a collection, if this is true an exception will be thrown,
        /// otherwise the unexpected value will be deserialized as null.
        /// </summary>
        public bool ThrowErrorOnUnexpectedCollections = true;

        /// <summary>
        /// In the case deserializer finds a "#type" annotation that doesn't correspond to 
        /// any known type, if this is true, an exception will be thrown; otherwise 
        /// the object will be deserialized as null.
        /// </summary>
        public bool ThrowErrorOnUnknownTypes = true;

        internal struct NSDef {
            public string prefix;
            public bool isclass;
        }

        private Dictionary<string, Type> _ExplicitlyNamedTypes;
        private Dictionary<Type, object> _DefaultInstances;
        private Dictionary<Type, Func<object>> _InstanceFactories;
        private Dictionary<Type, Dictionary<string, MemberInfo>> _TypeToAllMemberInfos;
        private List<NSDef> _ImplicitNamespaces;

        public void Initialize () {
            this._ExplicitlyNamedTypes = new Dictionary<string, Type>();
            this._DefaultInstances = new Dictionary<Type, object>();
            this._InstanceFactories = new Dictionary<Type, Func<object>>();
            this._TypeToAllMemberInfos = new Dictionary<Type, Dictionary<string, MemberInfo>>();
            this._ImplicitNamespaces = new List<NSDef>();
        }

        public void Release () {
            this._ImplicitNamespaces.Clear();
            this._ImplicitNamespaces = null;

            foreach (var entry in _TypeToAllMemberInfos) { entry.Value.Clear(); }
            this._TypeToAllMemberInfos.Clear();
            this._TypeToAllMemberInfos = null;

            this._InstanceFactories.Clear();
            this._InstanceFactories = null;

            this._DefaultInstances.Clear();
            this._DefaultInstances = null;

            this._ExplicitlyNamedTypes.Clear();
            this._ExplicitlyNamedTypes = null;
        }


        //
        //
        // DESERIALIZE

        /// <summary>
        /// Deserializes a parsed value object, forcing it into an instance of given type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <returns></returns>
        public T Deserialize<T> (object value) {
            return (T)Deserialize(value, typeof(T));
        }

        /// <summary>
        /// Deserializes a parsed value object, allowing the user to supply type
        /// or trying to infer it from annotation inside the value object.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="targettype"></param>
        /// <returns></returns>
        public object Deserialize (object value, Type targettype = null) {
            // reset type to "unknown" if it's insufficiently specific
            if (targettype == typeof(object)) {
                targettype = null;
            }

            // return nulls as nulls
            if (value == null) {
                return null;
            }

            // enums get converted from a number value, or parsed by name from a string
            if (targettype != null && targettype.IsEnum) {
                if (value is string) {
                    return Enum.Parse(targettype, (string)value, true);
                } else {
                    int enumValue = Convert.ToInt32(value);
                    return Enum.ToObject(targettype, enumValue);
                }
            }

            // scalars get simply converted (if needed)
            if (targettype != null && targettype.IsPrimitive) {
                return Convert.ChangeType(value, targettype);
            }
            if (IsScalar(value)) {
                return (targettype != null) ? Convert.ChangeType(value, targettype) : value;
            }

            // try to infer type from the value if it's a dictionary with a #type key
            if (targettype == null || IsExplicitlyTypedHashtable(value)) {
                targettype = InferType(value);
            }

            // we know the type, make an instance to deserialize into
            int size = (value is ArrayList) ? ((ArrayList)value).Count : 0;
            object instance = CreateInstance(targettype, size);

            // are we deserializing a dictionary?
            if (typeof(IDictionary).IsAssignableFrom(targettype)) {
                Hashtable table = value as Hashtable;
                if (table == null) {
                    if (ThrowErrorOnUnexpectedCollections) {
                        throw new Exception("Deserializer found value where Hashtable expected: " + value);
                    }
                    return null;
                }

                // recursively deserialize all values
                foreach (DictionaryEntry entry in table) {
                    // do we need to convert values into some specific type?
                    Type valuetype = null;
                    if (targettype.IsGenericType) {
                        valuetype = targettype.GetGenericArguments()[1]; // T in Dictionary<S,T>
                    }
                    object typedEntryValue = Deserialize(entry.Value, valuetype);
                    targettype.GetMethod("Add").Invoke(instance, new object[] { entry.Key, typedEntryValue });
                }

            }

            // are we deserializing a linear collection?
            else if (typeof(IEnumerable).IsAssignableFrom(targettype)) {
                // recursively deserialize all values
                ArrayList list = value as ArrayList;
                if (list == null) {
                    if (ThrowErrorOnUnexpectedCollections) {
                        throw new Exception("Deserializer found value where ArrayList expected: " + value);
                    }
                    return null;
                }

                Array array = instance as Array;
                Type arrayElementType = (array != null) ? array.GetType().GetElementType() : null;
                int i = 0;
                foreach (object element in list) {
                    // do we need to convert values into some specific type?
                    Type valuetype = null;
                    if (targettype.IsGenericType) {
                        valuetype = targettype.GetGenericArguments()[0]; // T in List<T>
                    }
                    object typedElement = Deserialize(element, valuetype);

                    // now insert into the list or array as needed
                    if (array != null) { // T[]
                        array.SetValue(Convert.ChangeType(typedElement, arrayElementType), i);
                    } else {             // List<T> or List or some such
                        targettype.GetMethod("Add").Invoke(instance, new object[] { typedElement });
                    }
                    i++;                    
                }
            } else {
                // class - deserialize each field recursively
                DeserializeIntoClassOrStruct(value, instance);
            }

            return instance;
        }

        private Dictionary<string, MemberInfo> GetMemberInfos (Type t) {
            Dictionary<string, MemberInfo> result = null;
            if (_TypeToAllMemberInfos.TryGetValue(t, out result)) {
                return result;
            }

            result = new Dictionary<string, MemberInfo>();
            foreach (var info in TypeUtils.GetMembers(t)) {
                result[info.Name] = info;
            }

            _TypeToAllMemberInfos.Add(t, result);
            return result;
        }

        /// <summary>
        /// Helper function, deserializes a parsed object into a specific class instance,
        /// using each field's type in recursive deserialization.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="target"></param>
        internal void DeserializeIntoClassOrStruct (object value, object target) {
            Type targetType = target.GetType();
            var members = GetMemberInfos(targetType);

            if (value is Hashtable) {
                Hashtable table = value as Hashtable;
                foreach (DictionaryEntry entry in table) {
                    string key = entry.Key as String;
                    if (key == TYPEKEY) {
                        // ignore it
                    } else if (members.ContainsKey(key)) {
                        MemberInfo member = members[key];
                        object fieldval = Deserialize(entry.Value, TypeUtils.GetMemberType(member));
                        if (member is PropertyInfo) { ((PropertyInfo)member).SetValue(target, fieldval, null); }
                        if (member is FieldInfo) { ((FieldInfo)member).SetValue(target, fieldval); }
                    } else if (ThrowErrorOnSpuriousData) {
                        throw new Exception("Deserializer found key in data but not in class, key=" + key + ", class=" + target);
                    }
                }
            } else if (ThrowErrorOnSpuriousData) {
                throw new Exception("Deserializer can't populate class or struct from a non-hashtable");
            }
        }


        //
        //
        // SERIALIZE

        private static List<Type> INT_TYPES = new List<Type> { // all types of size Int32 or smaller (so not Uint32 or larger)
            typeof(Int16), typeof(Int32), typeof(UInt16), typeof(Char), typeof(Byte), typeof(SByte) 
        };

        /// <summary>
        /// Serializes a class instance or a collection into a parsed value, ready to be converted into JSON.
        /// Also allows the caller to specify if the type of this instance should be added to the 
        /// produced value, otherwise it will be left out.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="specifyType"></param>
        /// <returns></returns>
        public object Serialize (object value, bool specifyType = false) {
            if (value == null) {
                if (ThrowErrorOnSerializingNull) {
                    throw new Exception("Serializer encountered an unexpected null");
                }

                return null;
            }

            Type type = value.GetType();

            // booleans and strings get returned as is
            if (value is Boolean || value is String) {
                return value;
            }

            // enums get converted to their number value and saved out as int
            if (type.IsEnum) {
                return Convert.ToInt32(value);
            }

            // numeric types and chars get converted to either a double or an int
            if (type.IsPrimitive) {
                if (INT_TYPES.Contains(type)) {
                    return Convert.ToInt32(value);
                } else {
                    return Convert.ToDouble(value);
                }
            }

            // this is either a collection or a class instance. 
            // if it's a collection and it's not generic, we'll need to explicitly serialize out
            // type names for each value
            bool isPotentiallyUntyped = !type.IsGenericType;

            // if it's a dictionary, convert to hashtable
            if (value is IDictionary) {
                return SerializeDictionary(value as IDictionary, isPotentiallyUntyped);
            }

            // if it's a list, convert to array list
            if (typeof(IEnumerable).IsAssignableFrom(type)) {
                return SerializeEnumerable(value as IEnumerable, isPotentiallyUntyped);
            }

            // it's some other type of class or object - serialize field by field
            return SerializeClassOrStruct(value, specifyType);
        }

        private object SerializeClassOrStruct (object value, bool specifyType) {
            Hashtable result = new Hashtable();
            if (specifyType) {
                result[TYPEKEY] = value.GetType().FullName;
            }

            var fields = TypeUtils.GetMembers(value);
            foreach (MemberInfo field in fields) {
                string fieldName = field.Name;
                object rawFieldValue = TypeUtils.GetValue(field, value);
                bool serialize = true;

                if (SkipDefaultsDuringSerialization) {
                    object defaultValue = GetDefaultInstanceValue(value, field);
                    serialize = (rawFieldValue != null) &&
                                !rawFieldValue.Equals(defaultValue);
                }

                if (serialize) {
                    bool specifyFieldType = false; 
                    object serializedFieldValue = Serialize(rawFieldValue, specifyFieldType);
                    result[fieldName] = serializedFieldValue;
                }
            }

            return result;
        }

        private Hashtable SerializeDictionary (IDictionary dict, bool specifyValueTypes) {
            Hashtable results = new Hashtable();
            foreach (DictionaryEntry entry in dict) {
                object key = entry.Key;
                object value = Serialize(entry.Value, specifyValueTypes);
                results[key] = value;
            }

            return results;
        }

        private ArrayList SerializeEnumerable (IEnumerable list, bool specifyValueTypes) {
            ArrayList results = new ArrayList();
            foreach (object element in list) {
                object value = Serialize(element, specifyValueTypes);
                results.Add(value);
            }

            return results;
        }

        //
        //
        // HELPERS

        public T Clone<T> (T value) {
            return (T)Clone(value, typeof(T));
        }

        public object Clone (object value, Type targetType) {
            object temp = Serialize(value, true);
            return Deserialize(temp, targetType);
        }

        private bool IsScalar (object value) {
            // same types as the primitives written out in serialize()
            return value is Int32 || value is Double || value is Boolean || value is String;
        }

        private Type FindTypeByName (string name, bool ignoreCase = false, bool cache = true) {
            // see if we need to convert from shorthand notation or not 
            if (name.Contains("-")) {
                name = MakeCamelCaseTypeName(name);
            }

            // do we have it cached?
            Type result = null;
            if (_ExplicitlyNamedTypes.TryGetValue(name, out result)) {
                return result;
            }

            // search for it the hard way
            Type type = FindTypeIncludingImplicits(name, ignoreCase);
            if (type != null) {
                if (cache) {
                    this._ExplicitlyNamedTypes[name] = type;
                }
                return type;
            }

            if (ThrowErrorOnUnknownTypes) {
                throw new Exception("Serializer could not find type information for " + name);
            }

            return null;
        }

        private string MakeCamelCaseTypeName (string name) {
            // split on dash, capitalize each segment, then squish back together
            string[] segments = name.Split('-');
            for (int i = 0; i < segments.Length; ++i) { segments[i] = UpcaseFirstLetter(segments[i]); }
            return string.Join("", segments);
        }

        private string UpcaseFirstLetter (string str) {
            return
                (str == null) ? null :
                (str.Length == 1) ? str.ToUpper() :
                (char.ToUpper(str[0]) + str.Substring(1));
        }

        private Type FindTypeIncludingImplicits (string name, bool ignoreCase) {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            
            // first try to find based on explicit name
            Type type = FindInAllAssemblies(name, ignoreCase, assemblies);
            if (type != null) {
                return type;
            }

            // otherwise try all implicit namespaces in order
            foreach (NSDef def in _ImplicitNamespaces) {
                string longName = def.prefix + (def.isclass ? "+" : ".") + name;
                type = FindInAllAssemblies(longName, ignoreCase, assemblies);
                if (type != null) {
                    return type;
                }
            }

            return null;
        }

        private Type FindInAllAssemblies (string name, bool ignoreCase, Assembly[] assemblies) {
            foreach (var assembly in assemblies) {
                Type type = assembly.GetType(name, false, ignoreCase);
                if (type != null) {
                    return type;
                }
            }
            return null;
        }

        private bool IsExplicitlyTypedHashtable (object value) {
            var table = value as Hashtable;
            return table != null && table.ContainsKey(TYPEKEY);
        }

        private Type InferType (object value) {
            var table = value as Hashtable;

            // if it's a hashtable, see if we have the magical type marker
            if (IsExplicitlyTypedHashtable(value)) {
                // manufacture the type
                string typeName = table[TYPEKEY] as string;
                Type explicitType = FindTypeByName(typeName);
                if (explicitType != null) {
                    return explicitType;
                }
            } 
            
            if (table != null) {
                // either the type wasn't specified, or it's unknown. treat it as a dictionary
                return typeof(Hashtable);
            } 
            
            if (value is ArrayList) {
                return typeof(ArrayList);
            }

            // it's a scalar, no type
            return null;
        }

        //
        //
        // INSTANCE CACHE

        private bool HasDefaultInstance (object o) {
            return (o != null) && HasDefaultInstance(o.GetType());
        }

        private bool HasDefaultInstance (Type t) {
            return _DefaultInstances.ContainsKey(t);
        }

        private object GetDefaultInstanceValue (object o, MemberInfo field) {
            if (o == null) {
                return null;
            }

            Type t = o.GetType();
            object instance = null;
            if (!_DefaultInstances.ContainsKey(t)) {
                instance = _DefaultInstances[t] = CreateInstance(t, 0);
            } else {
                instance = _DefaultInstances[t];
            }

            return TypeUtils.GetValue(field, instance);
        }

        //
        //
        // CUSTOM DESERIALIZATION 

        public void RegisterFactory (Type type, Func<object> factory) {
            _InstanceFactories[type] = factory;
        }

        public void UnregisterFactory (Type type) {
            _InstanceFactories.Remove(type);
        }

        private object CreateInstance (Type type, int length) {
            if (_InstanceFactories.ContainsKey(type)) {
                return (_InstanceFactories[type]).Invoke();
            } 

            if (type.IsArray) {
                return Array.CreateInstance(type.GetElementType(), length);
            }

            return Activator.CreateInstance(type);
        }

        //
        //
        // IMPLICIT NAMESPACES

        public void AddImplicitNamespace (string prefix, bool isNamespace = true) {
            _ImplicitNamespaces.Add(new NSDef() { prefix = prefix, isclass = !isNamespace });
        }

        public void RemoveImplicitNamespace (string prefix) {
            _ImplicitNamespaces.RemoveAll((NSDef def) => def.prefix == prefix);
        }
    }
}
